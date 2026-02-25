using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

public sealed partial class NTokenDecryptor(PlayerContextManager playerManager) : JsDecryptorBase<NTokenDecryptor>(playerManager, G.FilePath.NTokenCache, 2000, 500)
{
    public async ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nToken)) return nToken;
        if (Cache.TryGet(nToken, out var cached)) return cached;

        await EnsureInitializedAsync(ct);

        var result = TryInvokeJs(nToken, "NToken");
        return result ?? nToken;
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

        Log.Debug($"[NToken] Found entry function: {funcName}");

        const string testToken = "WDZxqubC-kfdqV5cl60";

        var success = TryInitJsEngines(
            context,
            funcName,
            BuildNTokenWrapperScript,
            testToken,
            BuildNTokenBundle);

        sw.Stop();
        if (success)
            Log.Info($"[NToken] Ready in {sw.ElapsedMilliseconds}ms");
        else
            Log.Error($"[NToken] All init strategies failed after {sw.ElapsedMilliseconds}ms");
    }

    private static string BuildNTokenWrapperScript(string funcName) => $$"""
        function __decryptorTransform(n) {
            try {
                var f = window['{{funcName}}'];
                if (typeof f !== 'function') return n;
                var r = f(n);
                return (typeof r === 'string' && r !== n) ? r : n;
            } catch(e) { return n; }
        }
        """;

    /// <summary>
    /// Кастомный builder бандла для NToken.
    /// Использует общий BuildDefaultBundle из базового класса.
    /// </summary>
    private string? BuildNTokenBundle(string baseJs, string funcName) =>
        BuildDefaultBundle(baseJs, funcName);

    // ═══════════════════════════════════════════════════════════════
    // FUNCTION NAME DISCOVERY
    // ═══════════════════════════════════════════════════════════════

    private static string? FindNTokenFunctionName(string baseJs)
    {
        return FindBySelfReferences(baseJs)
            ?? FindByWrapperArray(baseJs)
            ?? FindByEnhancedPattern(baseJs)
            ?? FindByNumericMarker(baseJs);
    }

    private static string? FindBySelfReferences(string baseJs)
    {
        var match = SelfReferencesRegex().Match(baseJs);
        if (!match.Success) return null;

        var arrayName = match.Groups[1].Value;
        Log.Debug($"[NToken] Self-ref array '{arrayName}' at position {match.Index}");

        var containingFunc = FindContainingFunction(baseJs, match.Index);
        if (containingFunc is null) return null;

        Log.Debug($"[NToken] Containing function: {containingFunc}");

        var wrapper = FindWrapperFor(baseJs, containingFunc);
        var result = wrapper ?? containingFunc;
        Log.Debug($"[NToken] Found via self-references: {result}");
        return result;
    }

    private static string? FindByWrapperArray(string baseJs)
    {
        var wrapperRegex = WrapperFunctionRegex();
        foreach (System.Text.RegularExpressions.Match match in wrapperRegex.Matches(baseJs))
        {
            var wrapperName = match.Groups[1].Value;
            var targetFunc = match.Groups[2].Value;

            var arrayPattern = $@"(\w+)\s*=\s*\[\s*{System.Text.RegularExpressions.Regex.Escape(wrapperName)}\s*\]";
            var arrayMatch = System.Text.RegularExpressions.Regex.Match(baseJs, arrayPattern);

            if (arrayMatch.Success)
            {
                var arrayName = arrayMatch.Groups[1].Value;
                var usagePattern = $@"{System.Text.RegularExpressions.Regex.Escape(arrayName)}\s*\[\s*0\s*\]\s*\(";
                if (System.Text.RegularExpressions.Regex.IsMatch(baseJs, usagePattern))
                {
                    Log.Debug($"[NToken] Wrapper array: {arrayName} = [{wrapperName}], target: {targetFunc}");
                    return wrapperName;
                }
            }
        }
        return null;
    }

    private static string? FindByEnhancedPattern(string baseJs)
    {
        var candidates = new List<(string Name, int Score)>();

        var funcRegex = FunctionWithSplitJoinRegex();
        foreach (System.Text.RegularExpressions.Match match in funcRegex.Matches(baseJs))
        {
            var funcName = match.Groups[1].Value;
            var funcBody = match.Value;

            int score = 0;
            if (funcBody.Contains("split")) score += 2;
            if (funcBody.Contains("join")) score += 2;
            if (System.Text.RegularExpressions.Regex.IsMatch(funcBody, @"typeof\s+\w+\s*===")) score += 1;
            if (System.Text.RegularExpressions.Regex.IsMatch(funcBody, @"\[\s*\d+\s*\]\s*=")) score += 1;
            if (funcBody.Contains("null")) score += 1;
            if (funcBody.Length > 500) score += 1;

            if (score >= 4) candidates.Add((funcName, score));
        }

        var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        if (best.Name is not null && best.Score >= 4)
        {
            var wrapper = FindWrapperFor(baseJs, best.Name);
            return wrapper ?? best.Name;
        }
        return null;
    }

    private static string? FindByNumericMarker(string baseJs)
    {
        string[] markers = ["-1552975130", "1673840063", "1630572004"];

        foreach (var marker in markers)
        {
            int markerIdx = baseJs.IndexOf(marker, StringComparison.Ordinal);
            if (markerIdx < 0) continue;

            var contextStart = Math.Max(0, markerIdx - 5000);
            var context = baseJs.Substring(contextStart, markerIdx - contextStart);

            string? lastName = null;
            foreach (System.Text.RegularExpressions.Match m in FunctionDefinitionRegex().Matches(context))
                lastName = m.Groups[1].Value;

            if (lastName is not null)
            {
                var wrapper = FindWrapperFor(baseJs, lastName);
                return wrapper ?? lastName;
            }
        }
        return null;
    }

    private static string? FindContainingFunction(string js, int position)
    {
        var searchStart = Math.Max(0, position - 10000);
        var context = js.Substring(searchStart, position - searchStart);

        string? lastName = null;
        int lastPos = -1;

        foreach (System.Text.RegularExpressions.Match m in FunctionDefinitionRegex().Matches(context))
        {
            if (m.Index > lastPos)
            {
                lastName = m.Groups[1].Value;
                lastPos = m.Index;
            }
        }
        return lastName;
    }

    private static string? FindWrapperFor(string js, string targetFunc)
    {
        var patterns = new[]
        {
            $@"(\w+)\s*=\s*function\s*\(\s*(\w+)\s*\)\s*\{{\s*return\s+{System.Text.RegularExpressions.Regex.Escape(targetFunc)}\s*\[\s*\w+\s*\[\s*\d+\s*\]\s*\]\s*\(\s*this\s*,\s*\d+\s*,\s*\2\s*\)",
            $@"(\w+)\s*=\s*function\s*\(\s*(\w+)\s*\)\s*\{{\s*return\s+{System.Text.RegularExpressions.Regex.Escape(targetFunc)}\s*\.\s*call\s*\(\s*this\s*,\s*\d+\s*,\s*\2\s*\)"
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(js, pattern);
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // GENERATED REGEX
    // ═══════════════════════════════════════════════════════════════

    [System.Text.RegularExpressions.GeneratedRegex(@"(\w+)\[\d+\]\s*=\s*\1\s*;\s*\1\[\d+\]\s*=\s*\1\s*;\s*\1\[\d+\]\s*=\s*\1", System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex SelfReferencesRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(\w+)\s*=\s*function\s*\(\s*\w+\s*\)\s*\{\s*return\s+(\w+)\s*\[", System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex WrapperFunctionRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(\w+)\s*=\s*function\s*\([^)]*\)\s*\{(?:[^{}]|\{[^{}]*\}){200,}?(?:split|join)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex FunctionWithSplitJoinRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:^|[;\n}])([a-zA-Z_$][\w$]*)\s*=\s*function\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex FunctionDefinitionRegex();
}