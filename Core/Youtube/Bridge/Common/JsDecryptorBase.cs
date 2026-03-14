// Core/Youtube/Bridge/Common/JsDecryptorBase.cs

using System.Text;
using Jint;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Базовый класс для JS-дешифраторов YouTube (N-Token, SigCipher и др.).
/// <para>
/// Содержит общую логику:
/// - Инициализация JS-движков (Bundle / Full JS)
/// - Кэширование результатов
/// - Window export injection
/// - Browser stubs для Jint
/// - Поиск строковых словарей (split / bracket)
/// </para>
/// </summary>
public abstract class JsDecryptorBase<T> : IYoutubeDecryptor, IDisposable
{
    protected readonly PlayerContextManager PlayerManager;
    protected readonly DecryptorCache<string, string> Cache;

    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    protected Engine? BundleEngine;
    protected string? BundleFuncName;
    protected Engine? FullEngine;
    protected string? FullFuncName;

    protected string? CurrentPlayerVersion;
    internal volatile bool IsInitialized;

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

    // ═══════════════════════════════════════════════════════════════
    // INITIALIZATION
    // ═══════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════
    // JS ENGINE FACTORY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Создаёт настроенный JS-движок с ограничениями по времени и statements.
    /// <para>
    /// Единственная точка создания Engine — устраняет дублирование
    /// между NTokenDecryptor и JsDecryptorBase.
    /// </para>
    /// </summary>
    protected static Engine CreateEngine(TimeSpan timeout, int maxStatements) =>
        new(opt => opt
            .TimeoutInterval(timeout)
            .LimitRecursion(200)
            .MaxStatements(maxStatements));

    /// <summary>Движок для Bundle (компактный JS, жёсткие лимиты).</summary>
    protected static Engine CreateBundleEngine() =>
        CreateEngine(TimeSpan.FromSeconds(15), 2_000_000);

    /// <summary>Движок для Full JS (полный base.js, мягкие лимиты).</summary>
    protected static Engine CreateFullEngine() =>
        CreateEngine(TimeSpan.FromSeconds(30), 5_000_000);

    /// <summary>Движок для Sieve fuzzing (полный base.js, максимальные лимиты).</summary>
    protected static Engine CreateSieveEngine() =>
        CreateEngine(TimeSpan.FromSeconds(30), 10_000_000);

    // ═══════════════════════════════════════════════════════════════
    // JS ENGINE INITIALIZATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Инициализация JS-движков БЕЗ magic numbers.
    /// Используется SigCipherDecryptor и другими декрипторами,
    /// которые не нуждаются в числовых аргументах.
    /// </summary>
    internal bool TryInitJsEngines(
        PlayerContext context,
        string funcName,
        Func<string, string> buildWrapperScript,
        string testInput,
        Func<string, string, string?>? buildBundle = null,
        Func<string?, string, bool>? validateResult = null)
    {
        return TryInitJsEngines(
            context, funcName, MagicNumbers.None,
            (fn, _) => buildWrapperScript(fn),
            testInput, buildBundle, validateResult);
    }

