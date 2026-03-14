using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Bridge.Common;

internal static partial class JsFunctionExtractor
{
    // ═══════════════════════════════════════════════════════════════
    // SEARCH VALUES — cached character sets for hot-path scanning
    // ═══════════════════════════════════════════════════════════════

    private static readonly SearchValues<char> s_quoteChars =
        SearchValues.Create("\"'`");

    private static readonly SearchValues<char> s_identStartChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_$");

    /// <summary>
    /// Символы, которые могут предшествовать regex литералу в JS.
    /// После этих символов `/` интерпретируется как начало regex, а не оператор деления.
    /// Основано на спецификации ECMAScript (§12.9.5 Rules of Automatic Semicolon Insertion).
    /// </summary>
    private static readonly SearchValues<char> s_regexPrecedingChars =
        SearchValues.Create("=(<>!&|^~?:;,{}[+-%*/\n\r");

    private static readonly FrozenSet<string> SkipNames = FrozenSet.ToFrozenSet(
    [
        "var", "let", "const", "function", "return", "if", "else", "for", "while",
        "do", "switch", "case", "break", "continue", "try", "catch", "finally",
        "throw", "new", "delete", "typeof", "void", "in", "instanceof", "this",
        "true", "false", "null", "undefined", "of", "class", "extends", "yield",
        "async", "await", "with", "default", "import", "export",
        "String", "Array", "Object", "Math", "Date", "Number", "Boolean",
        "RegExp", "Error", "JSON", "console", "parseInt", "parseFloat",
        "isNaN", "isFinite", "Infinity", "NaN", "arguments",
        "Proxy", "Symbol", "Promise", "Uint8Array", "Int32Array",
        "Float32Array", "Float64Array", "Map", "Set", "WeakMap", "WeakSet",
        "decodeURIComponent", "encodeURIComponent", "decodeURI", "encodeURI",
        "window", "document", "navigator", "location", "history",
        "setTimeout", "setInterval", "clearTimeout", "clearInterval",
        "fetch", "XMLHttpRequest", "Image", "Blob", "URL", "Event",
        "g", "ytcfg", "yt",
        "name", "url", "path", "type", "value", "data", "key", "id",
        "length", "index", "count", "size", "width", "height",
        "top", "left", "right", "bottom", "start", "end",
        "text", "html", "body", "head", "style", "src", "href",
        "error", "result", "response", "request", "message", "status", "code",
        "prototype", "constructor", "toString", "valueOf", "hasOwnProperty",
        "call", "apply", "bind", "push", "pop", "shift", "unshift",
        "splice", "slice", "join", "split", "replace", "match", "search",
        "test", "indexOf", "lastIndexOf", "forEach", "map", "filter",
        "reduce", "concat", "sort", "reverse", "includes", "find",
        "findIndex", "every", "some", "keys", "values", "entries",
        "assign", "create", "defineProperty", "freeze",
        "parse", "stringify", "charCodeAt", "charAt", "fromCharCode",
        "setPrototypeOf", "getPrototypeOf",
        // ═══ Common property/parameter names используемые в YouTube base.js ═══
        // Эти имена часто встречаются как ключи объектов, параметры деструктуризации,
        // или callback-параметры — FindAnyDefinition находит случайные определения.
        "startTime", "ticks", "infos", "sampleRate", "timerName",
        "query", "method", "level", "chunkSize", "strategy",
        "search_query", "tkg",
    ]);

    [ThreadStatic]
    private static StringBuilder? t_concatBuilder;

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Извлекает минимальный JS-бандл: entry function + все зависимости.
    /// <para>
    /// <paramref name="externalNames"/> — имена, определённые вне бандла
    /// (например, словарь O, prepended отдельно). Они не будут:
    /// - искаться как зависимости
    /// - добавляться как guard vars
    /// - перезаписываться var X=0;
    /// </para>
    /// </summary>
    public static string? ExtractBundle(string fullJs, string entryFuncName,
        IReadOnlySet<string>? externalNames = null)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var entryDef = FindAnyDefinition(fullJs, entryFuncName);
            if (entryDef is null)
            {
                Log.Debug($"[JsExtractor] Entry function '{entryFuncName}' not found");
                return null;
            }

            Log.Debug($"[JsExtractor] Entry '{entryFuncName}' definition: {entryDef.Length} chars, " +
                       $"preview: {Truncate(entryDef, 120)}");

            var dictName = DetectDictArrayName(entryDef);

            // ═══ Thin wrapper fallback — multi-level ═══
            if (dictName is null)
            {
                dictName = FindDictArrayInDependencies(fullJs, entryDef, maxDepth: 3);
            }

            string? dictArrayCode = null;

            if (dictName is not null)
            {
                dictArrayCode = FindDictArrayDefinition(fullJs, dictName);
                if (dictArrayCode is not null)
                    Log.Debug($"[JsExtractor] Dict array '{dictName}': {dictArrayCode.Length} chars");
                else
                    Log.Warn($"[JsExtractor] Dict array '{dictName}' definition not found in base.js");
            }
            else
            {
                Log.Debug($"[JsExtractor] No dict array detected in entry or dependencies");
            }

            // ═══ Собираем параметры entry function — они НЕ являются зависимостями ═══
            var entryParams = ExtractParamNames(entryDef);

            var definitions = new Dictionary<string, string>(128);
            var visited = new HashSet<string>(SkipNames);
            var notFound = new HashSet<string>();

            if (dictName is not null) visited.Add(dictName);

            // ═══ Внешние имена (словарь и т.д.) — не трогаем ═══
            if (externalNames is not null)
            {
                foreach (var extName in externalNames)
                    visited.Add(extName);
            }

            // ═══ Однобуквенные параметры entry — пропускаем как зависимости,
            // т.к. FindAnyDefinition найдёт случайные определения из других функций ═══
            foreach (var param in entryParams)
            {
                if (param.Length <= 2)
                    visited.Add(param);
            }

            var queue = new Queue<string>();
            queue.Enqueue(entryFuncName);

            int iterations = 0;
            const int maxIterations = 300;

            var defBuilder = new StringBuilder(512);

            while (queue.Count > 0 && iterations++ < maxIterations)
            {
                var currentName = queue.Dequeue();
                if (!visited.Add(currentName)) continue;

                var def = FindAnyDefinition(fullJs, currentName);

                if (def is null)
                {
                    notFound.Add(currentName);
                    continue;
                }

                if (!IsValidExtractedDefinition(def, currentName))
                {
                    Log.Debug($"[JsExtractor] Skipping invalid definition for '{currentName}': " +
                              $"{Truncate(def, 80)}");
                    notFound.Add(currentName);
                    continue;
                }

                var cleanSpan = def.AsSpan().TrimEnd([';', ',', ' ', '\n', '\r']);

                defBuilder.Clear();
                defBuilder.Append("var ");
                defBuilder.Append(currentName);
                defBuilder.Append('=');
                defBuilder.Append(cleanSpan);
                defBuilder.Append(';');
                definitions[currentName] = defBuilder.ToString();

                // ═══ Извлекаем зависимости, но фильтруем параметры ═══
                var depParams = ExtractParamNames(def);
                foreach (var dep in FindReferencedNames(def))
                {
                    // Пропускаем параметры ЭТОЙ функции — они локальные
                    if (depParams.Contains(dep))
                        continue;

                    // Пропускаем однобуквенные имена, которые скорее всего
                    // являются параметрами/переменными вложенных функций.
                    // Такие имена FindAnyDefinition найдёт из случайного места base.js.
                    if (dep.Length == 1 && char.IsLower(dep[0]))
                        continue;

                    if (!visited.Contains(dep))
                        queue.Enqueue(dep);
                }
            }

            if (definitions.Count < 3)
            {
                Log.Warn($"[JsExtractor] Too few definitions ({definitions.Count}), " +
                         $"entry='{entryFuncName}', iterations={iterations}");
            }

            var guardVars = FindTypeofGuardVars(entryDef, definitions, dictName);
            foreach (var def in definitions.Values)
            {
                foreach (var guardVar in FindTypeofGuardVars(def, definitions, dictName))
                    guardVars.Add(guardVar);
            }

            foreach (var name in notFound)
            {
                if (name.Length <= 4
                    && !guardVars.Contains(name)
                    && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
                    && name.Any(c => char.IsUpper(c) || c is '_' or '$'))
                {
                    guardVars.Add(name);
                }
            }

            // ═══ КРИТИЧНО: убираем из guard vars имена, которые уже определены извне ═══
            if (externalNames is not null)
            {
                foreach (var extName in externalNames)
                    guardVars.Remove(extName);
            }

            // ═══ Убираем имена, которые уже есть в definitions ═══
            guardVars.ExceptWith(definitions.Keys);

            if (guardVars.Count > 0)
                Log.Debug($"[JsExtractor] Guard vars ({guardVars.Count}): {string.Join(", ", guardVars.Take(20))}");

