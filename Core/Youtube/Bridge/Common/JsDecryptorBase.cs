using System.Text;
using Jint;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Базовый класс для JS-дешифраторов YouTube.
/// <para>
/// Оптимизированный жизненный цикл Jint Engine: 
/// движок инициализируется единожды, а фаззинг (перебор кандидатов)
/// переопределяет лишь окно вызова <c>__decryptorTransform</c>.
/// </para>
/// </summary>
public abstract class JsDecryptorBase<T>(
    PlayerContextManager playerManager,
    string cacheFilePath,
    int maxMemory,
    int maxDisk) : IYoutubeDecryptor, IDisposable
{
    protected readonly PlayerContextManager PlayerManager = playerManager;
    protected readonly DecryptorCache<string, string> Cache = new(cacheFilePath, maxMemory, maxDisk);

    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    protected Engine? BundleEngine;
    protected string? BundleFuncName;
    protected Engine? FullEngine;
    protected string? FullFuncName;

    protected string? CurrentPlayerVersion;
    internal volatile bool IsInitialized;

    protected string DecryptorName => typeof(T).Name;
    protected string DiagFolder => Cache.CacheFolder;

    public async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (IsInitialized) return;

        await _initSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;

            var context = await PlayerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
            CurrentPlayerVersion = context.Version;
            await Cache.LoadAsync(context.Version).ConfigureAwait(false);

            try
            {
                InitializeCore(context);
                IsInitialized = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[{DecryptorName}] Initialization failed: {ex.Message}");
            }
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    protected abstract void InitializeCore(PlayerContext context);

    protected static Engine CreateEngine(TimeSpan timeout, int maxStatements) =>
        new(opt => opt.TimeoutInterval(timeout).LimitRecursion(200).MaxStatements(maxStatements));

    protected static Engine CreateBundleEngine() => CreateEngine(TimeSpan.FromSeconds(15), 2_000_000);
    protected static Engine CreateFullEngine() => CreateEngine(TimeSpan.FromSeconds(30), 5_000_000);
    protected static Engine CreateSieveEngine() => CreateEngine(TimeSpan.FromSeconds(30), 10_000_000);

    /// <summary>
    /// Оптимизированный запуск: создает JS-движок ОДИН РАЗ, 
    /// прогоняет всех кандидатов за миллисекунды.
    /// </summary>
    internal bool TryInitWithCandidates(
        PlayerContext context,
        string funcName,
        List<MagicNumbers> candidates,
        Func<string, MagicNumbers, string> buildWrapperScript,
        string testInput,
        Func<string, string, string?>? buildBundle = null,
        Func<string?, string, bool>? validateResult = null)
    {
        if (candidates.Count == 0) candidates = [MagicNumbers.None];
        validateResult ??= (r, inp) => !string.IsNullOrEmpty(r) && r != inp;

        var bundleCode = buildBundle?.Invoke(context.BaseJs, funcName) ?? BuildDefaultBundle(context.BaseJs, funcName);
        if (bundleCode is not null)
        {
            if (TryCandidatesInEngine(bundleCode, funcName, candidates, buildWrapperScript, testInput, true, validateResult))
                return true;
        }

        Log.Debug($"[{DecryptorName}] Bundle failed or skipped, trying full JS...");
        var fullCode = InjectWindowExport(context.BaseJs, funcName);

        if (TryCandidatesInEngine(fullCode, funcName, candidates, buildWrapperScript, testInput, false, validateResult))
            return true;

        return false;
    }

    private bool TryCandidatesInEngine(
    string jsCode,
    string funcName,
    List<MagicNumbers> candidates,
    Func<string, MagicNumbers, string> buildWrapperScript,
    string testInput,
    bool isBundle,
    Func<string?, string, bool> validateResult)
    {
        Engine? engine = null;
        try
        {
            engine = isBundle ? CreateBundleEngine() : CreateFullEngine();
            engine.Execute(BrowserStubs);
            engine.Execute(jsCode);

            // ═══ Early termination: если 3 подряд одинаковые ошибки — стоп ═══
            string? lastJsError = null;
            int sameErrorCount = 0;
            const int maxSameErrors = 3;

            foreach (var magic in candidates)
            {
                var wrapperScript = buildWrapperScript(funcName, magic);
                engine.Execute(wrapperScript);

                string? result = null;
                try
                {
                    result = engine.Invoke("__decryptorTransform", testInput).AsString();
                }
                catch (Exception ex)
                {
                    Log.Debug($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} test crashed for {magic}: {FormatJsException(ex)}");
                    continue;
                }

                if (validateResult(result, testInput))
                {
                    if (isBundle) { BundleEngine = engine; BundleFuncName = "__decryptorTransform"; }
                    else { FullEngine = engine; FullFuncName = "__decryptorTransform"; }

                    Cache.Set(testInput, result!);
                    Log.Info($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} ready with {magic}");
                    return true;
                }

                var jsError = ReadJsError(engine);
                if (jsError is not null)
                {
                    Log.Debug($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} JS Error for {magic}: {jsError}");

                    // ═══ Подсчёт повторяющихся ошибок для early termination ═══
                    if (jsError == lastJsError)
                    {
                        sameErrorCount++;
                        if (sameErrorCount >= maxSameErrors)
                        {
                            Log.Debug($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} " +
                                      $"early termination: {sameErrorCount + 1} identical errors " +
                                      $"(skipping {candidates.Count - candidates.IndexOf(magic) - 1} remaining)");
                            break;
                        }
                    }
                    else
                    {
                        lastJsError = jsError;
                        sameErrorCount = 0;
                    }
                }
                else
                {
                    lastJsError = null;
                    sameErrorCount = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} engine init failed: {FormatJsException(ex)}");
        }

        engine?.Dispose();
        return false;
    }

    internal bool TryInitJsEngines(
        PlayerContext context,
        string funcName,
        Func<string, string> buildWrapperScript,
        string testInput,
        Func<string, string, string?>? buildBundle = null,
        Func<string?, string, bool>? validateResult = null)
    {
        return TryInitWithCandidates(context, funcName, [MagicNumbers.None], (fn, _) => buildWrapperScript(fn), testInput, buildBundle, validateResult);
    }

    protected void ForceDisposeEngines()
    {
        BundleEngine?.Dispose();
        BundleEngine = null;
        BundleFuncName = null;
        FullEngine?.Dispose();
        FullEngine = null;
        FullFuncName = null;
    }

    protected static string FormatJsException(Exception ex)
    {
        if (ex is not Jint.Runtime.JavaScriptException jsEx) return ex.Message;
        var sb = new StringBuilder(256).Append(jsEx.Message);
        try { var errorObj = jsEx.Error; if (errorObj is not null && !errorObj.IsUndefined() && !errorObj.IsNull()) sb.Append($" [JSError: {Truncate(errorObj.ToString(), 100)}]"); } catch { /* ignore */ }
        try { var jsStack = jsEx.JavaScriptStackTrace; if (!string.IsNullOrEmpty(jsStack)) sb.Append($" [JSStack: {Truncate(jsStack, 150)}]"); } catch { /* ignore */ }
        try { var loc = jsEx.Location; if (loc.Start.Line > 0) sb.Append($" [Line:{loc.Start.Line}:{loc.Start.Column}]"); } catch { /* ignore */ }
        return sb.ToString();
    }

    private static string? ReadJsError(Engine engine)
    {
        try
        {
            var errorVal = engine.GetValue("__lastError");
            if (errorVal.IsString()) return string.IsNullOrWhiteSpace(errorVal.AsString()) ? null : errorVal.AsString();
        }
        catch { /* ignore */ }
        return null;
    }

    protected string? TryInvokeJs(string input, string logPrefix)
    {
        if (BundleEngine is not null && BundleFuncName is not null)
        {
            try
            {
                var result = BundleEngine.Invoke(BundleFuncName, input).AsString();
                if (!string.IsNullOrEmpty(result) && result != input)
                {
                    Cache.Set(input, result);
                    Log.Debug($"[{DecryptorName}] {logPrefix} Bundle: {Truncate(input)} -> {Truncate(result)}");
                    return result;
                }
            }
            catch (Exception ex) { Log.Warn($"[{DecryptorName}] {logPrefix} Bundle failed: {FormatJsException(ex)}"); }
        }

        if (FullEngine is not null && FullFuncName is not null)
        {
            try
            {
                var result = FullEngine.Invoke(FullFuncName, input).AsString();
                if (!string.IsNullOrEmpty(result) && result != input)
                {
                    Cache.Set(input, result);
                    Log.Debug($"[{DecryptorName}] {logPrefix} Full: {Truncate(input)} -> {Truncate(result)}");
                    return result;
                }
            }
            catch (Exception ex) { Log.Error($"[{DecryptorName}] {logPrefix} Full JS failed: {FormatJsException(ex)}"); }
        }
        return null;
    }

    protected static string InjectWindowExport(string baseJs, string funcName)
    {
        var export = $"\nwindow['{funcName}']={funcName};\n";
        try
        {
            var funcInfo = JsFunctionExtractor.FindFunctionByName(baseJs, funcName);
            if (funcInfo is not null)
            {
                int endOfDef = funcInfo.Value.Position + funcName.Length + 1 + funcInfo.Value.Code.Length;
                int safePos = FindSafeInsertionPoint(baseJs, endOfDef);
                return baseJs.Insert(safePos, export);
            }

            var eqPattern = $"{funcName}=";
            var baseSpan = baseJs.AsSpan();
            int searchFrom = 0;
            while (searchFrom < baseSpan.Length)
            {
                int found = baseSpan[searchFrom..].IndexOf(eqPattern.AsSpan(), StringComparison.Ordinal);
                if (found < 0) break;
                found += searchFrom;
                if (found > 0 && JsFunctionExtractor.IsIdentChar(baseSpan[found - 1])) { searchFrom = found + eqPattern.Length; continue; }
                int afterEq = found + eqPattern.Length;
                if (afterEq < baseSpan.Length && baseSpan[afterEq] == '=') { searchFrom = afterEq + 1; continue; }
                int safePos = FindSafeInsertionPointAfterDefinition(baseJs, afterEq);
                if (safePos > 0) return baseJs.Insert(safePos, export);
                searchFrom = afterEq;
            }
            return baseJs + export;
        }
        catch { return baseJs; }
    }

    private static int FindSafeInsertionPoint(string js, int afterPos)
    {
        var span = js.AsSpan();
        int i = afterPos;
        while (i < span.Length && span[i] is ' ' or '\t') i++;
        if (i < span.Length && span[i] is ';' or '}' or ',') return i + 1;
        while (i < span.Length)
        {
            char c = span[i];
            if (c is '"' or '\'' or '`') { i = JsFunctionExtractor.SkipString(js, i); continue; }
            if (c == '/' && i + 1 < span.Length)
            {
                if (span[i + 1] == '/') { i = span[i..].IndexOf('\n') >= 0 ? i + span[i..].IndexOf('\n') : span.Length; continue; }
                if (span[i + 1] == '*') { i += 2; i = span[i..].IndexOf("*/") >= 0 ? i + span[i..].IndexOf("*/") + 2 : span.Length; continue; }
            }
            if (c is ';' or '\n' or '}') return i + 1;
            i++;
        }
        return afterPos;
    }

    private static int FindSafeInsertionPointAfterDefinition(string js, int valueStart)
    {
        var span = js.AsSpan();
        int i = valueStart;
        while (i < span.Length && span[i] is ' ' or '\t') i++;
        if (i >= span.Length) return -1;
        char c = span[i];
        if (c == 'f' && i + 8 <= span.Length && span.Slice(i, 8).SequenceEqual("function"))
        {
            int parenStart = js.IndexOf('(', i + 8);
            if (parenStart >= 0 && parenStart - i < 200)
            {
                int parenEnd = JsFunctionExtractor.FindMatchingParen(js, parenStart);
                if (parenEnd > 0)
                {
                    int bodySearch = parenEnd + 1;
                    while (bodySearch < span.Length && span[bodySearch] is ' ' or '\t') bodySearch++;
                    if (bodySearch < span.Length && span[bodySearch] == '{')
                    {
                        int braceEnd = JsFunctionExtractor.FindMatchingBrace(js, bodySearch);
                        if (braceEnd > 0) return FindSafeInsertionPoint(js, braceEnd + 1);
                    }
                }
            }
        }
        if (c == '{') { int end = JsFunctionExtractor.FindMatchingBrace(js, i); if (end > 0) return FindSafeInsertionPoint(js, end + 1); }
        if (c == '[') { int end = JsFunctionExtractor.FindMatchingBracket(js, i); if (end > 0) return FindSafeInsertionPoint(js, end + 1); }
        if (c == '(') { int end = JsFunctionExtractor.FindMatchingParen(js, i); if (end > 0) return FindSafeInsertionPoint(js, end + 1); }
        if (c is '"' or '\'' or '`') return FindSafeInsertionPoint(js, JsFunctionExtractor.SkipString(js, i));

        int depth = 0;
        while (i < span.Length)
        {
            char ch = span[i];
            if (ch is '"' or '\'' or '`') { i = JsFunctionExtractor.SkipString(js, i); continue; }
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') { depth--; if (depth < 0) return i; }
            else if (depth == 0 && ch is ';' or '\n') return i + 1;
            i++;
        }
        return -1;
    }

    protected virtual string? BuildDefaultBundle(string baseJs, string funcName)
    {
        var dictCode = FindStringDictionary(baseJs);
        string? dictVarName = dictCode is not null ? ExtractVarNameFromDeclaration(dictCode) : null;
        HashSet<string>? externalNames = dictVarName is not null ? [dictVarName] : null;

        var extracted = JsFunctionExtractor.ExtractBundle(baseJs, funcName, externalNames);
        if (extracted is null) return null;

        if (BundleContainsDictionary(extracted)) return extracted;
        if (dictCode is not null) return dictCode + "\n" + extracted;
        return extracted;
    }

    private static string? ExtractVarNameFromDeclaration(string declaration)
    {
        var span = declaration.AsSpan().TrimStart();
        if (span.StartsWith("var ")) span = span[4..];
        else if (span.StartsWith("let ")) span = span[4..];
        else if (span.StartsWith("const ")) span = span[6..];
        else return null;

        span = span.TrimStart();
        int i = 0;
        while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] is '_' or '$')) i++;
        return i == 0 ? null : span[..i].ToString();
    }

    protected static string? FindStringDictionary(string baseJs) => FindSplitStringDictionary(baseJs) ?? FindBracketStringDictionary(baseJs);

    private static string? FindSplitStringDictionary(string baseJs)
    {
        var span = baseJs.AsSpan();
        int pos = 0;
        while (pos < span.Length - 20)
        {
            int varIdx = span[pos..].IndexOf("var ", StringComparison.Ordinal);
            if (varIdx < 0) break;
            varIdx += pos;
            if (varIdx > 0 && span[varIdx - 1] is not (';' or '{' or '}' or '\n' or '\r' or ' ' or '\t')) { pos = varIdx + 4; continue; }

            pos = varIdx + 4;
            int nameStart = pos;
            while (nameStart < span.Length && span[nameStart] is ' ' or '\t') nameStart++;
            int nameEnd = nameStart;
            while (nameEnd < span.Length && (char.IsLetterOrDigit(span[nameEnd]) || span[nameEnd] is '_' or '$')) nameEnd++;
            if (nameEnd - nameStart is < 1 or > 3) continue;

            var varName = span[nameStart..nameEnd].ToString();
            int eqPos = nameEnd;
            while (eqPos < span.Length && span[eqPos] is ' ' or '\t') eqPos++;
            if (eqPos >= span.Length || span[eqPos] != '=') continue;
            eqPos++;
            if (eqPos < span.Length && span[eqPos] == '=') continue;

            int quotePos = eqPos;
            while (quotePos < span.Length && span[quotePos] is ' ' or '\t') quotePos++;
            if (quotePos >= span.Length || span[quotePos] is not ('\'' or '"')) continue;

            int stringStart = quotePos;
            int stringEnd = JsFunctionExtractor.SkipString(baseJs, stringStart);
            if (stringEnd <= stringStart + 2 || stringEnd - stringStart - 2 < 500) continue;

            int afterStr = stringEnd;
            while (afterStr < span.Length && span[afterStr] is ' ' or '\t') afterStr++;
            if (afterStr + 7 > span.Length || !span.Slice(afterStr, 7).SequenceEqual(".split(")) continue;

            int splitParenStart = afterStr + 6;
            int splitParenEnd = JsFunctionExtractor.FindMatchingParen(baseJs, splitParenStart);
            if (splitParenEnd < 0) continue;

            return $"var {varName}={baseJs[stringStart..(splitParenEnd + 1)]};";
        }
        return null;
    }

    private static string? FindBracketStringDictionary(string baseJs)
    {
        var span = baseJs.AsSpan();
        int searchStart = Math.Max(0, span.IndexOf("'use strict'", StringComparison.Ordinal));
        int searchEnd = Math.Min(searchStart + 5000, span.Length);
        int pos = searchStart;

        while (pos < searchEnd - 10)
        {
            int varIdx = span[pos..searchEnd].IndexOf("var ", StringComparison.Ordinal);
            if (varIdx < 0) break;
            varIdx += pos;
            if (varIdx > 0 && span[varIdx - 1] is not (';' or '{' or '}' or '\n' or '\r' or ' ' or '\t')) { pos = varIdx + 4; continue; }

            pos = varIdx + 4;
            int nameStart = pos;
            while (nameStart < span.Length && span[nameStart] is ' ' or '\t') nameStart++;
            int nameEnd = nameStart;
            while (nameEnd < span.Length && (char.IsLetterOrDigit(span[nameEnd]) || span[nameEnd] is '_' or '$')) nameEnd++;
            if (nameEnd - nameStart is < 1 or > 3) continue;

            var varName = span[nameStart..nameEnd].ToString();
            int eqPos = nameEnd;
            while (eqPos < span.Length && span[eqPos] is ' ' or '\t') eqPos++;
            if (eqPos >= span.Length || span[eqPos] != '=') continue;
            eqPos++;

            while (eqPos < span.Length && span[eqPos] is ' ' or '\t') eqPos++;
            if (eqPos >= span.Length || span[eqPos] != '[') continue;

            int bracketStart = eqPos;
            int bracketEnd = JsFunctionExtractor.FindMatchingBracket(baseJs, bracketStart);
            if (bracketEnd < 0) continue;

            int end = bracketEnd + 1;
            if (end < span.Length && span[end] == ';') end++;

            var dictDef = $"var {varName}={baseJs[bracketStart..end]}";
            return dictDef.EndsWith(';') ? dictDef : dictDef + ";";
        }
        return null;
    }

    private static bool BundleContainsDictionary(string bundle)
    {
        int firstTry = bundle.IndexOf("try{", StringComparison.Ordinal);
        var searchArea = firstTry > 0 ? bundle.AsSpan(0, firstTry) : bundle.AsSpan(0, Math.Min(bundle.Length, 5000));
        if (searchArea.Contains(".split(", StringComparison.Ordinal)) return true;

        int quoteCount = 0;
        foreach (char c in searchArea) if (c is '"' or '\'') quoteCount++;
        return quoteCount > 100;
    }

    protected static string Truncate(string s, int len = 20) => s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    public void FlushCache() => Cache.SaveAsync().GetAwaiter().GetResult();

    public void InvalidateCache()
    {
        Cache.Clear();
        ForceDisposeEngines();
        IsInitialized = false;
        Log.Info($"[{DecryptorName}] Cache invalidated");
    }

    public virtual void Dispose()
    {
        FlushCache();
        ForceDisposeEngines();
        _initSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    protected const string BrowserStubs = """
        var _sink = new Proxy(function(){return _sink;}, { get: function(t,p) { return p === 'then' ? void 0 : _sink; }, set: function() { return true; }, apply: function() { return _sink; } });
        var window = this; window.self = window.top = window.globalThis = window;
        var location = { href: 'https://www.youtube.com/', hostname: 'www.youtube.com', protocol: 'https:' };
        window.location = location;
        var document = { createElement: function(tag) { return { tagName: (tag||'').toUpperCase(), style: {}, setAttribute: function(){}, getAttribute: function(){ return null; }, addEventListener: function(){}, removeEventListener: function(){}, appendChild: function(c){ return c; }, removeChild: function(c){ return c; }, insertBefore: function(c){ return c; }, cloneNode: function(){ return this; }, querySelector: function(){ return null; }, querySelectorAll: function(){ return []; }, getElementsByTagName: function(){ return []; }, innerHTML: '', textContent: '', className: '', href: '', src: '', id: '', parentNode: null, firstChild: null, lastChild: null, nextSibling: null, previousSibling: null, childNodes: [], children: [], offsetWidth: 0, offsetHeight: 0, clientWidth: 0, clientHeight: 0 }; }, getElementById: function() { return null; }, querySelector: function() { return null; }, querySelectorAll: function() { return []; }, getElementsByTagName: function() { return []; }, getElementsByClassName: function() { return []; }, createElementNS: function() { return document.createElement(''); }, createTextNode: function(t) { return { textContent: t, nodeType: 3 }; }, createDocumentFragment: function() { return { appendChild: function(c){return c;}, childNodes: [] }; }, head: { appendChild: function(){} }, body: { appendChild: function(){}, removeChild: function(){} }, documentElement: { style: {} }, readyState: 'complete', cookie: '', title: '', domain: 'www.youtube.com' };
        window.document = document;
        var navigator = { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36', platform: 'Win32', language: 'en-US', languages: ['en-US', 'en'], onLine: true, cookieEnabled: true, doNotTrack: null, maxTouchPoints: 0, hardwareConcurrency: 8 };
        window.navigator = navigator;
        window.setTimeout = window.setInterval = function(f) { try { if (typeof f === 'function') f(); } catch(e) {} return 0; };
        window.clearTimeout = window.clearInterval = function() {};
        window.requestAnimationFrame = function(f) { try { f(0); } catch(e) {} return 0; };
        window.cancelAnimationFrame = function() {};
        window.getComputedStyle = function() { return new Proxy({}, { get: function() { return ''; } }); };
        window.matchMedia = function() { return { matches: false, addListener: function(){}, removeListener: function(){} }; };
        window.innerWidth = 1920; window.innerHeight = 1080; window.screen = { width: 1920, height: 1080, colorDepth: 24 }; window.devicePixelRatio = 1;
        window.performance = { now: function() { return Date.now(); }, timing: { navigationStart: Date.now() }, mark: function(){}, measure: function(){}, getEntriesByName: function(){ return []; } };
        window.URL = window.webkitURL = { createObjectURL: function() { return ''; }, revokeObjectURL: function() {} };
        window.MutationObserver = window.WebKitMutationObserver = function() { this.observe = this.disconnect = this.takeRecords = function() {}; };
        window.ResizeObserver = function() { this.observe = this.unobserve = this.disconnect = function() {}; };
        window.IntersectionObserver = function() { this.observe = this.unobserve = this.disconnect = function() {}; };
        var _storageData = {};
        var localStorage = { getItem: function(k) { return _storageData[k] || null; }, setItem: function(k, v) { _storageData[k] = String(v); }, removeItem: function(k) { delete _storageData[k]; }, clear: function() { _storageData = {}; }, get: function(k) { return _storageData[k] || null; }, key: function(i) { return Object.keys(_storageData)[i] || null; }, get length() { return Object.keys(_storageData).length; } };
        window.localStorage = localStorage;
        window.sessionStorage = { getItem: function() { return null; }, setItem: function() {}, removeItem: function() {}, clear: function() {}, get length() { return 0; } };
        var XMLHttpRequest = function() { this.open = this.send = this.setRequestHeader = function() {}; this.readyState = 4; this.status = 200; this.responseText = ''; this.addEventListener = function() {}; };
        var fetch = function() { return Promise.resolve({ ok: true, json: function() { return Promise.resolve({}); }, text: function() { return Promise.resolve(''); } }); };
        var ytcfg = { get: function(k, d) { return d !== void 0 ? d : ''; }, set: function() {}, d: function() { return ''; } };
        var yt = { config_: {}, logging: { errors: [] } };
        var _gFallback = function() {}; _gFallback.prototype = {};
        var g = new Proxy({}, {
            get: function(t, p) {
                if (p === 'then') return void 0;
                if (typeof p === 'symbol') return void 0;
                if (p in t) return t[p];
                return _gFallback;
            },
            set: function(t, p, v) { t[p] = v; return true; },
            has: function(t, p) { return true; }
        });
        var kCO = window, rHS = window, Image = function() { this.src = ''; this.onload = this.onerror = null; }; window.Image = Image;
        var TextEncoder = function() { this.encode = function(s) { return new Uint8Array(0); }; }, TextDecoder = function() { this.decode = function() { return ''; }; };
        window.TextEncoder = TextEncoder; window.TextDecoder = TextDecoder;
        window.crypto = { getRandomValues: function(a) { for(var i=0;i<a.length;i++) a[i]=Math.floor(Math.random()*256); return a; }, subtle: _sink };
        window.atob = function(s) { return ''; }; window.btoa = function(s) { return ''; };
        if (typeof BigInt === 'undefined') { var BigInt = function(v) { return Number(v); }; BigInt.asIntN = function(bits, n) { return Number(n); }; BigInt.asUintN = function(bits, n) { return Number(n); }; window.BigInt = BigInt; }
        """;
}