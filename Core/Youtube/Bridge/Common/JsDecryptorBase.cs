using System.Text;
using Jint;

namespace LMP.Core.Youtube.Bridge.Common;

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
        Func<string, string, string?>? buildBundle = null)
    {
        // Оборачиваем в новую сигнатуру, игнорируя MagicNumbers
        return TryInitJsEngines(
            context,
            funcName,
            MagicNumbers.None,
            (fn, _) => buildWrapperScript(fn),
            testInput,
            buildBundle);
    }

    /// <summary>
    /// Инициализация JS-движков С magic numbers.
    /// Используется NTokenDecryptor для передачи числовых аргументов
    /// перед N-токеном: <c>f(6, 4494, nToken)</c>.
    /// </summary>
    internal bool TryInitJsEngines(
        PlayerContext context,
        string funcName,
        MagicNumbers magic,
        Func<string, MagicNumbers, string> buildWrapperScript,
        string testInput,
        Func<string, string, string?>? buildBundle = null)
    {
        var wrapperScript = buildWrapperScript(funcName, magic);
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
                Log.Info($"[{DecryptorName}] Bundle ready ({bundle.Length / 1024}KB, {magic})");
                return true;
            }
        }

        Log.Debug($"[{DecryptorName}] Bundle failed, trying full JS...");

        // 2. Full JS with window export injection
        var modifiedJs = InjectWindowExport(context.BaseJs, funcName);
        if (TryInitFull(modifiedJs, wrapperScript, wrapperFuncName, testInput))
        {
            Log.Info($"[{DecryptorName}] Full JS ready ({context.BaseJs.Length / 1024}KB, {magic})");
            return true;
        }

        // 3. Fallback: если magic numbers были указаны, пробуем без них
        if (magic.HasArgs)
        {
            Log.Debug($"[{DecryptorName}] Retrying without magic numbers...");
            var fallbackWrapper = buildWrapperScript(funcName, MagicNumbers.None);

            if (bundle is not null && TryInitBundle(bundle, fallbackWrapper, wrapperFuncName, testInput))
            {
                Log.Info($"[{DecryptorName}] Bundle ready WITHOUT magic numbers (fallback)");
                return true;
            }

            if (TryInitFull(modifiedJs, fallbackWrapper, wrapperFuncName, testInput))
            {
                Log.Info($"[{DecryptorName}] Full JS ready WITHOUT magic numbers (fallback)");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Пробует инициализировать JS-движки с несколькими вариантами magic numbers.
    /// Первый успешно инициализированный вариант побеждает.
    /// </summary>
    internal bool TryInitJsEnginesWithCandidates(
        PlayerContext context,
        string funcName,
        List<MagicNumbers> candidates,
        Func<string, MagicNumbers, string> buildWrapperScript,
        string testInput,
        Func<string, string, string?>? buildBundle = null)
    {
        if (candidates.Count == 0)
            candidates = [MagicNumbers.None];

        foreach (var magic in candidates)
        {
            Log.Debug($"[{DecryptorName}] Trying magic: {magic}");

            if (TryInitJsEngines(context, funcName, magic, buildWrapperScript, testInput, buildBundle))
                return true;

            // Очищаем движки перед следующей попыткой
            BundleEngine?.Dispose();
            BundleEngine = null;
            BundleFuncName = null;
            FullEngine?.Dispose();
            FullEngine = null;
            FullFuncName = null;
        }

        return false;
    }

    /// <summary>
    /// Строит минимальный JS-бандл для функции дешифрации.
    /// 
    /// Стратегия:
    /// 1. Находит глобальный словарь строк (split/bracket)
    /// 2. Передаёт имя словаря в ExtractBundle как "внешнее" имя,
    ///    чтобы оно НЕ перезаписывалось guard var'ом <c>var O=0;</c>
    /// 3. Извлекает функцию и все её зависимости
    /// 4. Prepend'ит словарь если он не включён в бандл
    /// 
    /// НЕ резолвит обращения к словарю (h[4] → "value") —
    /// оставляет runtime-обращения как есть для корректной работы
    /// с динамическими индексами и XOR-masked ключами.
    /// </summary>
    protected virtual string? BuildDefaultBundle(string baseJs, string funcName)
    {
        // ═══ Находим словарь ПЕРЕД извлечением бандла ═══
        // Нужно знать его имя, чтобы передать в ExtractBundle как external
        var dictCode = FindStringDictionary(baseJs);
        string? dictVarName = null;

        if (dictCode is not null)
        {
            // Извлекаем имя переменной словаря: "var X=..." → "X"
            dictVarName = ExtractVarNameFromDeclaration(dictCode);
        }

        // ═══ Собираем набор "внешних" имён ═══
        HashSet<string>? externalNames = null;
        if (dictVarName is not null)
        {
            externalNames = [dictVarName];
        }

        var extracted = JsFunctionExtractor.ExtractBundle(baseJs, funcName, externalNames);
        if (extracted is null) return null;

        // ═══ Check if dictionary definition is included ═══
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

    /// <summary>
    /// Извлекает имя переменной из объявления <c>var NAME=...</c>.
    /// Возвращает <c>null</c> если формат не распознан.
    /// </summary>
    private static string? ExtractVarNameFromDeclaration(string declaration)
    {
        var span = declaration.AsSpan().TrimStart();

        // Пропускаем "var "
        if (span.StartsWith("var "))
            span = span[4..];
        else if (span.StartsWith("let "))
            span = span[4..];
        else if (span.StartsWith("const "))
            span = span[6..];
        else
            return null;

        span = span.TrimStart();

        // Читаем идентификатор
        int i = 0;
        while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] is '_' or '$'))
            i++;

        if (i == 0) return null;

        return span[..i].ToString();
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
    /// Инжектирует window['funcName']=funcName; в base.js.
    /// 
    /// Стратегии (в порядке приоритета):
    /// 1. Точное определение через FindFunctionByName → safe insertion point
    /// 2. Pattern search для function( 
    /// 3. Generic name= search
    /// 4. Вставка перед концом IIFE
    /// 5. Конец файла
    /// </summary>
    protected static string InjectWindowExport(string baseJs, string funcName)
    {
        var export = $"\nwindow['{funcName}']={funcName};\n";

        try
        {
            // ═══ Strategy 1: Точное определение ═══
            var funcInfo = JsFunctionExtractor.FindFunctionByName(baseJs, funcName);
            if (funcInfo is not null)
            {
                int endOfDef = funcInfo.Value.Position + funcName.Length + 1 + funcInfo.Value.Code.Length;
                int safePos = FindSafeInsertionPoint(baseJs, endOfDef);

                Log.Debug($"[JsDecryptor] Injecting window export for '{funcName}' " +
                          $"at safe position {safePos} (def ended at {endOfDef})");
                return baseJs.Insert(safePos, export);
            }

            // ═══ Strategy 2: function( pattern ═══
            var pattern = $"{funcName}=function(";
            int patternIdx = baseJs.IndexOf(pattern, StringComparison.Ordinal);
            if (patternIdx >= 0 && (patternIdx == 0 || !JsFunctionExtractor.IsIdentChar(baseJs[patternIdx - 1])))
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

            // ═══ Strategy 3: name= generic search ═══
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

            // ═══ Strategy 4: IIFE end ═══
            int fallbackPos = FindEndOfIIFE(baseJs);
            if (fallbackPos > 0)
            {
                Log.Debug($"[JsDecryptor] Injecting window export for '{funcName}' " +
                          $"at IIFE end (fallback), position {fallbackPos}");
                return baseJs.Insert(fallbackPos, export);
            }

            // ═══ Strategy 5: EOF ═══
            Log.Warn($"[JsDecryptor] Using end-of-file fallback for '{funcName}'");
            return baseJs + export;
        }
        catch (Exception ex)
        {
            Log.Warn($"[JsDecryptor] Window export injection failed: {ex.Message}");
        }

        return baseJs;
    }

    /// <summary>
    /// Находит безопасную точку вставки после заданной позиции.
    /// Ищет ближайший `;` или `}` которые завершают statement.
    /// </summary>
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
    // STRING DICTIONARY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Находит определение глобального словаря строк.
    /// Поддерживает:
    ///   1. var q = '...'.split("}") — split-стиль
    ///   2. var h = ["...", "...", ...] — bracket-стиль (новый формат YouTube)
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

            Log.Debug($"[JsDecryptor] Found split dictionary '{varName}', {separatorCount + 1} elements");
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
                if (c is '"' or '\'' or '`') { i = JsFunctionExtractor.SkipString(baseJs, i) - 1; continue; }
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

            Log.Debug($"[JsDecryptor] Found bracket dictionary '{varName}', {elementCount} elements");
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

        // Bracket array detection — count quotes
        int quoteCount = 0;
        for (int i = 0; i < searchArea.Length; i++)
        {
            if (searchArea[i] is '"' or '\'') quoteCount++;
        }
        return quoteCount > 100;
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