    /// <summary>
    /// Инициализация JS-движков С magic numbers.
    /// Пробует: Bundle → Full JS → Fallback без magic.
    /// </summary>
    internal bool TryInitJsEngines(
        PlayerContext context,
        string funcName,
        MagicNumbers magic,
        Func<string, MagicNumbers, string> buildWrapperScript,
        string testInput,
        Func<string, string, string?>? buildBundle = null,
        Func<string?, string, bool>? validateResult = null)
    {
        var wrapperScript = buildWrapperScript(funcName, magic);
        const string wrapperFuncName = "__decryptorTransform";

        var bundle = buildBundle is not null
            ? buildBundle(context.BaseJs, funcName)
            : BuildDefaultBundle(context.BaseJs, funcName);

        if (bundle is not null)
        {
            SaveDiagScript("bundle", bundle, funcName);
            if (TryInitEngine(bundle, wrapperScript, wrapperFuncName, testInput,
                    isBundle: true, validateResult))
            {
                Log.Info($"[{DecryptorName}] Bundle ready ({bundle.Length / 1024}KB, {magic})");
                return true;
            }
        }

        Log.Debug($"[{DecryptorName}] Bundle failed, trying full JS...");

        var modifiedJs = InjectWindowExport(context.BaseJs, funcName);
        if (TryInitEngine(modifiedJs, wrapperScript, wrapperFuncName, testInput,
                isBundle: false, validateResult))
        {
            Log.Info($"[{DecryptorName}] Full JS ready ({context.BaseJs.Length / 1024}KB, {magic})");
            return true;
        }

        if (magic.HasArgs)
        {
            Log.Debug($"[{DecryptorName}] Retrying without magic numbers...");
            var fallbackWrapper = buildWrapperScript(funcName, MagicNumbers.None);

            if (bundle is not null &&
                TryInitEngine(bundle, fallbackWrapper, wrapperFuncName, testInput,
                    isBundle: true, validateResult))
            {
                Log.Info($"[{DecryptorName}] Bundle ready WITHOUT magic (fallback)");
                return true;
            }

            if (TryInitEngine(modifiedJs, fallbackWrapper, wrapperFuncName, testInput,
                    isBundle: false, validateResult))
            {
                Log.Info($"[{DecryptorName}] Full JS ready WITHOUT magic (fallback)");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Пробует инициализировать с несколькими вариантами magic numbers.
    /// <para>
    /// ОПТИМИЗАЦИЯ:
    /// 1. Bundle строится ОДИН раз (не зависит от magic).
    /// 2. Full JS с window export строится ОДИН раз (lazy).
    /// 3. Early exit при 2+ одинаковых ошибках подряд (системная проблема).
    /// </para>
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
        if (candidates.Count == 0)
            candidates = [MagicNumbers.None];

        string? cachedBundle = buildBundle is not null
            ? buildBundle(context.BaseJs, funcName)
            : BuildDefaultBundle(context.BaseJs, funcName);

        string? cachedModifiedJs = null;

        string? lastBundleError = null;
        int bundleRepeatCount = 0;
        string? lastFullError = null;
        int fullRepeatCount = 0;
        const int maxRepeat = 2;
        const string wrapperFuncName = "__decryptorTransform";

        foreach (var magic in candidates)
        {
            Log.Debug($"[{DecryptorName}] Trying magic: {magic}");

            var wrapperScript = buildWrapperScript(funcName, magic);
            bool bundleTriedAndFailed = false;

            if (cachedBundle is not null && bundleRepeatCount < maxRepeat)
            {
                if (TryInitTracked(cachedBundle, wrapperScript, wrapperFuncName,
                        testInput, isBundle: true,
                        ref lastBundleError, ref bundleRepeatCount, validateResult))
                    return true;
                bundleTriedAndFailed = true;
            }

            if (fullRepeatCount < maxRepeat)
            {
                if (bundleTriedAndFailed)
                    Log.Debug($"[{DecryptorName}] Bundle failed, trying full JS...");

                cachedModifiedJs ??= InjectWindowExport(context.BaseJs, funcName);

                if (TryInitTracked(cachedModifiedJs, wrapperScript, wrapperFuncName,
                        testInput, isBundle: false,
                        ref lastFullError, ref fullRepeatCount, validateResult))
                    return true;
            }

            if (magic.HasArgs)
            {
                Log.Debug($"[{DecryptorName}] Retrying without magic numbers...");
                var fallbackWrapper = buildWrapperScript(funcName, MagicNumbers.None);

                if (cachedBundle is not null && bundleRepeatCount < maxRepeat)
                {
                    if (TryInitTracked(cachedBundle, fallbackWrapper, wrapperFuncName,
                            testInput, isBundle: true,
                            ref lastBundleError, ref bundleRepeatCount, validateResult))
                        return true;
                }

                if (fullRepeatCount < maxRepeat)
                {
                    cachedModifiedJs ??= InjectWindowExport(context.BaseJs, funcName);
                    if (TryInitTracked(cachedModifiedJs, fallbackWrapper, wrapperFuncName,
                            testInput, isBundle: false,
                            ref lastFullError, ref fullRepeatCount, validateResult))
                        return true;
                }
            }

            if (bundleRepeatCount >= maxRepeat && fullRepeatCount >= maxRepeat)
            {
                Log.Debug($"[{DecryptorName}] Both strategies hit repeat limit, " +
                          $"skipping remaining candidates");
                break;
            }

            ForceDisposeEngines();
        }

        return false;
    }

    private bool TryInitTracked(
        string jsCode,
        string wrapperScript,
        string wrapperFuncName,
        string testInput,
        bool isBundle,
        ref string? lastError,
        ref int repeatCount,
        Func<string?, string, bool>? validateResult = null)
    {
        if (TryInitEngine(jsCode, wrapperScript, wrapperFuncName, testInput,
                isBundle, validateResult))
        {
            lastError = null;
            repeatCount = 0;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Низкоуровневая инициализация одного JS-движка.
    /// <para>
    /// ИСПРАВЛЕНИЕ: Валидация результата через <c>validateResult</c> callback.
    /// JS wrapper проверяет только <c>r !== n</c>, но C#-код должен
    /// дополнительно проверять длину, символьный состав и substitution distance.
    /// Без этого проходят ложные результаты: URL-charset строки (64 символа),
    /// splice(0,1) мутации и прочие тривиальные преобразования.
    /// </para>
    /// </summary>
    private bool TryInitEngine(
        string jsCode,
        string wrapperScript,
        string wrapperFuncName,
        string testInput,
        bool isBundle,
        Func<string?, string, bool>? validateResult = null)
    {
        Engine? engine = null;
        try
        {
            engine = isBundle ? CreateBundleEngine() : CreateFullEngine();
            engine.Execute(BrowserStubs);
            engine.Execute(jsCode);
            engine.Execute(wrapperScript);

            var result = engine.Invoke(wrapperFuncName, testInput).AsString();
            if (string.IsNullOrEmpty(result) || result == testInput)
            {
                var jsError = ReadJsError(engine);
                if (jsError is not null)
                    Log.Debug($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} JS error: {jsError}");

                engine.Dispose();
                return false;
            }

            // ═══ C#-ВАЛИДАЦИЯ результата ═══
            if (validateResult is not null && !validateResult(result, testInput))
            {
                Log.Debug($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} " +
                          $"result rejected by validator: '{Truncate(result, 40)}'");
                engine.Dispose();
                return false;
            }

            if (isBundle)
            {
                BundleEngine = engine;
                BundleFuncName = wrapperFuncName;
            }
            else
            {
                FullEngine = engine;
                FullFuncName = wrapperFuncName;
            }
            Cache.Set(testInput, result);

            var label = isBundle ? "Bundle" : "Full JS";
            Log.Info($"[{DecryptorName}] {label} ready ({jsCode.Length / 1024}KB)");
            return true;
        }
        catch (Exception ex)
        {
            var detailedMsg = FormatJsException(ex);
            Log.Debug($"[{DecryptorName}] {(isBundle ? "Bundle" : "Full")} init failed: " +
                      $"{Truncate(detailedMsg, 150)}");

            if (isBundle)
                SaveDiagError("bundle", detailedMsg);
            else
                SaveDiagError("full", detailedMsg);

            engine?.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Принудительно освобождает все неиспользуемые движки.
    /// </summary>
    protected void ForceDisposeEngines()
    {
        BundleEngine?.Dispose();
        BundleEngine = null;
        BundleFuncName = null;
        FullEngine?.Dispose();
        FullEngine = null;
        FullFuncName = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // JS ERROR FORMATTING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Форматирует Jint-исключение с максимумом деталей:
    /// JS error object, stack trace, позиция в коде.
    /// <para>
    /// Jint выбрасывает <c>JavaScriptException</c> с полем <c>Error</c>
    /// (JS-объект) и <c>JavaScriptStackTrace</c>. Стандартный <c>ex.Message</c>
    /// часто содержит только "X is not a function" без контекста.
    /// </para>
    /// </summary>
    protected static string FormatJsException(Exception ex)
    {
        if (ex is not Jint.Runtime.JavaScriptException jsEx)
            return ex.Message;

        var sb = new StringBuilder(256);
        sb.Append(jsEx.Message);

        // JS Error object
        try
        {
            var errorObj = jsEx.Error;
            if (errorObj is not null && !errorObj.IsUndefined() && !errorObj.IsNull())
            {
                var errorStr = errorObj.ToString();
                if (errorStr != jsEx.Message)
                    sb.Append($" [JSError: {Truncate(errorStr, 100)}]");
            }
        }
        catch { /* ignore */ }

        // JS stack trace
        try
        {
            var jsStack = jsEx.JavaScriptStackTrace;
            if (!string.IsNullOrEmpty(jsStack))
                sb.Append($" [JSStack: {Truncate(jsStack, 150)}]");
        }
        catch { /* ignore */ }

        // Source location
        try
        {
            var loc = jsEx.Location;
            if (loc.Start.Line > 0)
                sb.Append($" [Line:{loc.Start.Line}:{loc.Start.Column}]");
        }
        catch { /* ignore */ }

        return sb.ToString();
    }

    /// <summary>
    /// Читает последнюю JS-ошибку из глобальной переменной <c>__lastError</c>.
    /// </summary>
    private static string? ReadJsError(Engine engine)
    {
        try
        {
            var errorVal = engine.GetValue("__lastError");
            if (errorVal.IsString())
            {
                var msg = errorVal.AsString();
                return string.IsNullOrWhiteSpace(msg) ? null : msg;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // JS INVOCATION
    // ═══════════════════════════════════════════════════════════════

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
                    Log.Debug($"[{DecryptorName}] {logPrefix} Bundle: " +
                              $"{Truncate(input)} -> {Truncate(result)}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[{DecryptorName}] {logPrefix} Bundle failed: " +
                         $"{FormatJsException(ex)}");
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
                    Log.Debug($"[{DecryptorName}] {logPrefix} Full: " +
                              $"{Truncate(input)} -> {Truncate(result)}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[{DecryptorName}] {logPrefix} Full JS failed: " +
                          $"{FormatJsException(ex)}");
            }
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // WINDOW EXPORT INJECTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Инжектирует <c>window['funcName']=funcName;</c> в base.js.
    /// <para>
    /// Стратегии (в порядке приоритета):
    /// 1. Точное определение через FindFunctionByName
    /// 2. Pattern search для function(
    /// 3. Generic name= search
    /// 4. Вставка перед концом IIFE
    /// 5. Конец файла
    /// </para>
    /// </summary>
    protected static string InjectWindowExport(string baseJs, string funcName)
    {
        var export = $"\nwindow['{funcName}']={funcName};\n";

        try
        {
            // Strategy 1: Точное определение
            var funcInfo = JsFunctionExtractor.FindFunctionByName(baseJs, funcName);
            if (funcInfo is not null)
            {
                int endOfDef = funcInfo.Value.Position + funcName.Length + 1 + funcInfo.Value.Code.Length;
                int safePos = FindSafeInsertionPoint(baseJs, endOfDef);

                Log.Debug($"[JsDecryptor] Injecting window export for '{funcName}' " +
                          $"at safe position {safePos} (def ended at {endOfDef})");
                return baseJs.Insert(safePos, export);
            }

            // Strategy 2: function( pattern
            var pattern = $"{funcName}=function(";
            int patternIdx = baseJs.IndexOf(pattern, StringComparison.Ordinal);
            if (patternIdx >= 0 &&
                (patternIdx == 0 || !JsFunctionExtractor.IsIdentChar(baseJs[patternIdx - 1])))
            {
                int parenStart = patternIdx + pattern.Length - 1;
                int parenEnd = JsFunctionExtractor.FindMatchingParen(baseJs, parenStart);
                if (parenEnd > 0)
                {
                    int bodySearch = parenEnd + 1;
                    while (bodySearch < baseJs.Length && baseJs[bodySearch] is ' ' or '\t' or '\n' or '\r')
                        bodySearch++;

                    if (bodySearch < baseJs.Length && baseJs[bodySearch] == '{')
                    {
                        int braceEnd = JsFunctionExtractor.FindMatchingBrace(baseJs, bodySearch);
                        if (braceEnd > 0)
                        {
                            int safePos = FindSafeInsertionPoint(baseJs, braceEnd + 1);
                            Log.Debug($"[JsDecryptor] Injecting window export for '{funcName}' " +
                                      $"via pattern search at position {safePos}");
                            return baseJs.Insert(safePos, export);
                        }
                    }
                }
            }

            // Strategy 3: name= generic search
            var eqPattern = $"{funcName}=";
            var baseSpan = baseJs.AsSpan();
            int searchFrom = 0;

            while (searchFrom < baseSpan.Length)
            {
                int found = baseSpan[searchFrom..].IndexOf(eqPattern.AsSpan(), StringComparison.Ordinal);
                if (found < 0) break;
                found += searchFrom;

                if (found > 0 && JsFunctionExtractor.IsIdentChar(baseSpan[found - 1]))
                {
                    searchFrom = found + eqPattern.Length;
                    continue;
                }

                int afterEq = found + eqPattern.Length;
                if (afterEq < baseSpan.Length && baseSpan[afterEq] == '=')
                {
                    searchFrom = afterEq + 1;
                    continue;
                }

                int safePos = FindSafeInsertionPointAfterDefinition(baseJs, afterEq);
                if (safePos > 0)
                {
                    Log.Debug($"[JsDecryptor] Injecting window export for '{funcName}' " +
                              $"via eq-search at position {safePos}");
                    return baseJs.Insert(safePos, export);
                }

                searchFrom = afterEq;
            }

            // Strategy 4: IIFE end
            int fallbackPos = FindEndOfIIFE(baseJs);
            if (fallbackPos > 0)
            {
                Log.Debug($"[JsDecryptor] Injecting window export for '{funcName}' " +
                          $"at IIFE end (fallback), position {fallbackPos}");
                return baseJs.Insert(fallbackPos, export);
            }

            // Strategy 5: EOF
            Log.Warn($"[JsDecryptor] Using end-of-file fallback for '{funcName}'");
            return baseJs + export;
        }
        catch (Exception ex)
        {
            Log.Warn($"[JsDecryptor] Window export injection failed: {ex.Message}");
        }

        return baseJs;
    }

    private static int FindSafeInsertionPoint(string js, int afterPos)
    {
        var span = js.AsSpan();
        int i = afterPos;

        while (i < span.Length && span[i] is ' ' or '\t') i++;

        if (i < span.Length && span[i] == ';') return i + 1;
        if (i < span.Length && span[i] == '}') return i + 1;
        if (i < span.Length && span[i] == ',') return i + 1;

        while (i < span.Length)
        {
            char c = span[i];

            if (c is '"' or '\'' or '`')
            {
                i = JsFunctionExtractor.SkipString(js, i);
                continue;
            }

            if (c == '/' && i + 1 < span.Length)
            {
                if (span[i + 1] == '/')
                {
                    int nl = span[i..].IndexOf('\n');
                    i = nl >= 0 ? i + nl : span.Length;
                    continue;
                }
                if (span[i + 1] == '*')
                {
                    i += 2;
                    int end = span[i..].IndexOf("*/");
                    i = end >= 0 ? i + end + 2 : span.Length;
                    continue;
                }
            }

            if (c is ';' or '\n') return i + 1;
            if (c == '}') return i + 1;

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
                        if (braceEnd > 0)
                            return FindSafeInsertionPoint(js, braceEnd + 1);
                    }
                }
            }
        }

        if (c == '{')
        {
            int end = JsFunctionExtractor.FindMatchingBrace(js, i);
            if (end > 0) return FindSafeInsertionPoint(js, end + 1);
        }
        if (c == '[')
        {
            int end = JsFunctionExtractor.FindMatchingBracket(js, i);
            if (end > 0) return FindSafeInsertionPoint(js, end + 1);
        }
        if (c == '(')
        {
            int end = JsFunctionExtractor.FindMatchingParen(js, i);
            if (end > 0) return FindSafeInsertionPoint(js, end + 1);
        }
        if (c is '"' or '\'' or '`')
        {
            int end = JsFunctionExtractor.SkipString(js, i);
            return FindSafeInsertionPoint(js, end);
        }

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

    private static int FindEndOfIIFE(string js)
    {
        var span = js.AsSpan();
        for (int i = span.Length - 1; i >= 1; i--)
        {
            if (span[i] == '(' && span[i - 1] == ')')
            {
                int j = i - 2;
                while (j >= 0 && span[j] is ' ' or '\t' or '\n' or '\r') j--;
                if (j >= 0 && span[j] == '}')
                    return j;
            }

            if (span[i] == ';' && i >= 1 && span[i - 1] == ')')
            {
                int j = i - 2;
                while (j >= 0 && span[j] is ' ' or '\t' or '\n' or '\r') j--;
                if (j >= 0 && span[j] == '}')
                    return j;
            }
        }

        return -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // DEFAULT BUNDLE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Строит минимальный JS-бандл для функции дешифрации.
    /// <para>
    /// Стратегия:
    /// 1. Находит глобальный словарь строк (split/bracket)
    /// 2. Передаёт имя словаря как external name в ExtractBundle
    /// 3. Prepend'ит словарь если он не включён в бандл
    /// </para>
    /// </summary>
    protected virtual string? BuildDefaultBundle(string baseJs, string funcName)
    {
        var dictCode = FindStringDictionary(baseJs);
        string? dictVarName = null;

        if (dictCode is not null)
            dictVarName = ExtractVarNameFromDeclaration(dictCode);

        HashSet<string>? externalNames = null;
        if (dictVarName is not null)
            externalNames = [dictVarName];

        var extracted = JsFunctionExtractor.ExtractBundle(baseJs, funcName, externalNames);
        if (extracted is null) return null;

        if (BundleContainsDictionary(extracted))
            return extracted;

        if (dictCode is not null)
        {
            Log.Debug($"[{DecryptorName}] Prepending dictionary ({dictCode.Length} chars)");
            return dictCode + "\n" + extracted;
        }

        Log.Warn($"[{DecryptorName}] Dictionary not found, bundle may fail");
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
        while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] is '_' or '$'))
            i++;

        return i == 0 ? null : span[..i].ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // STRING DICTIONARY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Находит определение глобального словаря строк.
    /// Поддерживает split-стиль и bracket-стиль.
    /// </summary>
    protected static string? FindStringDictionary(string baseJs)
    {
        return FindSplitStringDictionary(baseJs)
            ?? FindBracketStringDictionary(baseJs);
    }

    private static string? FindSplitStringDictionary(string baseJs)
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

            Log.Debug($"[JsDecryptor] Found split dictionary '{varName}', " +
                      $"{separatorCount + 1} elements");
            return dictDef;
        }

        return null;
    }

    private static string? FindBracketStringDictionary(string baseJs)
    {
        var span = baseJs.AsSpan();

        int searchStart = 0;
        int useStrict = span.IndexOf("'use strict'", StringComparison.Ordinal);
        if (useStrict >= 0) searchStart = useStrict;

        int searchEnd = Math.Min(searchStart + 5000, span.Length);

        int pos = searchStart;
        while (pos < searchEnd - 10)
        {
            int varIdx = span[pos..searchEnd].IndexOf("var ", StringComparison.Ordinal);
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

            while (eqPos < span.Length && span[eqPos] is ' ' or '\t') eqPos++;
            if (eqPos >= span.Length || span[eqPos] != '[') continue;

            int bracketStart = eqPos;
            int bracketEnd = JsFunctionExtractor.FindMatchingBracket(baseJs, bracketStart);
            if (bracketEnd < 0) continue;

            int elementCount = 0;
            int depth = 0;
            for (int i = bracketStart + 1; i < bracketEnd; i++)
            {
                char c = span[i];
                if (c is '"' or '\'' or '`')
                {
                    i = JsFunctionExtractor.SkipString(baseJs, i) - 1;
                    continue;
                }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ',' && depth == 0) elementCount++;
            }
            elementCount++;

            if (elementCount < 50) continue;

            var sampleEnd = Math.Min(bracketStart + 200, bracketEnd);
            var sample = span[bracketStart..sampleEnd];
            if (!sample.Contains('"') && !sample.Contains('\'')) continue;

            int end = bracketEnd + 1;
            if (end < span.Length && span[end] == ';') end++;

            var dictDef = $"var {varName}={baseJs[bracketStart..end]}";
            if (!dictDef.EndsWith(';')) dictDef += ";";

            Log.Debug($"[JsDecryptor] Found bracket dictionary '{varName}', " +
                      $"{elementCount} elements");
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

        if (searchArea.Contains(".split(", StringComparison.Ordinal))
            return true;

        int quoteCount = 0;
        for (int i = 0; i < searchArea.Length; i++)
        {
            if (searchArea[i] is '"' or '\'') quoteCount++;
        }
        return quoteCount > 100;
    }

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
            var path = Path.Combine(DiagFolder,
                $"player_{CurrentPlayerVersion}_{type}_error.txt");
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

    public void FlushCache() => Cache.SaveAsync().GetAwaiter().GetResult();

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
        _initSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    // ═══════════════════════════════════════════════════════════════
    // BROWSER STUBS
    // ═══════════════════════════════════════════════════════════════

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
            createElement: function(tag) {
                var el = {
                    tagName: (tag||'').toUpperCase(),
                    style: {},
                    setAttribute: function(){},
                    getAttribute: function(){ return null; },
                    addEventListener: function(){},
                    removeEventListener: function(){},
                    appendChild: function(c){ return c; },
                    removeChild: function(c){ return c; },
                    insertBefore: function(c){ return c; },
                    cloneNode: function(){ return el; },
                    querySelector: function(){ return null; },
                    querySelectorAll: function(){ return []; },
                    getElementsByTagName: function(){ return []; },
                    innerHTML: '', textContent: '', className: '',
                    href: '', src: '', id: '',
                    parentNode: null, firstChild: null, lastChild: null,
                    nextSibling: null, previousSibling: null,
                    childNodes: [], children: [],
                    offsetWidth: 0, offsetHeight: 0,
                    clientWidth: 0, clientHeight: 0
                };
                return el;
            }, 
            getElementById: function() { return null; },
            querySelector: function() { return null; },
            querySelectorAll: function() { return []; },
            getElementsByTagName: function() { return []; },
            getElementsByClassName: function() { return []; },
            createElementNS: function() { return document.createElement(''); },
            createTextNode: function(t) { return { textContent: t, nodeType: 3 }; },
            createDocumentFragment: function() { return { appendChild: function(c){return c;}, childNodes: [] }; },
            head: { appendChild: function(){} },
            body: { appendChild: function(){}, removeChild: function(){} },
            documentElement: { style: {} },
            readyState: 'complete',
            cookie: '',
            title: '',
            domain: 'www.youtube.com'
        };
        window.document = document;
        var navigator = { 
            userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36', 
            platform: 'Win32',
            language: 'en-US',
            languages: ['en-US', 'en'],
            onLine: true,
            cookieEnabled: true,
            doNotTrack: null,
            maxTouchPoints: 0,
            hardwareConcurrency: 8
        }; 
        window.navigator = navigator;
        window.setTimeout = window.setInterval = function(f) { try { if (typeof f === 'function') f(); } catch(e) {} return 0; };
        window.clearTimeout = window.clearInterval = function() {};
        window.requestAnimationFrame = function(f) { try { f(0); } catch(e) {} return 0; };
        window.cancelAnimationFrame = function() {};
        window.getComputedStyle = function() { return new Proxy({}, { get: function() { return ''; } }); };
        window.matchMedia = function() { return { matches: false, addListener: function(){}, removeListener: function(){} }; };
        window.innerWidth = 1920; window.innerHeight = 1080;
        window.screen = { width: 1920, height: 1080, colorDepth: 24 };
        window.devicePixelRatio = 1;
        window.performance = { 
            now: function() { return Date.now(); },
            timing: { navigationStart: Date.now() },
            mark: function(){}, measure: function(){}, getEntriesByName: function(){ return []; }
        };
        window.URL = window.webkitURL = { 
            createObjectURL: function() { return ''; }, 
            revokeObjectURL: function() {} 
        };
        window.MutationObserver = window.WebKitMutationObserver = function() { 
            this.observe = this.disconnect = this.takeRecords = function() {}; 
        };
        window.ResizeObserver = function() { this.observe = this.unobserve = this.disconnect = function() {}; };
        window.IntersectionObserver = function() { this.observe = this.unobserve = this.disconnect = function() {}; };
        var _storageData = {};
        var localStorage = {
            getItem: function(k) { return _storageData[k] || null; },
            setItem: function(k, v) { _storageData[k] = String(v); },
            removeItem: function(k) { delete _storageData[k]; },
            clear: function() { _storageData = {}; },
            get: function(k) { return _storageData[k] || null; },
            key: function(i) { return Object.keys(_storageData)[i] || null; },
            get length() { return Object.keys(_storageData).length; }
        };
        window.localStorage = localStorage;
        window.sessionStorage = {
            getItem: function(k) { return null; },
            setItem: function() {},
            removeItem: function() {},
            clear: function() {},
            get length() { return 0; }
        };
        var XMLHttpRequest = function() {
            this.open = this.send = this.setRequestHeader = function() {};
            this.readyState = 4; this.status = 200; this.responseText = '';
            this.addEventListener = function() {};
        };
        var fetch = function() { return Promise.resolve({ ok: true, json: function() { return Promise.resolve({}); }, text: function() { return Promise.resolve(''); } }); };
        var ytcfg = { get: function(k, d) { return d !== void 0 ? d : ''; }, set: function() {}, d: function() { return ''; } };
        var yt = { config_: {}, logging: { errors: [] } };
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
        var Image = function() { this.src = ''; this.onload = this.onerror = null; };
        window.Image = Image;
        var TextEncoder = function() { this.encode = function(s) { return new Uint8Array(0); }; };
        var TextDecoder = function() { this.decode = function() { return ''; }; };
        window.TextEncoder = TextEncoder;
        window.TextDecoder = TextDecoder;
        window.crypto = { getRandomValues: function(a) { for(var i=0;i<a.length;i++) a[i]=Math.floor(Math.random()*256); return a; }, subtle: _sink };
        window.atob = function(s) { return ''; };
        window.btoa = function(s) { return ''; };
        if (typeof BigInt === 'undefined') {
            var BigInt = function(v) { return Number(v); };
            BigInt.asIntN = function(bits, n) { return Number(n); };
            BigInt.asUintN = function(bits, n) { return Number(n); };
            window.BigInt = BigInt;
        }
        """;
}