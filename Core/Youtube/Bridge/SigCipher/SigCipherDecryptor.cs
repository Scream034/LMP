using System.Diagnostics;
using Jint;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.SigCipher;

public sealed partial class SigCipherDecryptor(PlayerContextManager playerManager) : JsDecryptorBase<SigCipherDecryptor>(playerManager, G.FilePath.SigCipherCache, 500, 100)
{
    private SigCipherManifest? _manifest;
    private string? _manifestCachePath;

    public async ValueTask<string> DecipherAsync(string signature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signature)) return signature;
        if (Cache.TryGet(signature, out var cached)) return cached;

        await EnsureInitializedAsync(ct);

        // Tier 1: Manifest (native C#, zero JS)
        if (_manifest is not null)
        {
            try
            {
                var result = _manifest.Decipher(signature);
                Cache.Set(signature, result);
                Log.Debug($"[SigCipher] Manifest: {Truncate(signature)} -> {Truncate(result)}");
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn($"[SigCipher] Manifest failed: {ex.Message}");
            }
        }

        // Tier 2+3: JS (bundle → full)
        var jsResult = TryInvokeJs(signature, "Decipher");
        if (jsResult is not null)
        {
            TrySolveInBackground(signature, jsResult);
            return jsResult;
        }

        return signature;
    }

    protected override void InitializeCore(PlayerContext context)
    {
        Log.Info("[SigCipher] Initializing...");
        var sw = Stopwatch.StartNew();

        _manifestCachePath = Path.Combine(DiagFolder, $"manifest_{context.Version}.txt");

        // Stage 1: Cached manifest
        _manifest = TryLoadCachedManifest(context.Version);
        if (_manifest is not null)
        {
            sw.Stop();
            Log.Info($"[SigCipher] Cached manifest loaded in {sw.ElapsedMilliseconds}ms: {_manifest}");
            return;
        }

        // ═══ Find function name early — needed by multiple stages ═══
        var funcName = SigCipherExtractor.FindDecipherFunctionName(context.BaseJs);

        // Stage 2: Extract manifest (parse JS, no execution)
        _manifest = SigCipherExtractor.ExtractManifest(context.BaseJs, context.Version);
        if (_manifest is not null)
        {
            SaveManifest(_manifest);

            // Save diagnostic bundle even though we don't need JS engine
            if (funcName is not null)
                SaveDiagBundle(context.BaseJs, funcName);

            sw.Stop();
            Log.Info($"[SigCipher] Extracted manifest in {sw.ElapsedMilliseconds}ms: {_manifest}");
            return;
        }

        Log.Debug("[SigCipher] Extractor failed, trying Solver...");

        // Stage 3: Solver (get one sample from JS, then discard JS)
        if (TryInitWithSolver(context, funcName))
        {
            sw.Stop();
            Log.Info($"[SigCipher] Solver manifest ready in {sw.ElapsedMilliseconds}ms: {_manifest}");
            return;
        }

        // Stage 4: Persistent JS engines
        if (funcName is not null)
        {
            var callNumber = FindCallNumber(context.BaseJs, funcName);
            TryInitJsEngines(
                context,
                funcName,
                fn => BuildSigWrapperScript(fn, callNumber),
                BuildTestSignature(),
                BuildSigBundle);
        }

        sw.Stop();
        Log.Info($"[SigCipher] Initialized in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Saves diagnostic bundle for analysis even when manifest extraction succeeded.
    /// </summary>
    private void SaveDiagBundle(string baseJs, string funcName)
    {
        try
        {
            var bundle = BuildSigBundle(baseJs, funcName);
            if (bundle is not null)
                SaveDiagScript("bundle", bundle, funcName);
        }
        catch (Exception ex)
        {
            Log.Debug($"[SigCipher] Diag bundle save failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WRAPPER SCRIPT & BUNDLE
    // ═══════════════════════════════════════════════════════════════

    private static string BuildSigWrapperScript(string funcName, int? callNumber) => $$"""
        function __decryptorTransform(s) {
            try {
                var f = window['{{funcName}}'];
                if (typeof f !== 'function') return s;
                var r;
                {{(callNumber.HasValue
                    ? $"r = f({callNumber.Value}, s);"
                    : """
                      var nums = [4, 26, 2, 8, 14];
                      for (var i = 0; i < nums.length; i++) {
                          try {
                              r = f(nums[i], s);
                              if (typeof r === 'string' && r !== s && r.length > 0) break;
                              r = null;
                          } catch(e2) { r = null; }
                      }
                      if (!r) {
                          try { r = f(s); } catch(e3) { r = null; }
                      }
                      """)}}
                return (typeof r === 'string' && r !== s && r.length > 0) ? r : s;
            } catch(e) { return s; }
        }
        """;

    private static int? FindCallNumber(string baseJs, string funcName)
    {
        var span = baseJs.AsSpan();
        var target = string.Concat(funcName, "(");
        var targetSpan = target.AsSpan();

        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int idx = span[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (idx < 0) break;
            idx += searchFrom;
            searchFrom = idx + targetSpan.Length;

            if (idx > 0 && (char.IsLetterOrDigit(span[idx - 1]) || span[idx - 1] is '_' or '$'))
                continue;

            int pos = idx + targetSpan.Length;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

            int numStart = pos;
            while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
            if (pos == numStart) continue;

            int afterNum = pos;
            while (afterNum < span.Length && span[afterNum] is ' ' or '\t') afterNum++;
            if (afterNum >= span.Length || span[afterNum] != ',') continue;

            if (int.TryParse(span.Slice(numStart, pos - numStart), out int num))
            {
                Log.Debug($"[SigCipher] Found call number {num} for '{funcName}'");
                return num;
            }
        }

        return null;
    }

    private string? BuildSigBundle(string baseJs, string funcName) =>
        BuildDefaultBundle(baseJs, funcName);

    private static string BuildTestSignature() =>
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop" +
        "qrstu";

    // ═══════════════════════════════════════════════════════════════
    // MANIFEST
    // ═══════════════════════════════════════════════════════════════

    private SigCipherManifest? TryLoadCachedManifest(string playerVersion)
    {
        try
        {
            if (_manifestCachePath is null || !File.Exists(_manifestCachePath))
                return null;

            var data = File.ReadAllText(_manifestCachePath);
            var manifest = SigCipherManifest.Deserialize(data);

            if (manifest?.PlayerVersion != playerVersion)
            {
                File.Delete(_manifestCachePath);
                return null;
            }

            return manifest;
        }
        catch { return null; }
    }

    private void SaveManifest(SigCipherManifest manifest)
    {
        try
        {
            if (_manifestCachePath is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_manifestCachePath)!);
            File.WriteAllText(_manifestCachePath, manifest.Serialize());
        }
        catch (Exception ex)
        {
            Log.Debug($"[SigCipher] Manifest save failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SOLVER
    // ═══════════════════════════════════════════════════════════════

    private bool TryInitWithSolver(PlayerContext context, string? funcName)
    {
        if (funcName is null) return false;

        var callNumber = FindCallNumber(context.BaseJs, funcName);
        var testSig = BuildTestSignature();
        string? decrypted = null;

        // Попытка через bundle
        var bundle = BuildSigBundle(context.BaseJs, funcName);
        if (bundle is not null)
        {
            decrypted = TryRunOnce(bundle, funcName, testSig, callNumber);
            if (decrypted is not null)
                SaveDiagScript("solver_bundle", bundle, funcName);
        }

        // Попытка через full JS
        if (decrypted is null)
        {
            var modifiedJs = InjectWindowExport(context.BaseJs, funcName);
            decrypted = TryRunOnce(modifiedJs, funcName, testSig, callNumber);
        }

        if (decrypted is null) return false;

        var ops = SigCipherSolver.Solve(testSig, decrypted);
        if (ops is null || ops.Count < 3) return false;

        _manifest = new SigCipherManifest(context.Version, ops, "solver");
        SaveManifest(_manifest);
        return true;
    }

    private static string? TryRunOnce(string jsCode, string funcName, string testInput, int? callNumber)
    {
        Engine? engine = null;
        try
        {
            engine = new Engine(opt => opt
                .TimeoutInterval(TimeSpan.FromSeconds(10))
                .LimitRecursion(100)
                .MaxStatements(1_000_000));

            engine.Execute(BrowserStubs);
            engine.Execute(jsCode);

            var wrapperScript = BuildSigWrapperScript(funcName, callNumber);
            engine.Execute(wrapperScript);

            var result = engine.Invoke("__decryptorTransform", testInput).AsString();
            return !string.IsNullOrEmpty(result) && result != testInput ? result : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SigCipher] TryRunOnce failed: {ex.Message}");
            return null;
        }
        finally
        {
            engine?.Dispose();
        }
    }

    private void TrySolveInBackground(string encrypted, string decrypted)
    {
        if (_manifest is not null || CurrentPlayerVersion is null) return;

        Task.Run(() =>
        {
            try
            {
                var ops = SigCipherSolver.Solve(encrypted, decrypted);
                if (ops is null || ops.Count < 3) return;

                var manifest = new SigCipherManifest(CurrentPlayerVersion, ops, "background_solver");

                int verified = 0, total = 0;
                foreach (var sig in Cache.Keys.Take(5))
                {
                    if (Cache.TryGet(sig, out var expected))
                    {
                        total++;
                        if (manifest.Decipher(sig) == expected) verified++;
                    }
                }

                if (verified >= 3 || (total > 0 && verified == total))
                {
                    _manifest = manifest;
                    SaveManifest(manifest);

                    BundleEngine?.Dispose();
                    BundleEngine = null;
                    FullEngine?.Dispose();
                    FullEngine = null;

                    Log.Info($"[SigCipher] Background solver succeeded: {manifest}");
                }
            }
            catch { /* ignore */ }
        });
    }

    public override void Dispose()
    {
        _manifest = null;
        base.Dispose();
    }
}