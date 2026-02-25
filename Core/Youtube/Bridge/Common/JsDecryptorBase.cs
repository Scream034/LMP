using System.Text;
using Jint;

namespace LMP.Core.Youtube.Bridge.Common;

public abstract class JsDecryptorBase<T> : IYoutubeDecryptor, IDisposable
{
    protected readonly PlayerContextManager PlayerManager;
    protected readonly DecryptorCache<string, string> Cache;

    private readonly Lock _initLock = new();

    protected Engine? BundleEngine;
    protected string? BundleFuncName;
    protected Engine? FullEngine;
    protected string? FullFuncName;

    protected string? CurrentPlayerVersion;
    protected volatile bool IsInitialized;

    /// <summary>Имя дешифратора для логов.</summary>
    protected string DecryptorName => typeof(T).Name;

    /// <summary>Папка для диагностических файлов.</summary>
    protected string DiagFolder => Cache.CacheFolder;

    protected JsDecryptorBase(
        PlayerContextManager playerManager,
        string cacheFilePath,
        int maxMemory,
        int maxDisk)
    {
        PlayerManager = playerManager;
        Cache = new DecryptorCache<string, string>(cacheFilePath, maxMemory, maxDisk);
    }

    protected async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (IsInitialized) return;

        lock (_initLock)
        {
            if (IsInitialized) return;
        }

        var context = await PlayerManager.GetOrLoadAsync(ct);

        lock (_initLock)
        {
            if (IsInitialized) return;

            CurrentPlayerVersion = context.Version;
            Cache.LoadAsync(context.Version).Wait(ct);

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
    }

    protected abstract void InitializeCore(PlayerContext context);