            // ═══════════════════════════════════════════════════════
            // BUILD BUNDLE
            // ═══════════════════════════════════════════════════════

            var totalSize = (dictArrayCode?.Length ?? 0)
                          + definitions.Values.Sum(static d => d.Length)
                          + guardVars.Count * 16
                          + 1024;

            var safeDefs = new List<KeyValuePair<string, string>>();
            var unsafeDefs = new List<KeyValuePair<string, string>>();

            foreach (var kv in definitions)
            {
                if (string.Equals(kv.Key, entryFuncName, StringComparison.Ordinal))
                    continue;

                if (IsSafeForTryCatchWrap(kv.Value))
                    safeDefs.Add(kv);
                else
                    unsafeDefs.Add(kv);
            }

            if (unsafeDefs.Count > 0)
            {
                Log.Debug($"[JsExtractor] Unsafe defs (no try-catch wrap): " +
                          $"{string.Join(", ", unsafeDefs.Select(kv => kv.Key))}");
            }

            var sb = new StringBuilder(totalSize);

            if (dictArrayCode is not null) sb.AppendLine(dictArrayCode);

            foreach (var guardVar in guardVars)
                sb.Append("var ").Append(guardVar).AppendLine("=0;");

            foreach (var kv in unsafeDefs)
            {
                sb.AppendLine(kv.Value);
            }

            foreach (var kv in safeDefs)
            {
                sb.Append("try{").Append(kv.Value).AppendLine("}catch(e$){}");
            }

            // 5. Entry function + экспорт
            if (definitions.TryGetValue(entryFuncName, out var entryCode))
            {
                sb.AppendLine(entryCode);
                sb.Append("window['").Append(entryFuncName).Append("']=")
                  .Append(entryFuncName).AppendLine(";");
            }
            else
            {
                Log.Warn($"[JsExtractor] Entry function '{entryFuncName}' not found in definitions!");
            }

            sw.Stop();
            var result = sb.ToString();
            var defCount = definitions.Count + (dictArrayCode is not null ? 1 : 0);
            var reduction = fullJs.Length > 0 ? 100 - result.Length * 100 / fullJs.Length : 0;

            Log.Info($"[JsExtractor] Extracted {defCount} definitions, " +
                     $"{fullJs.Length / 1024}KB -> {result.Length / 1024}KB " +
                     $"({reduction}% reduction) in {sw.ElapsedMilliseconds}ms");

            if (notFound.Count > 0)
                Log.Debug($"[JsExtractor] Not found ({notFound.Count}): " +
                          $"{string.Join(", ", notFound.Take(30))}");

