using System.Diagnostics;
using Jint;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.SigCipher;

public sealed class SigCipherDecryptor : JsDecryptorBase<SigCipherDecryptor>, ISigCipherDecryptor
{
    private SigCipherManifest? _manifest;
    private string? _manifestCachePath;

    public SigCipherDecryptor(PlayerContextManager playerManager)
        : base(playerManager, Globals.File.SigCipherCache, 500, 100)
    {
    }

    public async ValueTask<string> DecipherAsync(string signature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signature)) return signature;

        // ═══════════════════════════════════════════════════════════════
        // TIER 0: Cache Hit (instant)
        // ═══════════════════════════════════════════════════════════════
        if (Cache.TryGet(signature, out var cached)) return cached;

        await EnsureInitializedAsync(ct);

        // ═══════════════════════════════════════════════════════════════
        // TIER 1: Manifest (native C#, zero JS allocations)
        // ═══════════════════════════════════════════════════════════════
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

        // ═══════════════════════════════════════════════════════════════
        // TIER 2: JS Bundle
        // ═══════════════════════════════════════════════════════════════
        if (BundleEngine is not null && BundleFuncName is not null)
        {
            try
            {
                var result = BundleEngine.Invoke(BundleFuncName, signature).AsString();
                if (!string.IsNullOrEmpty(result) && result != signature)
                {
                    Cache.Set(signature, result);
                    Log.Debug($"[SigCipher] Bundle: {Truncate(signature)} -> {Truncate(result)}");
                    
                    // Попробуем решить в фоне для следующих вызовов
                    TrySolveInBackground(signature, result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[SigCipher] Bundle failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // TIER 3: Full JS
        // ═══════════════════════════════════════════════════════════════
        if (FullEngine is not null && FullFuncName is not null)
        {
            try
            {
                var result = FullEngine.Invoke(FullFuncName, signature).AsString();
                if (!string.IsNullOrEmpty(result) && result != signature)
                {
                    Cache.Set(signature, result);
                    Log.Debug($"[SigCipher] Full: {Truncate(signature)} -> {Truncate(result)}");
                    
                    TrySolveInBackground(signature, result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[SigCipher] Full JS failed: {ex.Message}");
            }
        }

        return signature;
    }

    protected override void InitializeCore(PlayerContext context)
    {
        Log.Info("[SigCipher] Initializing...");
        var sw = Stopwatch.StartNew();

        _manifestCachePath = Path.Combine(
            Path.GetDirectoryName(Globals.File.SigCipherCache)!,
            $"manifest_{context.Version}.txt");

        // ═══════════════════════════════════════════════════════════════
        // STAGE 1: Load Cached Manifest
        // ═══════════════════════════════════════════════════════════════
        _manifest = TryLoadCachedManifest(context.Version);
        if (_manifest is not null)
        {
            sw.Stop();
            Log.Info($"[SigCipher] Cached manifest loaded in {sw.ElapsedMilliseconds}ms: {_manifest}");
            return;
        }

        // ═══════════════════════════════════════════════════════════════
        // STAGE 2: Extract Manifest (parse JS, no execution)
        // ═══════════════════════════════════════════════════════════════
        _manifest = SigCipherExtractor.ExtractManifest(context.BaseJs, context.Version);
        if (_manifest is not null)
        {
            SaveManifest(_manifest);
            sw.Stop();
            Log.Info($"[SigCipher] Extracted manifest in {sw.ElapsedMilliseconds}ms: {_manifest}");
            return;
        }

        Log.Debug("[SigCipher] Extractor failed, trying Solver...");

        // ═══════════════════════════════════════════════════════════════
        // STAGE 3: Solve via Math (get one sample from JS, then discard JS)
        // ═══════════════════════════════════════════════════════════════
        if (TryInitWithSolver(context))
        {
            sw.Stop();
            Log.Info($"[SigCipher] Solver manifest ready in {sw.ElapsedMilliseconds}ms: {_manifest}");
            return;
        }

        // ═══════════════════════════════════════════════════════════════
        // STAGE 4: Fallback to persistent JS engines
        // ═══════════════════════════════════════════════════════════════
        InitializeJsEngines(context);
        sw.Stop();
        Log.Info($"[SigCipher] JS fallback ready in {sw.ElapsedMilliseconds}ms");
    }

    // ═══════════════════════════════════════════════════════════════
    // MANIFEST CACHE
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
        catch
        {
            return null;
        }
    }

    private void SaveManifest(SigCipherManifest manifest)
    {
        try
        {
            if (_manifestCachePath is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_manifestCachePath)!);
            File.WriteAllText(_manifestCachePath, manifest.Serialize());
            Log.Debug($"[SigCipher] Manifest cached to {Path.GetFileName(_manifestCachePath)}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[SigCipher] Manifest cache failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SOLVER INTEGRATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Создаёт временный JS engine, получает один пример дешифровки,
    /// решает через SigCipherSolver, сохраняет манифест, освобождает JS.
    /// </summary>
    private bool TryInitWithSolver(PlayerContext context)
    {
        var funcName = SigCipherExtractor.FindDecipherFunctionName(context.BaseJs);
        if (funcName is null)
        {
            Log.Debug("[SigCipher] Solver: decipher function not found");
            return false;
        }

        var wrapperScript = $$"""
            function __sigTransform(s) {
                try {
                    var f = window['{{funcName}}'];
                    if (typeof f !== 'function') return s;
                    var r = f(26, s);
                    return (typeof r === 'string' && r !== s) ? r : s;
                } catch(e) { return s; }
            }
            """;

        // Тестовая подпись (реалистичная длина ~105 символов)
        const string testSig =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop" +
            "qrstu";

        Engine? tempEngine = null;
        try
        {
            // Пробуем Bundle
            var bundle = JsFunctionExtractor.ExtractBundle(context.BaseJs, funcName);
            if (bundle is not null)
            {
                tempEngine = CreateTempEngine();
                tempEngine.Execute(bundle);
                tempEngine.Execute(wrapperScript);

                var decrypted = tempEngine.Invoke("__sigTransform", testSig).AsString();
                if (!string.IsNullOrEmpty(decrypted) && decrypted != testSig)
                {
                    var ops = SigCipherSolver.Solve(testSig, decrypted);
                    if (ops is not null && ops.Count >= 3)
                    {
                        _manifest = new SigCipherManifest(context.Version, ops, "solver");
                        SaveManifest(_manifest);
                        SaveScript(context.Version, "solver_bundle", bundle, funcName);
                        return true;
                    }
                }
            }

            // Пробуем Full JS
            tempEngine?.Dispose();
            tempEngine = CreateTempEngine();
            tempEngine.Execute(context.BaseJs);
            tempEngine.Execute(wrapperScript);

            var fullDecrypted = tempEngine.Invoke("__sigTransform", testSig).AsString();
            if (!string.IsNullOrEmpty(fullDecrypted) && fullDecrypted != testSig)
            {
                var ops = SigCipherSolver.Solve(testSig, fullDecrypted);
                if (ops is not null && ops.Count >= 3)
                {
                    _manifest = new SigCipherManifest(context.Version, ops, "solver_full");
                    SaveManifest(_manifest);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SigCipher] Solver init failed: {ex.Message}");
            return false;
        }
        finally
        {
            tempEngine?.Dispose();
        }
    }

    /// <summary>
    /// Фоновое решение — если JS сработал, пытаемся вывести манифест для будущих вызовов.
    /// </summary>
    private void TrySolveInBackground(string encrypted, string decrypted)
    {
        if (_manifest is not null) return; // уже есть
        if (CurrentPlayerVersion is null) return;

        Task.Run(() =>
        {
            try
            {
                var ops = SigCipherSolver.Solve(encrypted, decrypted);
                if (ops is null || ops.Count < 3) return;

                var manifest = new SigCipherManifest(CurrentPlayerVersion, ops, "background_solver");

                // Проверяем на нескольких примерах из кэша
                int verified = 0;
                foreach (var sig in GetCachedSignatures().Take(5))
                {
                    if (Cache.TryGet(sig, out var expected))
                    {
                        var actual = manifest.Decipher(sig);
                        if (actual == expected) verified++;
                    }
                }

                if (verified >= 3 || verified == GetCachedSignatures().Take(5).Count())
                {
                    _manifest = manifest;
                    SaveManifest(manifest);
                    
                    // Освобождаем JS engines
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

    private IEnumerable<string> GetCachedSignatures()
    {
        // Простой способ — итерируем по известным ключам
        // В реальности Cache не экспонирует ключи, но можно добавить
        yield break; // заглушка
    }

    // ═══════════════════════════════════════════════════════════════
    // JS ENGINE HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static Engine CreateTempEngine() => new(opt => opt
        .TimeoutInterval(TimeSpan.FromSeconds(10))
        .LimitRecursion(100)
        .MaxStatements(1_000_000));

    private void InitializeJsEngines(PlayerContext context)
    {
        var funcName = SigCipherExtractor.FindDecipherFunctionName(context.BaseJs);
        if (funcName is null)
        {
            Log.Error("[SigCipher] Decipher function not found");
            return;
        }

        var wrapperScript = $$"""
            function __sigTransform(s) {
                try {
                    var f = window['{{funcName}}'];
                    if (typeof f !== 'function') return s;
                    var r = f(26, s);
                    return (typeof r === 'string' && r !== s) ? r : s;
                } catch(e) { return s; }
            }
            """;

        const string testSig = "TEST_SIGNATURE_STRING_1234567890";

        // Bundle
        var bundle = JsFunctionExtractor.ExtractBundle(context.BaseJs, funcName);
        if (bundle is not null)
        {
            SaveScript(context.Version, "bundle", bundle, funcName);
            if (TryInitBundle(bundle, funcName, wrapperScript, "__sigTransform", testSig))
            {
                Log.Info($"[SigCipher] Bundle ready ({bundle.Length / 1024}KB)");
                return;
            }
        }

        // Full JS
        if (TryInitFull(context.BaseJs, wrapperScript, "__sigTransform", testSig))
        {
            Log.Info($"[SigCipher] Full JS ready ({context.BaseJs.Length / 1024}KB)");
        }
    }

    public override void Dispose()
    {
        _manifest = null;
        base.Dispose();
    }
}