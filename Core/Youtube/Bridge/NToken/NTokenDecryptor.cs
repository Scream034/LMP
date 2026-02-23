using Jint;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

public sealed class NTokenDecryptor : JsDecryptorBase<NTokenDecryptor>, INTokenDecryptor
{
    public NTokenDecryptor(PlayerContextManager playerManager) 
        : base(playerManager, Globals.File.NTokenCache, 2000, 500)
    {
    }

    public async ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nToken)) return nToken;

        if (Cache.TryGet(nToken, out var cached)) return cached;

        await EnsureInitializedAsync(ct);

        // 1. Bundle
        if (BundleEngine is not null && BundleFuncName is not null)
        {
            try
            {
                var result = BundleEngine.Invoke(BundleFuncName, nToken).AsString();
                if (!string.IsNullOrEmpty(result) && result != nToken)
                {
                    Cache.Set(nToken, result);
                    Log.Debug($"[NToken] Bundle: {Truncate(nToken)} -> {Truncate(result)}");
                    return result;
                }
            }
            catch (Exception ex) { Log.Warn($"[NToken] Bundle failed: {ex.Message}"); }
        }

        // 2. Full JS
        if (FullEngine is not null && FullFuncName is not null)
        {
            try
            {
                var result = FullEngine.Invoke(FullFuncName, nToken).AsString();
                if (!string.IsNullOrEmpty(result) && result != nToken)
                {
                    Cache.Set(nToken, result);
                    Log.Debug($"[NToken] Full: {Truncate(nToken)} -> {Truncate(result)}");
                    return result;
                }
            }
            catch (Exception ex) { Log.Error($"[NToken] Full JS failed: {ex.Message}"); }
        }

        return nToken;
    }

    protected override void InitializeCore(PlayerContext context)
    {
        Log.Info("[NToken] Initializing...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var funcName = FindNTokenFunctionName(context.BaseJs);
        if (funcName is null)
        {
            Log.Error("[NToken] n-token function not found");
            return;
        }

        var wrapperScript = $$"""
            function __nTokenTransform(n) {
                try {
                    var f = window['{{funcName}}'];
                    if (typeof f !== 'function') return n;
                    var r = f(76, n) || f(5, n) || f(n);
                    return (typeof r === 'string' && r !== n) ? r : n;
                } catch(e) { return n; }
            }
            """;
            
        const string testToken = "WDZxqubC-kfdqV5cl60";

        // 1. Попытка 1: Bundle
        var bundle = JsFunctionExtractor.ExtractBundle(context.BaseJs, funcName);
        if (bundle is not null && TryInitBundle(bundle, funcName, wrapperScript, "__nTokenTransform", testToken))
        {
            sw.Stop();
            Log.Info($"[NToken] Bundle ready in {sw.ElapsedMilliseconds}ms ({bundle.Length / 1024}KB)");
            return;
        }

        Log.Debug("[NToken] Bundle failed, using full JS...");

        // 2. Попытка 2: Full JS
        if (TryInitFull(context.BaseJs, wrapperScript, "__nTokenTransform", testToken))
        {
            sw.Stop();
            Log.Info($"[NToken] Full JS ready in {sw.ElapsedMilliseconds}ms ({context.BaseJs.Length / 1024}KB)");
        }
    }

    private static string? FindNTokenFunctionName(string baseJs)
    {
        const string primaryMarker = "-1552975130";
        int markerIdx = baseJs.IndexOf(primaryMarker, StringComparison.Ordinal);
        if (markerIdx < 0) return null;

        var contextStart = Math.Max(0, markerIdx - 5000);
        var context = baseJs.Substring(contextStart, markerIdx - contextStart);

        string? lastName = null;
        foreach (System.Text.RegularExpressions.Match m in 
                 System.Text.RegularExpressions.Regex.Matches(context, @"(?:^|[;\n}])([a-zA-Z_$][\w$]*)\s*=\s*function\s*\("))
        {
            lastName = m.Groups[1].Value;
        }

        return lastName;
    }
}