    // ═══════════════════════════════════════════════════════════════
    // JS ENGINE INITIALIZATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Универсальная инициализация JS движков.
    /// Подклассы вызывают этот метод, предоставляя:
    /// - funcName: имя entry-функции
    /// - buildWrapperScript: генератор wrapper-скрипта
    /// - testInput: тестовый вход для проверки
    /// - buildBundle: опциональный кастомный builder бандла
    /// </summary>
    protected bool TryInitJsEngines(
        PlayerContext context,
        string funcName,
        Func<string, string> buildWrapperScript,
        string testInput,
        Func<string, string, string?>? buildBundle = null)
    {
        var wrapperScript = buildWrapperScript(funcName);
        const string wrapperFuncName = "__decryptorTransform";

        // 1. Bundle
        var bundle = buildBundle is not null
            ? buildBundle(context.BaseJs, funcName)
            : BuildDefaultBundle(context.BaseJs, funcName);

        if (bundle is not null)
        {
            SaveDiagScript("bundle", bundle, funcName);
            if (TryInitBundle(bundle, wrapperScript, wrapperFuncName, testInput))
            {
                Log.Info($"[{DecryptorName}] Bundle ready ({bundle.Length / 1024}KB)");
                return true;
            }
        }

        Log.Debug($"[{DecryptorName}] Bundle failed, trying full JS...");

        // 2. Full JS with window export injection
        var modifiedJs = InjectWindowExport(context.BaseJs, funcName);
        if (TryInitFull(modifiedJs, wrapperScript, wrapperFuncName, testInput))
        {
            Log.Info($"[{DecryptorName}] Full JS ready ({context.BaseJs.Length / 1024}KB)");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Строит бандл по умолчанию: ExtractBundle + словарь строк + dict resolution.
    /// </summary>
    protected virtual string? BuildDefaultBundle(string baseJs, string funcName)
    {
        var extracted = JsFunctionExtractor.ExtractBundle(baseJs, funcName);
        if (extracted is null) return null;

        // ═══ Step 1: Resolve dictionary references (q[N] → actual values) ═══
        var dictName = JsDictResolver.DetectDictName(extracted);
        if (dictName is not null)
        {
            var dictElements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (dictElements is not null && dictElements.Length >= 10)
            {
                var resolved = JsDictResolver.Resolve(extracted, dictName, dictElements);
                Log.Debug($"[{DecryptorName}] Dict '{dictName}' resolved in bundle: " +
                          $"{extracted.Length} → {resolved.Length} chars");
                extracted = resolved;
            }
        }

        // ═══ Step 2: Check if dictionary .split() definition is included ═══
        if (BundleContainsDictionary(extracted))
            return extracted;

        // Fallback: find and prepend dictionary
        var dictCode = FindStringDictionary(baseJs);
        if (dictCode is not null)
        {
            Log.Debug($"[{DecryptorName}] Prepending dictionary ({dictCode.Length} chars)");
            return dictCode + "\n" + extracted;
        }

        Log.Warn($"[{DecryptorName}] Dictionary not found, bundle may fail");
        return extracted;
    }

    private bool TryInitBundle(
        string bundle, string wrapperScript, string wrapperFuncName, string testInput)
    {
        try
        {
            BundleEngine = CreateEngine(TimeSpan.FromSeconds(15), 2_000_000);
            BundleEngine.Execute(BrowserStubs);
            BundleEngine.Execute(bundle);
            BundleEngine.Execute(wrapperScript);

            var result = BundleEngine.Invoke(wrapperFuncName, testInput).AsString();
            if (string.IsNullOrEmpty(result) || result == testInput)
            {
                BundleEngine.Dispose();
                BundleEngine = null;
                return false;
            }

            BundleFuncName = wrapperFuncName;
            Cache.Set(testInput, result);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[{DecryptorName}] Bundle init failed: {ex.Message}");
            SaveDiagError("bundle", ex.ToString());
            BundleEngine?.Dispose();
            BundleEngine = null;
            return false;
        }
    }

    private bool TryInitFull(
        string baseJs, string wrapperScript, string wrapperFuncName, string testInput)
    {
        try
        {
            FullEngine = CreateEngine(TimeSpan.FromSeconds(30), 5_000_000);
            FullEngine.Execute(BrowserStubs);
            FullEngine.Execute(baseJs);
            FullEngine.Execute(wrapperScript);

            var result = FullEngine.Invoke(wrapperFuncName, testInput).AsString();
            if (string.IsNullOrEmpty(result) || result == testInput)
            {
                FullEngine.Dispose();
                FullEngine = null;
                return false;
            }

            FullFuncName = wrapperFuncName;
            Cache.Set(testInput, result);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[{DecryptorName}] Full JS init failed: {ex.Message}");
            SaveDiagError("full", ex.ToString());
            FullEngine?.Dispose();
            FullEngine = null;
            return false;
        }
    }

    /// <summary>
    /// Вызывает JS-движок для расшифровки.
    /// Сначала bundle, потом full — с кэшированием результата.
    /// </summary>
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
            catch (Exception ex)
            {
                Log.Warn($"[{DecryptorName}] {logPrefix} Bundle failed: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                Log.Error($"[{DecryptorName}] {logPrefix} Full JS failed: {ex.Message}");
            }
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // WINDOW EXPORT INJECTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Инжектирует window['funcName']=funcName; после определения функции в base.js.
    /// Необходимо потому что функции определены внутри IIFE и недоступны глобально.
    /// </summary>
    protected static string InjectWindowExport(string baseJs, string funcName)
    {
        try
        {
            // Strategy 1: через JsFunctionExtractor
            var funcInfo = JsFunctionExtractor.FindFunctionByName(baseJs, funcName);
            if (funcInfo is not null)
            {
                int endOfDef = funcInfo.Value.Position + funcName.Length + 1 + funcInfo.Value.Code.Length;
                while (endOfDef < baseJs.Length && baseJs[endOfDef] is ';' or ',' or ' ' or '\t')
                    endOfDef++;

                var export = $"\nwindow['{funcName}']={funcName};\n";
                Log.Debug($"[JsDecryptor] Injecting window export for '{funcName}' at position {endOfDef}");
                return baseJs.Insert(endOfDef, export);
            }

            // Strategy 2: простой string search
            var pattern = $"{funcName}=function(";
            int patternIdx = baseJs.IndexOf(pattern, StringComparison.Ordinal);
            if (patternIdx >= 0)
            {
                int braceStart = baseJs.IndexOf('{', patternIdx + pattern.Length);
                if (braceStart >= 0)
                {
                    int braceEnd = JsFunctionExtractor.FindMatchingBrace(baseJs, braceStart);
                    if (braceEnd > 0)
                    {
                        int insertPos = braceEnd + 1;
                        if (insertPos < baseJs.Length && baseJs[insertPos] == ';') insertPos++;

                        var export = $"\nwindow['{funcName}']={funcName};\n";
                        return baseJs.Insert(insertPos, export);
                    }
                }
            }

            Log.Warn($"[JsDecryptor] Could not find injection point for '{funcName}'");
        }
        catch (Exception ex)
        {
            Log.Warn($"[JsDecryptor] Window export injection failed: {ex.Message}");
        }

        return baseJs;
    }

    // ═══════════════════════════════════════════════════════════════
    // STRING DICTIONARY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Находит определение глобального словаря строк (var q = '...'.split("}")).
    /// JS-aware parsing: обрабатывает escaped кавычки, любые разделители,
    /// и корректно отрезает trailing var declarations.
    /// </summary>
    protected static string? FindStringDictionary(string baseJs)
    {
        var span = baseJs.AsSpan();
        int pos = 0;

        while (pos < span.Length - 20)
        {
            int varIdx = span[pos..].IndexOf("var ", StringComparison.Ordinal);
            if (varIdx < 0) break;
            varIdx += pos;

            if (varIdx > 0 && span[varIdx - 1] is not (';' or '{' or '}' or '\n' or '\r' or ' ' or '\t'))
            {
                pos = varIdx + 4;
                continue;
            }

            pos = varIdx + 4;

            int nameStart = pos;
            while (nameStart < span.Length && span[nameStart] is ' ' or '\t') nameStart++;

            int nameEnd = nameStart;
            while (nameEnd < span.Length && (char.IsLetterOrDigit(span[nameEnd]) || span[nameEnd] is '_' or '$'))
                nameEnd++;

            int nameLen = nameEnd - nameStart;
            if (nameLen < 1 || nameLen > 3) continue;

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
            if (stringEnd <= stringStart + 2) continue;

            if (stringEnd - stringStart - 2 < 500) continue;

            int afterStr = stringEnd;
            while (afterStr < span.Length && span[afterStr] is ' ' or '\t') afterStr++;
            if (afterStr + 7 > span.Length) continue;
            if (!span.Slice(afterStr, 7).SequenceEqual(".split(")) continue;

            int splitParenStart = afterStr + 6;
            int splitParenEnd = JsFunctionExtractor.FindMatchingParen(baseJs, splitParenStart);
            if (splitParenEnd < 0) continue;

            // Извлекаем разделитель и считаем элементы
            int sepStart = splitParenStart + 1;
            while (sepStart < span.Length && span[sepStart] is ' ' or '\t') sepStart++;
            if (sepStart >= span.Length || span[sepStart] is not ('\'' or '"')) continue;

            char sepQuote = span[sepStart];
            int sepContentStart = sepStart + 1;
            int sepContentEnd = -1;
            for (int si = sepContentStart; si < span.Length; si++)
            {
                if (span[si] == '\\' && si + 1 < span.Length) { si++; continue; }
                if (span[si] == sepQuote) { sepContentEnd = si; break; }
            }
            if (sepContentEnd < 0 || sepContentEnd == sepContentStart ||
                sepContentEnd - sepContentStart > 10) continue;

            var separator = span[sepContentStart..sepContentEnd];
            var stringContent = span[(stringStart + 1)..(stringEnd - 1)];

            int separatorCount = 0;
            int sPos = 0;
            while (sPos <= stringContent.Length - separator.Length)
            {
                int found = stringContent[sPos..].IndexOf(separator, StringComparison.Ordinal);
                if (found < 0) break;
                separatorCount++;
                sPos += found + separator.Length;
            }
            if (separatorCount + 1 < 50) continue;

            int dictEnd = splitParenEnd + 1;
            var dictDef = $"var {varName}={baseJs[stringStart..dictEnd]};";

            Log.Debug($"[JsDecryptor] Found dictionary '{varName}', {separatorCount + 1} elements");
            return dictDef;
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static bool BundleContainsDictionary(string bundle)
    {
        int firstTry = bundle.IndexOf("try{", StringComparison.Ordinal);
        var searchArea = firstTry > 0
            ? bundle.AsSpan(0, firstTry)
            : bundle.AsSpan(0, Math.Min(bundle.Length, 5000));
        return searchArea.Contains(".split(", StringComparison.Ordinal);
    }

    private static Engine CreateEngine(TimeSpan timeout, int maxStatements) =>
        new(opt => opt
            .TimeoutInterval(timeout)
            .LimitRecursion(200)
            .MaxStatements(maxStatements));

    protected void SaveDiagScript(string type, string code, string funcName)
    {
        try
        {
            Directory.CreateDirectory(DiagFolder);

            var sb = new StringBuilder();
            sb.AppendLine("// ═══════════════════════════════════════════════════════════");
            sb.AppendLine($"// {DecryptorName.ToUpper()} {type.ToUpper()} — AUTO-GENERATED");
            sb.AppendLine($"// Player version: {CurrentPlayerVersion}");
            sb.AppendLine($"// Entry function: {funcName}");
            sb.AppendLine($"// Generated: {DateTime.UtcNow:O}");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════\n");
            sb.AppendLine(code);

            var path = Path.Combine(DiagFolder, $"player_{CurrentPlayerVersion}_{type}.js");
            File.WriteAllText(path, sb.ToString());
        }
        catch { /* ignore */ }
    }

    protected void SaveDiagError(string type, string error)
    {
        if (CurrentPlayerVersion is null) return;
        try
        {
            Directory.CreateDirectory(DiagFolder);
            var path = Path.Combine(DiagFolder, $"player_{CurrentPlayerVersion}_{type}_error.txt");
            File.WriteAllText(path, $"""
                === {DecryptorName.ToUpper()} {type.ToUpper()} ERROR ===
                Player version: {CurrentPlayerVersion}
                Timestamp: {DateTime.UtcNow:O}
                
                {error}
                """);
        }
        catch { /* ignore */ }
    }

    protected static string Truncate(string s, int len = 20) =>
        s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    public void FlushCache() => Cache.SaveAsync().Wait();

    public void InvalidateCache()
    {
        Cache.Clear();
        BundleEngine?.Dispose();
        BundleEngine = null;
        FullEngine?.Dispose();
        FullEngine = null;
        IsInitialized = false;
        Log.Info($"[{DecryptorName}] Cache invalidated");
    }

    public virtual void Dispose()
    {
        FlushCache();
        BundleEngine?.Dispose();
        FullEngine?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected const string BrowserStubs = """
        var _sink = new Proxy(function(){return _sink;}, {
            get: function(t,p) { return p === 'then' ? void 0 : _sink; },
            set: function() { return true; },
            apply: function() { return _sink; }
        });
        var window = this; window.self = window.top = window.globalThis = window;
        var location = { href: 'https://www.youtube.com/', hostname: 'www.youtube.com', protocol: 'https:' };
        window.location = location;
        var document = { 
            createElement: function() { return _sink; }, 
            getElementById: function() { return null; },
            querySelector: function() { return null; },
            querySelectorAll: function() { return []; },
            readyState: 'complete'
        };
        window.document = document;
        var navigator = { userAgent: '', platform: '' }; window.navigator = navigator;
        window.setTimeout = window.setInterval = function(f) { try { if (typeof f === 'function') f(); } catch(e) {} return 0; };
        window.clearTimeout = window.clearInterval = function() {};
        var XMLHttpRequest = function() {
            this.open = this.send = this.setRequestHeader = function() {};
            this.readyState = 4; this.status = 200; this.responseText = '';
        };
        var fetch = function() { return Promise.resolve({ ok: true, text: function() { return Promise.resolve(''); } }); };
        var ytcfg = { get: function() { return ''; }, set: function() {}, d: function() { return ''; } };
        var yt = { config_: {} };
        var g = new Proxy({}, {
            get: function(t, p) {
                if (p === 'then') return void 0;
                if (p in t) return t[p];
                return function() { return undefined; };
            },
            set: function(t, p, v) { t[p] = v; return true; },
            has: function(t, p) { return p in t; }
        });
        var kCO = window;
        var rHS = window;
        """;
}