            return result;
        }
        catch (Exception ex)
        {
            Log.Debug($"[JsExtractor] Extraction failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Ищет dict array name в зависимостях entry function до заданной глубины.
    /// </summary>
    private static string? FindDictArrayInDependencies(string fullJs, string entryDef, int maxDepth)
    {
        var visited = new HashSet<string>(SkipNames);
        var currentLevel = FindReferencedNames(entryDef);

        for (int depth = 0; depth < maxDepth; depth++)
        {
            var nextLevel = new HashSet<string>();

            foreach (var depName in currentLevel)
            {
                if (!visited.Add(depName)) continue;

                var depDef = FindAnyDefinition(fullJs, depName);
                if (depDef is null) continue;

                var dictName = DetectDictArrayName(depDef);
                if (dictName is not null)
                {
                    Log.Debug($"[JsExtractor] Dict array '{dictName}' found via " +
                              $"dependency '{depName}' at depth {depth + 1}");
                    return dictName;
                }

                foreach (var nextDep in FindReferencedNames(depDef))
                {
                    if (!visited.Contains(nextDep))
                        nextLevel.Add(nextDep);
                }
            }

            if (nextLevel.Count == 0) break;
            currentLevel = nextLevel;
        }

        return null;
    }

    /// <summary>
    /// Валидирует извлечённое определение перед вставкой в бандл.
    /// 
    /// КЛЮЧЕВОЙ ПРИНЦИП:
    /// Для функций (начинающихся с "function") мы ДОВЕРЯЕМ FindMatchingBrace —
    /// если он нашёл парную скобку, определение корректно. HasBalancedBraces
    /// может давать false-positive на regex литералах в минифицированном JS,
    /// поэтому мы НЕ проверяем баланс для функций.
    /// 
    /// HasBalancedBraces проверяется только для коротких value определений,
    /// где SkipValue мог обрезать код.
    /// </summary>
    private static bool IsValidExtractedDefinition(string definition, string name)
    {
        if (string.IsNullOrWhiteSpace(definition)) return false;

        var span = definition.AsSpan().TrimEnd([';', ',', ' ', '\n', '\r']);
        if (span.Length == 0) return false;

        // ═══ ФУНКЦИИ: доверяем FindMatchingBrace ═══
        // FindFunctionDefinition, FindDestructuringFunctionDefinition и FindArrowFunctionDefinition
        // используют FindMatchingBrace/FindMatchingParen для нахождения тела.
        // Если они вернули результат — скобки парные. 
        // HasBalancedBraces может ломаться на regex литералах (`;/pattern/flags`)
        // поэтому НЕ вызываем его для функций.

        if (span.StartsWith("function"))
        {
            // Функция должна заканчиваться на }
            if (span[^1] != '}')
            {
                Log.Debug($"[JsExtractor] Definition '{name}' is incomplete function (not ending with }})");
                return false;
            }

            // Проверяем что parameter list закрыт
            int parenStart = span.IndexOf('(');
            if (parenStart >= 0)
            {
                int parenEnd = FindMatchingParen(definition, parenStart);
                if (parenEnd < 0)
                {
                    Log.Debug($"[JsExtractor] Definition '{name}' has unclosed parameter list");
                    return false;
                }
            }

            return true; // Доверяем FindMatchingBrace
        }

        // Arrow function с блочным телом
        if (span.Contains("=>", StringComparison.Ordinal))
        {
            int arrowIdx = span.IndexOf("=>");
            int afterArrow = arrowIdx + 2;
            while (afterArrow < span.Length && span[afterArrow] is ' ' or '\t') afterArrow++;

            if (afterArrow < span.Length && span[afterArrow] == '{')
            {
                // Блочное тело — должно заканчиваться на }
                if (span[^1] != '}')
                {
                    Log.Debug($"[JsExtractor] Definition '{name}' is incomplete arrow function");
                    return false;
                }
                return true; // Доверяем FindMatchingBrace
            }
        }

        // ═══ VALUE определения: проверяем баланс скобок ═══
        // Для коротких определений HasBalancedBraces надёжен (нет regex проблем).
        // Для длинных (> 10KB) value определений — тоже доверяем FindMatchingBrace,
        // т.к. они были извлечены через FindObjectDefinition с matching.
        if (span.Length < 10_000 && !HasBalancedBraces(span))
        {
            Log.Debug($"[JsExtractor] Definition '{name}' has unbalanced braces ({span.Length} chars)");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Проверяет, безопасно ли оборачивать определение в try{...}catch(e){}.
    /// </summary>
    private static bool IsSafeForTryCatchWrap(string definition)
    {
        if (!HasBalancedBracesForWrap(definition.AsSpan()))
        {
            return false;
        }

        if (HasUnmatchedTopLevelTry(definition))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Облегчённая проверка баланса для try-catch обёртки.
    /// Для длинных определений (> 5KB) не проверяем — оборачиваем как unsafe.
    /// Это предотвращает false-positive от regex литералов.
    /// </summary>
    private static bool HasBalancedBracesForWrap(ReadOnlySpan<char> code)
    {
        // Длинные определения (функции с regex внутри) — не оборачиваем
        if (code.Length > 5000) return false;
        return HasBalancedBraces(code);
    }

    public static string[]? ExtractArrayElements(string fullJs, string name)
    {
        var def = FindDictArrayDefinition(fullJs, name);
        if (def is null) return null;

        var defSpan = def.AsSpan();

        if (TryParseSplitExpression(defSpan, out var content, out var separator))
            return SplitToArray(content, separator);

        var bracketStart = defSpan.IndexOf('[');
        if (bracketStart >= 0)
        {
            var bracketEnd = defSpan.LastIndexOf(']');
            if (bracketEnd > bracketStart)
            {
                var inner = defSpan.Slice(bracketStart + 1, bracketEnd - bracketStart - 1);
                return SplitBracketElements(inner);
            }
        }

        return null;
    }

    /// <summary>
    /// Определяет имя массива-словаря в коде функции.
    /// Ищет паттерн <c>name[число]</c>, исключая:
    /// - параметры функции
    /// - стандартные JS-имена
    /// - однобуквенные имена, совпадающие с распространёнными локальными переменными
    /// 
    /// ВАЖНО: Этот метод может ложно определить локальную переменную
    /// как dict array, если она часто индексируется числами.
    /// Вызывающий код должен проверять, что найденное имя существует
    /// как глобальное определение в base.js.
    /// </summary>
    public static string? DetectDictArrayName(string funcCode)
    {
        var paramNames = ExtractParamNames(funcCode);
        var countDict = new Dictionary<string, int>(8);

        // ═══ Собираем все локальные var-имена из тела функции ═══
        // Они не могут быть глобальным словарём
        var localVars = ExtractLocalVarNames(funcCode);

        var span = funcCode.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            int bracketPos = span[i..].IndexOf('[');
            if (bracketPos < 0) break;
            bracketPos += i;

            int afterBracket = bracketPos + 1;
            if (afterBracket < span.Length && char.IsAsciiDigit(span[afterBracket]))
            {
                int digitEnd = afterBracket;
                while (digitEnd < span.Length && char.IsAsciiDigit(span[digitEnd])) digitEnd++;

                if (digitEnd < span.Length && span[digitEnd] == ']')
                {
                    int nameEnd = bracketPos;
                    int nameStart = nameEnd - 1;
                    while (nameStart >= 0 && IsIdentCharStrict(span[nameStart])) nameStart--;
                    nameStart++;

                    int nameLen = nameEnd - nameStart;
                    if (nameLen >= 1 &&
                        (nameStart == 0 || !IsIdentCharStrict(span[nameStart - 1])))
                    {
                        var arrName = span.Slice(nameStart, nameLen).ToString();

                        // ═══ Фильтрация: пропускаем параметры, локальные переменные,
                        // стандартные имена и однобуквенные строчные (скорее всего локальные) ═══
                        if (!paramNames.Contains(arrName)
                            && !SkipNames.Contains(arrName)
                            && !localVars.Contains(arrName))
                        {
                            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                countDict, arrName, out _);
                            count++;
                        }
                    }
                }
            }

            i = bracketPos + 1;
        }

        string? best = null;
        int bestCount = 1;
        foreach (var kv in countDict)
        {
            if (kv.Value > bestCount)
            {
                bestCount = kv.Value;
                best = kv.Key;
            }
        }

        if (best is not null)
            Log.Debug($"[JsExtractor] DetectDictArrayName: best='{best}' count={bestCount}");

        return best;
    }

    /// <summary>
    /// Извлекает имена локальных переменных, объявленных через <c>var</c> в теле функции.
    /// Не рекурсирует во вложенные функции.
    /// Используется для фильтрации ложных dict array candidates.
    /// </summary>
    private static HashSet<string> ExtractLocalVarNames(string funcCode)
    {
        var result = new HashSet<string>(8);
        var span = funcCode.AsSpan();

        // Ищем "var " после первого { (начало тела функции)
        int bodyStart = span.IndexOf('{');
        if (bodyStart < 0) return result;

        int i = bodyStart + 1;
        int braceDepth = 0; // глубина вложенных функций

        while (i < span.Length - 4)
        {
            char c = span[i];

            // Пропускаем строки
            if (c is '"' or '\'' or '`')
            {
                i = SkipString(funcCode, i);
                continue;
            }

            // Отслеживаем вложенные function(){} — не извлекаем их var
            if (c == 'f' && i + 8 <= span.Length
                && span.Slice(i, 8).SequenceEqual("function")
                && (i == 0 || !IsIdentCharStrict(span[i - 1])))
            {
                braceDepth++;
                i += 8;
                continue;
            }

            // Arrow function с блочным телом
            if (c == '=' && i + 1 < span.Length && span[i + 1] == '>')
            {
                int afterArrow = i + 2;
                while (afterArrow < span.Length && span[afterArrow] is ' ' or '\t') afterArrow++;
                if (afterArrow < span.Length && span[afterArrow] == '{')
                    braceDepth++;
                i = afterArrow;
                continue;
            }

            if (c == '{' && braceDepth > 0) { braceDepth++; i++; continue; }
            if (c == '}' && braceDepth > 0) { braceDepth--; i++; continue; }

            // Внутри вложенной функции — пропускаем
            if (braceDepth > 0) { i++; continue; }

            // Ищем "var "
            if (c == 'v' && i + 4 <= span.Length
                && span.Slice(i, 4).SequenceEqual("var ")
                && (i == 0 || !IsIdentCharStrict(span[i - 1])))
            {
                i += 4;
                // Может быть несколько переменных через запятую: var a, b, c=...
                while (i < span.Length)
                {
                    while (i < span.Length && span[i] is ' ' or '\t') i++;

                    int nameStart = i;
                    while (i < span.Length && IsIdentCharStrict(span[i])) i++;

                    if (i > nameStart)
                        result.Add(span[nameStart..i].ToString());

                    // Пропускаем до , или ;
                    while (i < span.Length && span[i] is ' ' or '\t') i++;

                    if (i < span.Length && span[i] == '=')
                    {
                        // Пропускаем значение (может содержать , внутри скобок)
                        i++;
                        int depth = 0;
                        while (i < span.Length)
                        {
                            char vc = span[i];
                            if (vc is '"' or '\'' or '`') { i = SkipString(funcCode, i); continue; }
                            if (vc is '(' or '[' or '{') depth++;
                            else if (vc is ')' or ']' or '}') { if (depth == 0) break; depth--; }
                            else if (vc == ',' && depth == 0) break;
                            else if (vc == ';' && depth == 0) break;
                            i++;
                        }
                    }

                    if (i >= span.Length) break;
                    if (span[i] == ',') { i++; continue; } // следующая переменная
                    break; // ; или другой символ
                }
                continue;
            }

            i++;
        }

        return result;
    }

    public static HashSet<string> ExtractParamNames(string funcCode)
    {
        var result = new HashSet<string>(4);
        var span = funcCode.AsSpan();

        if (span.StartsWith("function"))
        {
            int parenStart = span.IndexOf('(');
            if (parenStart >= 0)
            {
                int parenEnd = FindMatchingParen(funcCode, parenStart);
                if (parenEnd > parenStart)
                {
                    ExtractParamNamesFromParens(
                        span.Slice(parenStart + 1, parenEnd - parenStart - 1), result);
                    return result;
                }
            }
        }

        int openParen = -1;
        if (span.Length > 0 && span[0] == '(')
            openParen = 0;
        else if (span.StartsWith("async ") || span.StartsWith("async\t"))
            openParen = span.IndexOf('(');

        if (openParen >= 0)
        {
            int closeParen = FindMatchingParen(funcCode, openParen);
            if (closeParen > openParen)
            {
                ExtractParamNamesFromParens(
                    span.Slice(openParen + 1, closeParen - openParen - 1), result);
                return result;
            }
        }

        int arrowIdx = span.IndexOf("=>");
        if (arrowIdx > 0)
        {
            var paramPart = span[..arrowIdx].Trim();
            if (paramPart.Length > 0 && s_identStartChars.Contains(paramPart[0]))
                result.Add(paramPart.ToString());
        }

        return result;
    }

    /// <summary>
    /// Извлекает имена параметров из содержимого скобок, включая destructuring.
    /// Поддерживает: a, b, {c:d, e:f}, [g, h], ...rest, defaults (x=1)
    /// </summary>
    private static void ExtractParamNamesFromParens(ReadOnlySpan<char> paramsSpan, HashSet<string> result)
    {
        int i = 0;
        while (i < paramsSpan.Length)
        {
            while (i < paramsSpan.Length && char.IsWhiteSpace(paramsSpan[i])) i++;
            if (i >= paramsSpan.Length) break;

            char c = paramsSpan[i];

            if (c == '.' && i + 2 < paramsSpan.Length && paramsSpan[i + 1] == '.' && paramsSpan[i + 2] == '.')
            {
                i += 3;
                continue;
            }

            if (c == '{')
            {
                int depth = 1;
                i++;
                while (i < paramsSpan.Length && depth > 0)
                {
                    if (paramsSpan[i] == '{') depth++;
                    else if (paramsSpan[i] == '}') depth--;
                    if (depth > 0) i++;
                }
                if (i < paramsSpan.Length) i++;
                continue;
            }
            if (c == '[')
            {
                int depth = 1;
                i++;
                while (i < paramsSpan.Length && depth > 0)
                {
                    if (paramsSpan[i] == '[') depth++;
                    else if (paramsSpan[i] == ']') depth--;
                    if (depth > 0) i++;
                }
                if (i < paramsSpan.Length) i++;
                continue;
            }

            if (c == ',') { i++; continue; }

            if (s_identStartChars.Contains(c))
            {
                int start = i;
                while (i < paramsSpan.Length &&
                       (char.IsLetterOrDigit(paramsSpan[i]) || paramsSpan[i] is '_' or '$'))
                    i++;

                result.Add(paramsSpan[start..i].ToString());

                while (i < paramsSpan.Length && char.IsWhiteSpace(paramsSpan[i])) i++;
                if (i < paramsSpan.Length && paramsSpan[i] == '=')
                {
                    i++;
                    int depth = 0;
                    while (i < paramsSpan.Length)
                    {
                        char vc = paramsSpan[i];
                        if (vc is '(' or '[' or '{') depth++;
                        else if (vc is ')' or ']' or '}') depth--;
                        else if (vc == ',' && depth == 0) break;
                        if (vc is '"' or '\'' or '`')
                        {
                            char q = vc;
                            i++;
                            while (i < paramsSpan.Length)
                            {
                                if (paramsSpan[i] == '\\' && i + 1 < paramsSpan.Length) { i += 2; continue; }
                                if (paramsSpan[i] == q) break;
                                i++;
                            }
                        }
                        i++;
                    }
                }
                continue;
            }

            i++;
        }
    }

    public static string? FindDictArrayDefinition(string fullJs, string name) =>
        FindBracketArrayDefinition(fullJs, name) ?? FindSplitArrayDefinition(fullJs, name);

    public static string? FindAnyDefinition(string fullJs, string name) =>
        FindFunctionDefinition(fullJs, name)
        ?? FindArrowFunctionDefinition(fullJs, name)
        ?? FindValueDefinition(fullJs, name)
        ?? FindObjectDefinition(fullJs, name);

    public static string? FindFunctionDefinition(string fullJs, string name)
    {
        var fullSpan = fullJs.AsSpan();
        var target = string.Concat(name, "=function(");
        var targetSpan = target.AsSpan();

        int searchFrom = 0;
        while (searchFrom < fullSpan.Length)
        {
            int idx = fullSpan[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += searchFrom;
            searchFrom = idx + targetSpan.Length;

            if (idx > 0 && IsIdentCharStrict(fullSpan[idx - 1])) continue;

            int funcStart = idx + name.Length + 1; // position of "function("

            // ═══ FIX: Правильно находим тело функции через FindMatchingParen ═══
            // Сначала пропускаем parameter list (который может содержать { } для destructuring)
            int parenPos = idx + target.Length - 1; // position of '('
            int parenEnd = FindMatchingParen(fullJs, parenPos);
            if (parenEnd < 0) continue;

            // Теперь ищем { тела функции ПОСЛЕ закрывающей ) параметров
            int bodySearchStart = parenEnd + 1;
            int bodyBrace = -1;
            for (int bi = bodySearchStart; bi < fullSpan.Length && bi < bodySearchStart + 50; bi++)
            {
                if (fullSpan[bi] == '{') { bodyBrace = bi; break; }
                if (fullSpan[bi] is not (' ' or '\t' or '\n' or '\r')) break;
            }
            if (bodyBrace < 0) continue;

            int braceEnd = FindMatchingBrace(fullJs, bodyBrace);
            if (braceEnd < 0) continue;

            int end = braceEnd + 1;
            if (end < fullSpan.Length && fullSpan[end] == ';') end++;

            return fullJs[funcStart..end];
        }
        return null;
    }

    public static string? FindArrowFunctionDefinition(string fullJs, string name)
    {
        var fullSpan = fullJs.AsSpan();
        var target = string.Concat(name, "=");
        var targetSpan = target.AsSpan();

        int searchFrom = 0;
        while (searchFrom < fullSpan.Length)
        {
            int idx = fullSpan[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += searchFrom;
            searchFrom = idx + targetSpan.Length;

            if (idx > 0 && IsIdentCharStrict(fullSpan[idx - 1])) continue;

            int afterEq = idx + name.Length + 1;
            if (afterEq < fullSpan.Length && fullSpan[afterEq] == '=') continue;

            int valueStart = SkipHorizontalWhitespace(fullSpan, afterEq);
            if (valueStart >= fullSpan.Length) continue;

            int arrowSearchEnd;

            if (fullSpan[valueStart] == '(')
            {
                int parenEnd = FindMatchingParen(fullJs, valueStart);
                if (parenEnd < 0) continue;

                int afterParen = SkipHorizontalWhitespace(fullSpan, parenEnd + 1);
                if (afterParen + 1 >= fullSpan.Length ||
                    fullSpan[afterParen] != '=' || fullSpan[afterParen + 1] != '>')
                    continue;

                arrowSearchEnd = afterParen + 2;
            }
            else if (char.IsLetterOrDigit(fullSpan[valueStart]) || fullSpan[valueStart] is '_' or '$')
            {
                int paramEnd = valueStart;
                while (paramEnd < fullSpan.Length &&
                       (char.IsLetterOrDigit(fullSpan[paramEnd]) || fullSpan[paramEnd] is '_' or '$'))
                    paramEnd++;

                paramEnd = SkipHorizontalWhitespace(fullSpan, paramEnd);

                if (paramEnd + 1 >= fullSpan.Length ||
                    fullSpan[paramEnd] != '=' || fullSpan[paramEnd + 1] != '>')
                    continue;

                arrowSearchEnd = paramEnd + 2;
            }
            else continue;

            int bodyStart = SkipHorizontalWhitespace(fullSpan, arrowSearchEnd);
            if (bodyStart >= fullSpan.Length) continue;

            if (fullSpan[bodyStart] == '{')
            {
                int braceEnd = FindMatchingBrace(fullJs, bodyStart);
                if (braceEnd < 0) continue;

                int end = braceEnd + 1;
                if (end < fullSpan.Length && fullSpan[end] == ';') end++;
                return fullJs[valueStart..end];
            }
            else
            {
                int exprEnd = SkipValue(fullJs, bodyStart);
                if (exprEnd <= bodyStart) continue;
                if (exprEnd < fullSpan.Length && fullSpan[exprEnd] is ';' or ',') exprEnd++;
                return fullJs[valueStart..exprEnd];
            }
        }
        return null;
    }

    public static string? FindValueDefinition(string fullJs, string name)
    {
        var fullSpan = fullJs.AsSpan();
        var target = string.Concat(name, "=");
        var targetSpan = target.AsSpan();

        int searchFrom = 0;
        while (searchFrom < fullSpan.Length)
        {
            int idx = fullSpan[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += searchFrom;
            searchFrom = idx + targetSpan.Length;

            if (idx > 0 && IsIdentCharStrict(fullSpan[idx - 1])) continue;

            int afterEq = idx + name.Length + 1;
            if (afterEq < fullSpan.Length && fullSpan[afterEq] == '=') continue;
            if (!IsStatementBoundary(fullSpan, idx)) continue;

            int valueStart = SkipHorizontalWhitespace(fullSpan, afterEq);
            if (valueStart >= fullSpan.Length) continue;

            if (valueStart + 8 <= fullSpan.Length &&
                fullSpan.Slice(valueStart, 8).SequenceEqual("function"))
                continue;
            if (IsArrowFunctionStart(fullJs, valueStart)) continue;

            if (valueStart + 5 <= fullSpan.Length &&
                fullSpan.Slice(valueStart, 5).SequenceEqual("class"))
                continue;

            int valueEnd = SkipValue(fullJs, valueStart);
            if (valueEnd <= valueStart) continue;
            if (valueEnd < fullSpan.Length && fullSpan[valueEnd] is ';' or ',') valueEnd++;

            var valueSpan = fullSpan[valueStart..valueEnd];
            if (!IsValidValue(valueSpan)) continue;

            // HasBalancedBraces только для коротких value — для длинных доверяем SkipValue
            if (valueSpan.Length < 5000 && !HasBalancedBraces(valueSpan))
                continue;

            return fullJs[valueStart..valueEnd];
        }
        return null;
    }

    /// <summary>
    /// Быстрая проверка баланса скобок в извлечённом фрагменте.
    /// Учитывает строки и комментарии — пропускает скобки внутри них.
    /// Учитывает regex литералы после операторов.
    /// </summary>
    private static bool HasBalancedBraces(ReadOnlySpan<char> code)
    {
        int braces = 0, brackets = 0, parens = 0;
        for (int i = 0; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '{': braces++; break;
                case '}': braces--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
                case '(': parens++; break;
                case ')': parens--; break;
                case '/' when i + 1 < code.Length:
                    if (code[i + 1] == '/')
                    {
                        // Line comment
                        i++;
                        while (i < code.Length && code[i] != '\n') i++;
                        continue;
                    }
                    if (code[i + 1] == '*')
                    {
                        // Block comment
                        i += 2;
                        while (i + 1 < code.Length)
                        {
                            if (code[i] == '*' && code[i + 1] == '/') { i++; break; }
                            i++;
                        }
                        continue;
                    }
                    // Potential regex — check if preceded by operator
                    if (IsRegexContext(code, i))
                    {
                        i = SkipRegexLiteral(code, i);
                    }
                    break;
                case '"' or '\'' or '`':
                    char q = code[i];
                    i++;
                    while (i < code.Length)
                    {
                        if (code[i] == '\\' && i + 1 < code.Length) { i += 2; continue; }
                        if (code[i] == q) break;
                        if (q == '`' && code[i] == '$' && i + 1 < code.Length && code[i + 1] == '{')
                        {
                            // Template literal — skip ${...} recursively
                            i += 2;
                            int d = 1;
                            while (i < code.Length && d > 0)
                            {
                                if (code[i] is '"' or '\'' or '`')
                                {
                                    char innerQ = code[i]; i++;
                                    while (i < code.Length)
                                    {
                                        if (code[i] == '\\' && i + 1 < code.Length) { i += 2; continue; }
                                        if (code[i] == innerQ) break;
                                        i++;
                                    }
                                }
                                else if (code[i] == '{') d++;
                                else if (code[i] == '}') d--;
                                if (d > 0) i++;
                            }
                            continue;
                        }
                        i++;
                    }
                    break;
            }
            if (braces < 0 || brackets < 0 || parens < 0) return false;
        }
        return braces == 0 && brackets == 0 && parens == 0;
    }

    /// <summary>
    /// Определяет, является ли `/` в позиции `pos` началом regex литерала.
    /// В JS `/` может быть:
    ///   1. Деление: a / b
    ///   2. Regex: /pattern/flags
    /// 
    /// Regex следует после: = ( [ { , ; ! &amp; | ^ ~ ? : return typeof void delete
    /// Деление следует после: идентификатор, число, ) ] ++ --
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRegexContext(ReadOnlySpan<char> code, int pos)
    {
        if (pos == 0) return true;

        // Ищем последний значимый символ перед /
        int j = pos - 1;
        while (j >= 0 && code[j] is ' ' or '\t') j--;
        if (j < 0) return true;

        char prev = code[j];

        // После этих символов всегда regex
        if (s_regexPrecedingChars.Contains(prev))
            return true;

        // После ) или ] — деление (result of expression)
        if (prev is ')' or ']')
            return false;

        // После идентификатора — обычно деление, КРОМЕ ключевых слов
        if (char.IsLetterOrDigit(prev) || prev is '_' or '$')
        {
            // Проверяем: это ключевое слово "return", "typeof", "void", "delete", "case",
            // "throw", "in", "instanceof", "new"?
            int wordEnd = j + 1;
            int wordStart = j;
            while (wordStart > 0 && (char.IsLetterOrDigit(code[wordStart - 1]) || code[wordStart - 1] is '_' or '$'))
                wordStart--;

            var word = code[wordStart..wordEnd];
            if (word.SequenceEqual("return") || word.SequenceEqual("typeof") ||
                word.SequenceEqual("void") || word.SequenceEqual("delete") ||
                word.SequenceEqual("case") || word.SequenceEqual("throw") ||
                word.SequenceEqual("in") || word.SequenceEqual("instanceof") ||
                word.SequenceEqual("new") || word.SequenceEqual("else"))
            {
                return true;
            }

            return false; // identifier / number — это деление
        }

        return false;
    }

    /// <summary>
    /// Пропускает regex литерал начиная с `/`.
    /// Возвращает позицию ПОСЛЕДНЕГО символа regex (включая flags).
    /// Обрабатывает escaped символы и character classes [...].
    /// </summary>
    private static int SkipRegexLiteral(ReadOnlySpan<char> code, int pos)
    {
        // pos points to opening /
        int i = pos + 1;
        while (i < code.Length)
        {
            char c = code[i];
            if (c == '\\' && i + 1 < code.Length) { i += 2; continue; }
            if (c == '/') break; // closing /
            if (c == '[')
            {
                // Character class — scan to ]
                i++;
                while (i < code.Length)
                {
                    if (code[i] == '\\' && i + 1 < code.Length) { i += 2; continue; }
                    if (code[i] == ']') break;
                    i++;
                }
            }
            if (c is '\n' or '\r') return pos; // Не regex — newline внутри
            i++;
        }

        // Skip flags (gimsuy)
        if (i < code.Length && code[i] == '/')
        {
            i++;
            while (i < code.Length && char.IsLetter(code[i])) i++;
            return i - 1;
        }

        return pos; // Не удалось разобрать как regex
    }

    public static string? FindObjectDefinition(string fullJs, string name)
    {
        var fullSpan = fullJs.AsSpan();
        var nameSpan = name.AsSpan();
        int searchFrom = 0;

        while (searchFrom < fullSpan.Length)
        {
            int idx = fullSpan[searchFrom..].IndexOf(nameSpan, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += searchFrom;
            searchFrom = idx + name.Length;

            if (idx > 0 && (char.IsLetterOrDigit(fullSpan[idx - 1]) ||
                            fullSpan[idx - 1] is '_' or '$' or '.'))
                continue;

            int afterName = idx + name.Length;
            if (afterName < fullSpan.Length &&
                (char.IsLetterOrDigit(fullSpan[afterName]) || fullSpan[afterName] is '_' or '$'))
                continue;

            int pos = SkipWhitespace(fullSpan, afterName);
            if (pos >= fullSpan.Length || fullSpan[pos] != '=') continue;
            pos++;

            if (pos < fullSpan.Length && fullSpan[pos] == '=') continue;

            pos = SkipWhitespace(fullSpan, pos);
            if (pos >= fullSpan.Length) continue;

            char openChar = fullSpan[pos];
            if (openChar == '{')
            {
                int end = FindMatchingBrace(fullJs, pos);
                if (end > pos) return fullJs[pos..(end + 1)];
            }
            else if (openChar == '[')
            {
                int end = FindMatchingBracket(fullJs, pos);
                if (end > pos) return fullJs[pos..(end + 1)];
            }
        }
        return null;
    }

    public static JsFunctionInfo? FindFunctionByName(string js, string name)
    {
        var jsSpan = js.AsSpan();

        // Strategy 1: name=function(
        var funcTarget = string.Concat(name, "=function(");
        int idx = jsSpan.IndexOf(funcTarget.AsSpan(), StringComparison.Ordinal);
        if (idx >= 0 && (idx == 0 || !IsIdentCharStrict(jsSpan[idx - 1])))
        {
            int parenStart = idx + funcTarget.Length - 1;
            int parenEnd = FindMatchingParen(js, parenStart);
            if (parenEnd > 0)
            {
                int bodyStart = parenEnd + 1;
                while (bodyStart < jsSpan.Length && jsSpan[bodyStart] is ' ' or '\t' or '\n' or '\r')
                    bodyStart++;

                if (bodyStart < jsSpan.Length && jsSpan[bodyStart] == '{')
                {
                    int braceEnd = FindMatchingBrace(js, bodyStart);
                    if (braceEnd > 0)
                    {
                        int funcStart = idx + name.Length + 1;
                        return new JsFunctionInfo(name, js[funcStart..(braceEnd + 1)], idx);
                    }
                }
            }
        }

        // Strategy 2: Arrow functions
        var eqTarget = string.Concat(name, "=");
        var eqTargetSpan = eqTarget.AsSpan();

        int searchFrom = 0;
        while (searchFrom < jsSpan.Length)
        {
            idx = jsSpan[searchFrom..].IndexOf(eqTargetSpan, StringComparison.Ordinal);
            if (idx < 0) break;
            idx += searchFrom;
            searchFrom = idx + eqTargetSpan.Length;

            if (idx > 0 && IsIdentCharStrict(jsSpan[idx - 1])) continue;

            int afterEq = idx + name.Length + 1;
            if (afterEq >= jsSpan.Length || jsSpan[afterEq] == '=') continue;

            int pos = SkipHorizontalWhitespace(jsSpan, afterEq);

            bool isArrow = false;
            if (pos < jsSpan.Length && jsSpan[pos] == '(')
            {
                int pe = FindMatchingParen(js, pos);
                if (pe > 0)
                {
                    int ap = SkipHorizontalWhitespace(jsSpan, pe + 1);
                    if (ap + 1 < jsSpan.Length && jsSpan[ap] == '=' && jsSpan[ap + 1] == '>')
                        isArrow = true;
                }
            }
            else if (pos < jsSpan.Length && (char.IsLetter(jsSpan[pos]) || jsSpan[pos] is '_' or '$'))
            {
                int pe = pos;
                while (pe < jsSpan.Length &&
                       (char.IsLetterOrDigit(jsSpan[pe]) || jsSpan[pe] is '_' or '$'))
                    pe++;
                int ap = SkipHorizontalWhitespace(jsSpan, pe);
                if (ap + 1 < jsSpan.Length && jsSpan[ap] == '=' && jsSpan[ap + 1] == '>')
                    isArrow = true;
            }

            if (!isArrow) continue;

            int funcStart2 = idx + name.Length + 1;
            // Arrow function body
            int arrowBodySearch = funcStart2;
            int braceOffset2 = jsSpan[arrowBodySearch..].IndexOf('{');
            if (braceOffset2 >= 0 && braceOffset2 <= 300)
            {
                int braceStart = arrowBodySearch + braceOffset2;
                int braceEnd = FindMatchingBrace(js, braceStart);
                if (braceEnd > 0)
                    return new JsFunctionInfo(name, js[funcStart2..(braceEnd + 1)], idx);
            }
        }

        return null;
    }

    /// <summary>
    /// Находит все идентификаторы, на которые ссылается код.
    /// 
    /// ВАЖНО: НЕ фильтрует параметры/локальные переменные —
    /// это ответственность вызывающего кода (ExtractBundle).
    /// Но фильтрует стандартные JS-имена (SkipNames) и
    /// однобуквенные строчные буквы (a-z), которые практически
    /// всегда являются параметрами/переменными, а не глобальными функциями.
    /// </summary>
    public static HashSet<string> FindReferencedNames(string code)
    {
        var result = new HashSet<string>(32);
        var span = code.AsSpan();
        int i = 0;

        while (i < span.Length)
        {
            char c = span[i];

            if (c is '"' or '\'' or '`')
            {
                i = SkipString(code, i);
                continue;
            }

            if (c == '/' && i + 1 < span.Length)
            {
                if (span[i + 1] == '/')
                {
                    int nlPos = span[i..].IndexOf('\n');
                    i = nlPos >= 0 ? i + nlPos : span.Length;
                    continue;
                }
                if (span[i + 1] == '*')
                {
                    i += 2;
                    int endComment = span[i..].IndexOf("*/");
                    i = endComment >= 0 ? i + endComment + 2 : span.Length;
                    continue;
                }
                // Regex
                if (IsRegexContext(span, i))
                {
                    int regEnd = SkipRegexLiteral(span, i);
                    if (regEnd > i) { i = regEnd + 1; continue; }
                }
            }

            if (s_identStartChars.Contains(c))
            {
                int start = i;
                i++;
                while (i < span.Length &&
                       (char.IsLetterOrDigit(span[i]) || span[i] is '_' or '$'))
                    i++;

                int len = i - start;
                if (len is >= 1 and <= 20 &&
                    (start == 0 || span[start - 1] != '.'))
                {
                    var ident = span.Slice(start, len);

                    // ═══ Пропускаем однобуквенные строчные — это почти всегда
                    // параметры/локальные переменные, FindAnyDefinition
                    // найдёт случайное определение из другой функции ═══
                    if (len == 1 && char.IsLower(ident[0]))
                        continue;

                    var identStr = ident.ToString();
                    if (!SkipNames.Contains(identStr))
                        result.Add(identStr);
                }
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                while (i < span.Length &&
                       (char.IsLetterOrDigit(span[i]) || span[i] is '_' or '$' or '.'))
                    i++;
                continue;
            }

            i++;
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '$' or '.';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentCharStrict(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '$';

    public static int FindMatchingBrace(string js, int pos) => FindMatching(js, pos, '{', '}');
    public static int FindMatchingBracket(string js, int pos) => FindMatching(js, pos, '[', ']');
    public static int FindMatchingParen(string js, int pos) => FindMatching(js, pos, '(', ')');

    public static int SkipString(string js, int i)
    {
        if (i >= js.Length) return i;
        char quote = js[i++];

        if (quote == '`')
        {
            while (i < js.Length)
            {
                char c = js[i];
                if (c == '\\' && i + 1 < js.Length) { i += 2; continue; }
                if (c == '`') return i + 1;
                if (c == '$' && i + 1 < js.Length && js[i + 1] == '{')
                {
                    i += 2;
                    int d = 1;
                    while (i < js.Length && d > 0)
                    {
                        if (js[i] is '"' or '\'' or '`') { i = SkipString(js, i); continue; }
                        if (js[i] == '{') d++;
                        else if (js[i] == '}') d--;
                        if (d > 0) i++;
                    }
                    if (i < js.Length) i++;
                    continue;
                }
                i++;
            }
            return i;
        }

        var span = js.AsSpan();
        while (i < span.Length)
        {
            int found = span[i..].IndexOfAny(quote, '\\');
            if (found < 0) return span.Length;
            i += found;

            if (span[i] == '\\') { i += 2; continue; }

            return i + 1;
        }

        return i;
    }

    public readonly record struct JsFunctionInfo(string Name, string Code, int Position);

    // ═══════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipHorizontalWhitespace(ReadOnlySpan<char> span, int pos)
    {
        while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipWhitespace(ReadOnlySpan<char> span, int pos)
    {
        while (pos < span.Length && char.IsWhiteSpace(span[pos])) pos++;
        return pos;
    }

    /// <summary>
    /// Проверяет, содержит ли код top-level try без соответствующего catch/finally.
    /// </summary>
    private static bool HasUnmatchedTopLevelTry(string code)
    {
        if (code.IndexOf("try", StringComparison.Ordinal) < 0)
            return false;

        var span = code.AsSpan();
        int i = 0;
        int braceDepth = 0;
        int unmatchedTry = 0;

        Span<int> scopeStack = stackalloc int[64];
        int scopeTop = -1;

        while (i < span.Length)
        {
            char c = span[i];

            if (c is '"' or '\'' or '`') { i = SkipString(code, i); continue; }

            if (c == '/' && i + 1 < span.Length)
            {
                if (span[i + 1] == '/')
                {
                    int nl = span[i..].IndexOf('\n');
                    i = nl >= 0 ? i + nl + 1 : span.Length;
                    continue;
                }
                if (span[i + 1] == '*')
                {
                    i += 2;
                    int end = span[i..].IndexOf("*/");
                    i = end >= 0 ? i + end + 2 : span.Length;
                    continue;
                }
                if (IsRegexContext(span, i))
                {
                    int regEnd = SkipRegexLiteral(span, i);
                    if (regEnd > i) { i = regEnd + 1; continue; }
                }
            }

            if (c == '{')
            {
                braceDepth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                braceDepth--;
                if (scopeTop >= 0 && braceDepth == scopeStack[scopeTop])
                {
                    scopeTop--;
                }
                i++;
                continue;
            }

            if (c == 'f' && MatchKeyword(span, i, "function"))
            {
                // Пропускаем до { через FindMatchingParen (для destructuring)
                int parenPos = -1;
                for (int pi = i + 8; pi < span.Length && pi < i + 200; pi++)
                {
                    if (span[pi] == '(') { parenPos = pi; break; }
                    if (span[pi] is not (' ' or '\t' or '*')) break;
                }
                if (parenPos >= 0)
                {
                    int parenEnd = FindMatchingParen(code, parenPos);
                    if (parenEnd > 0)
                    {
                        // Ищем { после )
                        int bodySearch = parenEnd + 1;
                        while (bodySearch < span.Length && span[bodySearch] is ' ' or '\t' or '\n' or '\r') bodySearch++;
                        if (bodySearch < span.Length && span[bodySearch] == '{')
                        {
                            if (scopeTop + 1 < scopeStack.Length)
                                scopeStack[++scopeTop] = braceDepth;
                            i = bodySearch;
                            continue;
                        }
                    }
                }
                i += 8;
                continue;
            }

            if (c == '=' && i + 1 < span.Length && span[i + 1] == '>')
            {
                int afterArrow = SkipHorizontalWhitespace(span, i + 2);
                if (afterArrow < span.Length && span[afterArrow] == '{')
                {
                    if (scopeTop + 1 < scopeStack.Length)
                        scopeStack[++scopeTop] = braceDepth;
                    i = afterArrow;
                    continue;
                }
                i += 2;
                continue;
            }

            if (scopeTop >= 0) { i++; continue; }

            if (c == 't' && MatchKeyword(span, i, "try"))
            {
                unmatchedTry++;
                i += 3;
                continue;
            }

            if (c == 'c' && MatchKeyword(span, i, "catch"))
            {
                if (unmatchedTry > 0) unmatchedTry--;
                i += 5;
                continue;
            }

            if (c == 'f' && MatchKeyword(span, i, "finally"))
            {
                if (unmatchedTry > 0) unmatchedTry--;
                i += 7;
                continue;
            }

            i++;
        }

        return unmatchedTry > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchKeyword(ReadOnlySpan<char> span, int i, string keyword)
    {
        int len = keyword.Length;
        if (i + len > span.Length) return false;
        if (!span.Slice(i, len).SequenceEqual(keyword)) return false;
        if (i > 0 && IsIdentCharStrict(span[i - 1])) return false;
        if (i + len < span.Length && (char.IsLetterOrDigit(span[i + len]) || span[i + len] is '_' or '$'))
            return false;
        return true;
    }

    private static HashSet<string> FindTypeofGuardVars(
        string code, Dictionary<string, string> definitions, string? dictName)
    {
        var result = new HashSet<string>();

        foreach (Match m in TypeofUndefinedRegex().Matches(code))
        {
            var varName = m.Groups[1].Value;
            if (!definitions.ContainsKey(varName) &&
                !SkipNames.Contains(varName) && varName != dictName)
                result.Add(varName);
        }

        foreach (Match m in TypeofArrayRegex().Matches(code))
        {
            var varName = m.Groups[1].Value;
            if (!definitions.ContainsKey(varName) &&
                !SkipNames.Contains(varName) && varName != dictName)
                result.Add(varName);
        }

        return result;
    }

    private static string? FindBracketArrayDefinition(string fullJs, string name)
    {
        var fullSpan = fullJs.AsSpan();
        var target = string.Concat(name, "=[");
        var targetSpan = target.AsSpan();

        int searchFrom = 0;
        while (searchFrom < fullSpan.Length)
        {
            int idx = fullSpan[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (idx < 0) break;
            idx += searchFrom;
            searchFrom = idx + targetSpan.Length;

            if (idx > 0 && IsIdentCharStrict(fullSpan[idx - 1])) continue;

            int bracketPos = idx + name.Length + 1;
            int bracketEnd = FindMatchingBracket(fullJs, bracketPos);
            if (bracketEnd < 0) continue;

            int elementCount = CountTopLevelCommas(fullJs, bracketPos + 1, bracketEnd) + 1;

            int minElements = name.Length <= 2 ? 20 : 50;
            if (elementCount < minElements) continue;

            int sampleEnd = Math.Min(bracketPos + 500, bracketEnd);
            var sampleSpan = fullSpan[bracketPos..sampleEnd];
            if (!sampleSpan.ContainsAny(s_quoteChars)) continue;

            int end = bracketEnd + 1;
            if (end < fullSpan.Length && fullSpan[end] == ';') end++;

            var valuePart = fullSpan[bracketPos..end];

            Log.Debug($"[JsExtractor] Found bracket array '{name}' with {elementCount} elements");
            return string.Concat("var ".AsSpan(), name.AsSpan(), "=".AsSpan(), valuePart);
        }
        return null;
    }

    private static string? FindSplitArrayDefinition(string fullJs, string name)
    {
        var fullSpan = fullJs.AsSpan();

        var targetSingle = string.Concat(name, "='");
        var targetDouble = string.Concat(name, "=\"");
        ReadOnlySpan<string> targets = [targetSingle, targetDouble];

        foreach (var target in targets)
        {
            var targetSpan = target.AsSpan();

            int searchFrom = 0;
            while (searchFrom < fullSpan.Length)
            {
                int idx = fullSpan[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
                if (idx < 0) break;
                idx += searchFrom;
                searchFrom = idx + targetSpan.Length;

                if (idx > 0 && IsIdentCharStrict(fullSpan[idx - 1])) continue;

                int eqPos = idx + name.Length;
                if (eqPos + 1 < fullSpan.Length &&
                    fullSpan[eqPos] == '=' && fullSpan[eqPos + 1] == '=')
                    continue;

                int quoteStart = idx + name.Length + 1;
                int quoteEnd = SkipString(fullJs, quoteStart);
                if (quoteEnd <= quoteStart) continue;

                int afterString = SkipHorizontalWhitespace(fullSpan, quoteEnd);

                if (afterString + 7 > fullSpan.Length) continue;
                if (!fullSpan.Slice(afterString, 7).SequenceEqual(".split(")) continue;

                int splitParenStart = afterString + 6;
                int splitParenEnd = FindMatchingParen(fullJs, splitParenStart);
                if (splitParenEnd < 0) continue;

                int end = splitParenEnd + 1;
                if (end < fullSpan.Length && fullSpan[end] is ';' or ',') end++;

                int stringLen = quoteEnd - quoteStart - 2;
                if (stringLen < 100) continue;

                var stringContent = fullSpan.Slice(quoteStart + 1, quoteEnd - quoteStart - 2);

                int sepAreaStart = splitParenStart + 1;
                int sepPos = SkipHorizontalWhitespace(fullSpan, sepAreaStart);
                if (sepPos >= fullSpan.Length || fullSpan[sepPos] is not ('"' or '\'')) continue;

                char sepQuote = fullSpan[sepPos];
                int sepContentStart = sepPos + 1;
                int sepContentEnd = -1;
                for (int si = sepContentStart; si < fullSpan.Length; si++)
                {
                    if (fullSpan[si] == '\\' && si + 1 < fullSpan.Length) { si++; continue; }
                    if (fullSpan[si] == sepQuote) { sepContentEnd = si; break; }
                }
                if (sepContentEnd < 0 || sepContentEnd == sepContentStart ||
                    sepContentEnd - sepContentStart > 10) continue;

                var separator = fullSpan[sepContentStart..sepContentEnd];

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

                var definitionSpan = fullSpan[(idx + name.Length + 1)..end];

                if (definitionSpan.Length > 0 && definitionSpan[^1] == ',')
                {
                    var trimmed = definitionSpan[..^1];
                    return ConcatSpans(
                        "var ".AsSpan(), name.AsSpan(), "=".AsSpan(), trimmed, ";".AsSpan());
                }

                return string.Concat(
                    "var ".AsSpan(), name.AsSpan(), "=".AsSpan(), definitionSpan);
            }
        }
        return null;
    }

    private static int CountTopLevelCommas(string js, int from, int to)
    {
        int count = 0, depth = 0, i = from;
        while (i < to)
        {
            char c = js[i];
            if (c is '"' or '\'' or '`') { i = SkipString(js, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) count++;
            i++;
        }
        return count;
    }

    private static bool IsArrowFunctionStart(string js, int valueStart)
    {
        var span = js.AsSpan();
        if (valueStart >= span.Length) return false;

        if (span[valueStart] == '(')
        {
            int parenEnd = FindMatchingParen(js, valueStart);
            if (parenEnd < 0) return false;

            int after = SkipHorizontalWhitespace(span, parenEnd + 1);
            return after + 1 < span.Length && span[after] == '=' && span[after + 1] == '>';
        }

        if (char.IsLetter(span[valueStart]) || span[valueStart] is '_' or '$')
        {
            int paramEnd = valueStart;
            while (paramEnd < span.Length &&
                   (char.IsLetterOrDigit(span[paramEnd]) || span[paramEnd] is '_' or '$'))
                paramEnd++;

            paramEnd = SkipHorizontalWhitespace(span, paramEnd);
            return paramEnd + 1 < span.Length && span[paramEnd] == '=' && span[paramEnd + 1] == '>';
        }

        return false;
    }

    private static bool IsStatementBoundary(ReadOnlySpan<char> fullJs, int pos)
    {
        int i = pos - 1;
        while (i >= 0 && fullJs[i] is ' ' or '\t') i--;

        if (i < 0) return true;

        char prev = fullJs[i];

        if (prev is ';' or '{' or '}' or '\n' or '\r' or ',')
            return true;

        if (i >= 3 && fullJs.Slice(i - 3, 4).SequenceEqual("var ")) return true;
        if (i >= 3 && fullJs.Slice(i - 3, 4).SequenceEqual("let ")) return true;
        if (i >= 5 && fullJs.Slice(i - 5, 6).SequenceEqual("const ")) return true;

        return false;
    }

    private static bool IsValidValue(ReadOnlySpan<char> value)
    {
        var v = value.TrimEnd([';', ',', ' ', '\n', '\r']);
        if (v.Length == 0) return false;

        if (v.Length > 5 && v.IndexOf("'+") >= 0 && v.IndexOf("+'") >= 0) return false;
        if (v.StartsWith("function")) return false;
        if (v.StartsWith("new ")) return true;

        if (v[0] is '-' or '.' || char.IsAsciiDigit(v[0]))
            return v.Length < 30 && v.IndexOf(' ') < 0;
        if ((v[0] == '{' && v[^1] == '}') || (v[0] == '[' && v[^1] == ']'))
            return true;
        if (v[0] is '"' or '\'')
            return v.IndexOf('+') < 0 && v.Length < 200;

        if (v.Length < 100)
        {
            if (v.IndexOf("'+") >= 0 || v.IndexOf("+'") >= 0 || v.IndexOf("=\"") >= 0)
                return false;
            return true;
        }

        return false;
    }

    private static int SkipValue(string js, int i)
    {
        var span = js.AsSpan();
        if (i >= span.Length) return i;

        char c = span[i];

        if (c == 'f' && i + 8 <= span.Length && span.Slice(i, 8).SequenceEqual("function"))
        {
            // Правильно пропускаем function: через параметры → тело
            int parenStart = -1;
            for (int pi = i + 8; pi < span.Length && pi < i + 200; pi++)
            {
                if (span[pi] == '(') { parenStart = pi; break; }
                if (span[pi] is not (' ' or '\t' or '*') && !IsIdentCharStrict(span[pi])) break;
            }
            if (parenStart < 0) return i;

            int parenEnd = FindMatchingParen(js, parenStart);
            if (parenEnd < 0) return i;

            int bodyStart = parenEnd + 1;
            while (bodyStart < span.Length && span[bodyStart] is ' ' or '\t' or '\n' or '\r') bodyStart++;
            if (bodyStart >= span.Length || span[bodyStart] != '{') return i;

            int braceEnd = FindMatchingBrace(js, bodyStart);
            return braceEnd >= 0 ? braceEnd + 1 : i;
        }

        if (c == '{') { int end = FindMatchingBrace(js, i); return end >= 0 ? end + 1 : i; }
        if (c == '[') { int end = FindMatchingBracket(js, i); return end >= 0 ? end + 1 : i; }
        if (c == '(') { int end = FindMatchingParen(js, i); return end >= 0 ? end + 1 : i; }
        if (c is '"' or '\'' or '`') return SkipString(js, i);

        if (c == 'n' && i + 4 <= span.Length && span.Slice(i, 4).SequenceEqual("new "))
        {
            i += 4;
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            i = SkipValue(js, i);
            while (i < span.Length && span[i] == '(')
            {
                int end = FindMatchingParen(js, i);
                if (end < 0) break;
                i = end + 1;
            }
            return i;
        }

        int depth = 0;
        while (i < span.Length)
        {
            char ch = span[i];
            if (ch is '"' or '\'' or '`') { i = SkipString(js, i); continue; }
            if (ch is '(' or '[' or '{') depth++;
            if (ch is ')' or ']' or '}') { if (depth == 0) return i; depth--; }
            if (depth == 0 && ch is ';' or '\n' or ',') return i;
            i++;
        }

        return i;
    }

    /// <summary>
    /// Находит парный символ (скобку) с учётом строк, комментариев и regex литералов.
    /// 
    /// КЛЮЧЕВОЕ УЛУЧШЕНИЕ: Обработка regex литералов.
    /// В минифицированном JS часто встречается `};/regex/` — без обработки regex
    /// парсер принимает `/` за оператор деления и теряет синхронизацию,
    /// что приводит к обрезанию больших функций (например fD ~160KB).
    /// </summary>
    private static int FindMatching(string js, int openPos, char open, char close)
    {
        int depth = 1;
        int i = openPos + 1;
        var span = js.AsSpan();

        while (i < span.Length && depth > 0)
        {
            char c = span[i];

            if (c == open) { depth++; i++; continue; }
            if (c == close) { depth--; if (depth == 0) return i; i++; continue; }

            switch (c)
            {
                case '"' or '\'' or '`':
                    i = SkipString(js, i);
                    continue;
                case '/' when i + 1 < span.Length:
                    if (span[i + 1] == '/')
                    {
                        int nlPos = span[i..].IndexOf('\n');
                        i = nlPos >= 0 ? i + nlPos : span.Length;
                        continue;
                    }
                    if (span[i + 1] == '*')
                    {
                        i += 2;
                        int endComment = span[i..].IndexOf("*/");
                        i = endComment >= 0 ? i + endComment + 2 : span.Length;
                        continue;
                    }
                    // ═══ REGEX HANDLING ═══
                    if (IsRegexContext(span, i))
                    {
                        int regEnd = SkipRegexLiteral(span, i);
                        if (regEnd > i) { i = regEnd + 1; continue; }
                    }
                    break;
            }
            i++;
        }

        return depth == 0 ? i - 1 : -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // SPAN-BASED PARSING HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static bool TryParseSplitExpression(
        ReadOnlySpan<char> def,
        out ReadOnlySpan<char> content,
        out ReadOnlySpan<char> separator)
    {
        content = default;
        separator = default;

        int quoteStart = def.IndexOfAny(s_quoteChars);
        if (quoteStart < 0) return false;

        char q = def[quoteStart];
        if (q == '`') return false;

        int contentStart = quoteStart + 1;
        int contentEnd = -1;
        int i = contentStart;
        while (i < def.Length)
        {
            if (def[i] == '\\' && i + 1 < def.Length) { i += 2; continue; }
            if (def[i] == q) { contentEnd = i; break; }
            i++;
        }
        if (contentEnd < 0) return false;

        int afterContent = contentEnd + 1;
        var rest = def[afterContent..];

        int ws = 0;
        while (ws < rest.Length && rest[ws] is ' ' or '\t') ws++;
        rest = rest[ws..];

        if (!rest.StartsWith(".split(")) return false;
        rest = rest[7..];

        ws = 0;
        while (ws < rest.Length && rest[ws] is ' ' or '\t') ws++;
        rest = rest[ws..];

        if (rest.Length == 0 || rest[0] is not ('"' or '\'')) return false;
        char sq = rest[0];
        int sepStart = 1;
        int sepEnd = -1;
        i = sepStart;
        while (i < rest.Length)
        {
            if (rest[i] == '\\' && i + 1 < rest.Length) { i += 2; continue; }
            if (rest[i] == sq) { sepEnd = i; break; }
            i++;
        }
        if (sepEnd < 0) return false;

        content = def[contentStart..contentEnd];
        separator = rest[sepStart..sepEnd];
        return true;
    }

    private static string[] SplitToArray(ReadOnlySpan<char> content, ReadOnlySpan<char> separator)
    {
        int count = 1;
        int pos = 0;
        while (pos <= content.Length - separator.Length)
        {
            int idx = content[pos..].IndexOf(separator, StringComparison.Ordinal);
            if (idx < 0) break;
            count++;
            pos += idx + separator.Length;
        }

        var result = new string[count];
        int resultIdx = 0;
        pos = 0;

        while (pos <= content.Length - separator.Length)
        {
            int idx = content[pos..].IndexOf(separator, StringComparison.Ordinal);
            if (idx < 0) break;
            result[resultIdx++] = content.Slice(pos, idx).ToString();
            pos += idx + separator.Length;
        }

        result[resultIdx] = content[pos..].ToString();
        return result;
    }

    private static string[] SplitBracketElements(ReadOnlySpan<char> inner)
    {
        var list = new List<string>(64);
        int i = 0;

        while (i < inner.Length)
        {
            while (i < inner.Length && inner[i] is ' ' or '\t' or '\n' or '\r') i++;
            if (i >= inner.Length) break;
            if (inner[i] == ',') { i++; continue; }

            if (inner[i] is '"' or '\'')
            {
                char q = inner[i];
                i++;
                int start = i;
                while (i < inner.Length)
                {
                    if (inner[i] == '\\' && i + 1 < inner.Length) { i += 2; continue; }
                    if (inner[i] == q) break;
                    i++;
                }
                int elemLen = i - start;
                list.Add(elemLen > 0 ? inner.Slice(start, elemLen).ToString() : "");
                if (i < inner.Length) i++;
            }
            else
            {
                int start = i;
                while (i < inner.Length && inner[i] is not (',' or ' ' or '\t' or '\n' or '\r'))
                    i++;
                var elem = inner[start..i].Trim(" \t\n\r");
                if (elem.Length > 0)
                    list.Add(elem.ToString());
            }
        }

        return [.. list];
    }

    private static void ParseCommaSeparatedIdents(ReadOnlySpan<char> span, HashSet<string> result)
    {
        int start = 0;
        while (start < span.Length)
        {
            while (start < span.Length && span[start] is ' ' or '\t' or '\n' or '\r') start++;
            if (start >= span.Length) break;

            int end = span[start..].IndexOf(',');
            ReadOnlySpan<char> param;
            if (end >= 0)
            {
                param = span.Slice(start, end).Trim();
                start = start + end + 1;
            }
            else
            {
                param = span[start..].Trim();
                start = span.Length;
            }

            if (param.Length > 0)
                result.Add(param.ToString());
        }
    }

    private static string ConcatSpans(
        ReadOnlySpan<char> a, ReadOnlySpan<char> b,
        ReadOnlySpan<char> c, ReadOnlySpan<char> d,
        ReadOnlySpan<char> e)
    {
        var sb = t_concatBuilder ??= new StringBuilder(256);
        sb.Clear();
        sb.Append(a).Append(b).Append(c).Append(d).Append(e);
        return sb.ToString();
    }

    private static string Truncate(string s, int len = 60) =>
        s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    // ═══════════════════════════════════════════════════════════════
    // GENERATED REGEX
    // ═══════════════════════════════════════════════════════════════

    [GeneratedRegex(@"typeof\s+([a-zA-Z_$][\w$]*)\s*===?\s*""undefined""")]
    private static partial Regex TypeofUndefinedRegex();

    [GeneratedRegex(@"typeof\s+([a-zA-Z_$][\w$]*)\s*===?\s*[a-zA-Z_$]{1,3}\[\d+\]")]
    private static partial Regex TypeofArrayRegex();
}