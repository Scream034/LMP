// Core/Youtube/Bridge/NToken/NTokenDecryptor.cs

using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// N-Token дешифратор YouTube.
/// <para>
/// Стратегии (в порядке приоритета):
/// 1. Wrapper Sieve Fuzzing — находит обёртки с зашитыми magic numbers
/// 2. Embedded magic из обёрток + алгебраический анализ ядра
/// 3. Прямой вызов ядра без magic
/// </para>
/// </summary>
public sealed partial class NTokenDecryptor(PlayerContextManager playerManager)
    : JsDecryptorBase<NTokenDecryptor>(playerManager, G.FilePath.NTokenCache, 2000, 500)
{
    private const string TestToken = "Siib9I-K-KF0GqS-";
    private const int MaxWrapperBodyLength = 500;
    private const int MaxWrapperParams = 3;
    private const int MaxFuzzCandidates = 30;
    private const int MaxNTokenLength = 40;

    /// <summary>Минимальный diffCount для принятия результата как настоящей дешифровки.</summary>
    private const int MinDiffCount = 5;

    /// <summary>
    /// Сообщения ошибок JS, указывающие что ядро ожидает массив.
    /// </summary>
    private static readonly string[] ArrayMethodMarkers =
        ["'pop'", "'push'", "'reverse'", "'splice'", "'shift'", "'unshift'"];

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════

    public async ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nToken)) return nToken;
        if (Cache.TryGet(nToken, out var cached)) return cached;

        await EnsureInitializedAsync(ct);

        var result = TryInvokeJs(nToken, "NToken");
        return result ?? nToken;
    }

    // ═══════════════════════════════════════════════════════════════
    // INITIALIZATION
    // ═══════════════════════════════════════════════════════════════

    protected override void InitializeCore(PlayerContext context)
    {
        Log.Info("[NToken] Initializing...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var coreName = FindCoreFunctionName(context.BaseJs);
        if (coreName is null)
        {
            Log.Error("[NToken] Core function not found in base.js " +
                      $"(version={context.Version}, size={context.BaseJs.Length / 1024}KB)");
            return;
        }
        Log.Debug($"[NToken] Core function: {coreName}");

        var wrappers = FindAllShortWrappers(context.BaseJs, coreName);
        var embeddedMagics = ExtractAllEmbeddedMagic(wrappers, coreName);

        // Стратегия 1: Wrapper Sieve Fuzzing
        bool success = TryWrapperSieveFuzzing(context, coreName, wrappers);

        // Стратегия 2: MagicNumbers (embedded + algebraic)
        if (!success)
        {
            var candidates = MagicNumbersExtractor.ExtractCandidates(context.BaseJs, coreName);

            for (int i = embeddedMagics.Count - 1; i >= 0; i--)
            {
                if (!candidates.Contains(embeddedMagics[i]))
                {
                    candidates.Insert(0, embeddedMagics[i]);
                    Log.Info($"[NToken] Prioritized embedded magic: {embeddedMagics[i]}");
                }
            }

            if (candidates.Count > 0)
            {
                Log.Info($"[NToken] Magic candidates: " +
                         $"{string.Join(" | ", candidates.Take(10))}");
                if (candidates.All(c => c.HasArgs))
                    candidates.Add(MagicNumbers.None);

                success = TryInitWithCandidates(
                    context, coreName, candidates,
                    BuildCoreWrapperScript, TestToken,
                    (baseJs, fn) => BuildDefaultBundle(baseJs, fn),
                    ValidateNTokenResult);  // ← C#-валидация
            }
        }

        // Стратегия 3: ядро напрямую без magic
        if (!success)
        {
            Log.Debug("[NToken] Trying core directly without magic...");
            success = TryInitJsEngines(
                context, coreName,
                fn => BuildCoreWrapperScript(fn, MagicNumbers.None),
                TestToken,
                (baseJs, fn) => BuildDefaultBundle(baseJs, fn),
                ValidateNTokenResult);  // ← C#-валидация
        }

        sw.Stop();
        if (success)
            Log.Info($"[NToken] Ready in {sw.ElapsedMilliseconds}ms (core={coreName})");
        else
            Log.Error($"[NToken] All strategies failed after {sw.ElapsedMilliseconds}ms " +
                      $"(core={coreName}, version={context.Version})");
    }

    /// <summary>
    /// C#-валидатор результата для <see cref="TryInitEngine"/>.
    /// Повторяет логику <see cref="IsValidNToken"/> — проверяет длину,
    /// символьный состав и substitution distance.
    /// <para>
    /// Нужен потому что JS wrapper проверяет только <c>r !== n</c>,
    /// и пропускает ложные результаты: URL-charset строки (64 символа),
    /// splice(0,1) мутации и пр.
    /// </para>
    /// </summary>
    private static bool ValidateNTokenResult(string? result, string input)
    {
        if (!IsValidNToken(result, input))
        {
            Log.Debug($"[NToken] C# validator rejected: '{Truncate(result ?? "null", 40)}' " +
                      $"for input '{Truncate(input, 20)}'");
            return false;
        }
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // EMBEDDED MAGIC EXTRACTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Извлекает зашитые magic numbers из тел всех обёрток.
    /// <para>
    /// Паттерн: <c>coreName[dict[N]](this, MAGIC_R, MAGIC_P, param1, param2)</c>
    /// Пример: <c>return Tx[G[19]](this, 48, 8079, l, y)</c> → MagicNumbers(48, 8079)
    /// </para>
    /// </summary>
    private static List<MagicNumbers> ExtractAllEmbeddedMagic(
        List<WrapperCandidate> wrappers, string coreName)
    {
        var results = new List<MagicNumbers>(4);
        var seen = new HashSet<string>();

        foreach (var w in wrappers)
        {
            var magic = ExtractMagicFromWrapper(w.Body, coreName);
            if (magic is not null && seen.Add(magic.ToString()))
            {
                results.Add(magic);
                Log.Debug($"[NToken] Extracted embedded magic from {w.Name}: {magic}");
            }
        }

        return results;
    }

    /// <summary>
    /// Парсит magic numbers из одного тела обёртки.
    /// Ищет паттерн <c>(this, NUM1, NUM2, ...)</c> после вызова ядра.
    /// </summary>
    private static MagicNumbers? ExtractMagicFromWrapper(string wrapperBody, string coreName)
    {
        var span = wrapperBody.AsSpan();
        var coreSpan = coreName.AsSpan();

        int coreIdx = span.IndexOf(coreSpan, StringComparison.Ordinal);
        if (coreIdx < 0) return null;

        // Ищем "(this," после имени ядра
        int searchStart = coreIdx + coreSpan.Length;
        int thisCommaPos = FindThisComma(span, searchStart);
        if (thisCommaPos < 0) return null;

        // Парсим числовые аргументы после "this,"
        int pos = thisCommaPos;
        var nums = new List<int>(4);

        while (pos < span.Length)
        {
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length) break;

            // Пробуем прочитать число (возможно отрицательное)
            int numStart = pos;
            bool neg = false;
            if (span[pos] == '-') { neg = true; pos++; }

            if (pos >= span.Length || !char.IsDigit(span[pos]))
                break; // Не число — дошли до параметра (l, y, ...)

            while (pos < span.Length && char.IsDigit(span[pos])) pos++;

            if (!int.TryParse(span[numStart..pos], out int num))
                break;

            nums.Add(num);

            // Ожидаем запятую или закрывающую скобку
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] is not (',' or ')'))
                break;
            if (span[pos] == ')') break;
            pos++; // skip ','
        }

        return nums.Count >= 2 ? new MagicNumbers([.. nums]) : null;
    }

    /// <summary>
    /// Ищет позицию после "this," в вызове (this, ...).
    /// Возвращает позицию первого символа после запятой, или -1.
    /// </summary>
    private static int FindThisComma(ReadOnlySpan<char> span, int from)
    {
        for (int i = from; i < span.Length - 5; i++)
        {
            if (span[i] != '(') continue;

            var afterParen = span[(i + 1)..];
            int ws = 0;
            while (ws < afterParen.Length && afterParen[ws] is ' ' or '\t') ws++;

            if (ws + 4 <= afterParen.Length && afterParen.Slice(ws, 4).SequenceEqual("this"))
            {
                int afterThis = ws + 4;
                while (afterThis < afterParen.Length && afterParen[afterThis] is ' ' or '\t')
                    afterThis++;

                if (afterThis < afterParen.Length && afterParen[afterThis] == ',')
                    return i + 1 + afterThis + 1;
            }
        }
        return -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // WRAPPER SIEVE FUZZING
    // ═══════════════════════════════════════════════════════════════

    private bool TryWrapperSieveFuzzing(
        PlayerContext context, string coreName, List<WrapperCandidate> wrappers)
    {
        if (wrappers.Count == 0)
        {
            Log.Debug("[NToken] Sieve: no wrapper candidates found");
            return false;
        }
        Log.Info($"[NToken] Sieve: found {wrappers.Count} wrapper candidate(s)");
        return FuzzWrappersInFullJs(context, wrappers);
    }

    /// <summary>Результат fuzzing-вызова обёртки.</summary>
    private readonly record struct FuzzResult(
        string DecryptedToken,
        int UsedArgCount,
        JsValue? SecondArg,
        bool UsesArrayInput);

    /// <summary>
    /// Двухпроходный fuzzing обёрток в полном JS.
    /// Pass 1 — строковый вход. Pass 2 — массивный вход (fallback).
    /// </summary>
    private bool FuzzWrappersInFullJs(PlayerContext context, List<WrapperCandidate> wrappers)
    {
        // Инжектируем экспорты (в порядке убывания позиции)
        var sortedByPos = wrappers.OrderByDescending(w => w.Position).ToList();
        var modifiedJs = context.BaseJs;
        int exportedCount = 0;

        foreach (var w in sortedByPos)
        {
            int insertPos = FindEndOfDefinition(modifiedJs, w.Position);
            if (insertPos < 0)
            {
                Log.Debug($"[NToken] Sieve: cannot find end-of-def for {w.Name}");
                continue;
            }
            modifiedJs = modifiedJs.Insert(insertPos,
                $"\ntry{{window['{w.Name}']={w.Name};}}catch(_eX){{}}\n");
            exportedCount++;
        }

        if (exportedCount == 0)
        {
            Log.Debug("[NToken] Sieve: no exports injected");
            return false;
        }

        Engine? engine = null;
        try
        {
            Log.Debug($"[NToken] Sieve: loading full JS ({context.BaseJs.Length / 1024}KB) " +
                      $"with {exportedCount} in-place exports...");

            engine = CreateSieveEngine();
            engine.Execute(BrowserStubs);
            engine.Execute(modifiedJs);

            var arrayFallbackCandidates = new List<WrapperCandidate>(4);

            // ═══ Pass 1: строковый вход ═══
            foreach (var wrapper in wrappers)
            {
                if (!IsCallableInEngine(engine, wrapper.Name))
                    continue;

                bool sawArrayError = false;
                var result = FuzzSingleWrapper(
                    engine, wrapper, TestToken,
                    useArrayInput: false,
                    ref sawArrayError);

                if (result is not null)
                    return AcceptSieveWinner(ref engine, wrapper, result.Value);

                if (sawArrayError)
                {
                    Log.Debug($"[NToken] Sieve: {wrapper.Name} needs array input, " +
                              $"deferring to Pass 2");
                    arrayFallbackCandidates.Add(wrapper);
                }
            }

            // ═══ Pass 2: массивный вход ═══
            foreach (var wrapper in arrayFallbackCandidates)
            {
                bool sawArrayError = false;
                var result = FuzzSingleWrapper(
                    engine, wrapper, TestToken,
                    useArrayInput: true,
                    ref sawArrayError);

                if (result is not null)
                    return AcceptSieveWinner(ref engine, wrapper, result.Value);
            }

            Log.Debug("[NToken] Sieve: no valid wrapper found in full JS");
        }
        catch (Exception ex)
        {
            Log.Warn($"[NToken] Sieve: full JS execution failed: " +
                     $"{Truncate(FormatJsException(ex), 100)}");
        }
        finally
        {
            engine?.Dispose();
        }
        return false;
    }

    /// <summary>Принимает победителя Sieve, настраивает engine и кэш.</summary>
    private bool AcceptSieveWinner(ref Engine? engine, WrapperCandidate wrapper, FuzzResult r)
    {
        Log.Info($"[NToken] Sieve winner: {wrapper.Name} " +
                 $"→ '{r.DecryptedToken}' ({wrapper.ParamCount}p, " +
                 $"args={r.UsedArgCount}, " +
                 $"2nd={r.SecondArg?.ToString() ?? "none"}" +
                 $"{(r.UsesArrayInput ? ", array-input" : "")})");

        FullEngine = engine;
        Cache.Set(TestToken, r.DecryptedToken);
        engine!.Execute(BuildSieveRuntimeWrapper(wrapper.Name, wrapper.ParamCount, r));
        FullFuncName = "__decryptorTransform";
        engine = null; // Предотвращаем Dispose в finally
        return true;
    }

    private static bool IsCallableInEngine(Engine engine, string name)
    {
        try
        {
            var typeStr = engine.Evaluate($"typeof window['{name}']").ToString();
            if (typeStr == "function") return true;

            Log.Debug($"[NToken] Sieve: {name} is '{typeStr}', skipping");
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug($"[NToken] Sieve: {name} typeof check failed: " +
                      $"{Truncate(FormatJsException(ex), 60)}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // FUZZING: АРГУМЕНТЫ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Перебирает комбинации аргументов для одной обёртки.</summary>
    private static FuzzResult? FuzzSingleWrapper(
        Engine engine,
        WrapperCandidate wrapper,
        string testToken,
        bool useArrayInput,
        ref bool sawArrayMethodError)
    {
        string? lastErrorKey = null;
        int repeatCount = 0;
        const int maxRepeat = 3;

        // ═══ 1 аргумент ═══
        {
            var tokenArg = MakeTokenArg(engine, testToken, useArrayInput);
            bool sawErr = false;
            var r = TryCallAndValidate(
                engine, wrapper.Name, [tokenArg], testToken,
                useArrayInput ? tokenArg : null,
                ref sawErr, ref lastErrorKey, ref repeatCount);
            sawArrayMethodError |= sawErr;
            if (r is not null) return new FuzzResult(r, 1, null, useArrayInput);
            if (repeatCount >= maxRepeat) return null;
        }

        // ═══ 2+ params ═══
        if (wrapper.ParamCount >= 2)
        {
            int len = testToken.Length;
            ReadOnlySpan<double> secondValues =
            [
                double.NaN,
                0, 1, 2, 3, 4, 5, 7, 8, 10, 16,
                len, len - 1, len - 2, len / 2, len + 1, len * 2,
                -1, -2, -len,
                64, 128, 255, 256,
            ];

            foreach (var val in secondValues)
            {
                var tokenArg = MakeTokenArg(engine, testToken, useArrayInput);
                var second = double.IsNaN(val) ? JsValue.Undefined : new JsNumber(val);
                var args = BuildArgsArray(wrapper.ParamCount, tokenArg, second);
                bool sawErr = false;
                var r = TryCallAndValidate(
                    engine, wrapper.Name, args, testToken,
                    useArrayInput ? tokenArg : null,
                    ref sawErr, ref lastErrorKey, ref repeatCount);
                sawArrayMethodError |= sawErr;
                if (r is not null)
                    return new FuzzResult(r, wrapper.ParamCount, second, useArrayInput);
                if (repeatCount >= maxRepeat) return null;
            }
        }

        if (wrapper.ParamCount == 0)
        {
            bool sawErr = false;
            var r = TryCallAndValidate(
                engine, wrapper.Name, [], testToken,
                null, ref sawErr, ref lastErrorKey, ref repeatCount);
            sawArrayMethodError |= sawErr;
            if (r is not null) return new FuzzResult(r, 0, null, useArrayInput);
        }

        return null;
    }

    private static JsValue MakeTokenArg(Engine engine, string token, bool asArray)
    {
        if (!asArray) return token;
        return CreateJsArrayFromString(engine, token);
    }

    private static JsValue[] BuildArgsArray(int count, JsValue firstArg, JsValue secondArg)
    {
        var args = new JsValue[count];
        args[0] = firstArg;
        if (count > 1) args[1] = secondArg;
        for (int i = 2; i < count; i++)
            args[i] = JsValue.Undefined;
        return args;
    }

    // ═══════════════════════════════════════════════════════════════
    // CALL & VALIDATE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Вызывает JS-функцию и валидирует результат.
    /// Три стратегии: прямой строковый возврат, массив → join, мутированный массив.
    /// </summary>
    private static string? TryCallAndValidate(
        Engine engine,
        string funcName,
        JsValue[] args,
        string testToken,
        JsValue? mutableArrayArg,
        ref bool sawArrayMethodError,
        ref string? lastErrorKey,
        ref int repeatCount)
    {
        try
        {
            var jsResult = engine.Invoke(funcName, args);

            // Сбрасываем счётчик при успешном вызове
            lastErrorKey = null;
            repeatCount = 0;

            if (!jsResult.IsUndefined() && !jsResult.IsNull())
            {
                if (jsResult.IsArray())
                {
                    var joined = JoinJsArray(engine, jsResult);
                    if (IsValidNToken(joined, testToken))
                        return joined;
                }
                else if (!jsResult.IsObject())
                {
                    var str = jsResult.ToString();
                    if (IsValidNToken(str, testToken))
                        return str;
                    if (str is not null && str != testToken)
                        Log.Debug($"[NToken] Sieve: {funcName} → " +
                                  $"'{Truncate(str, 30)}' (invalid)");
                }
            }

            if (mutableArrayArg is not null && mutableArrayArg.IsArray())
            {
                var mutated = JoinJsArray(engine, mutableArrayArg);
                if (IsValidNToken(mutated, testToken))
                {
                    Log.Debug($"[NToken] Sieve: {funcName} → " +
                              $"'{Truncate(mutated!, 30)}' (mutated array)");
                    return mutated;
                }
            }
        }
        catch (Exception ex)
        {
            if (IsArrayMethodError(ex.Message))
                sawArrayMethodError = true;

            var detailedMsg = FormatJsException(ex);
            var errorKey = detailedMsg.Length > 60 ? detailedMsg[..60] : detailedMsg;
            if (errorKey == lastErrorKey)
                repeatCount++;
            else
            {
                lastErrorKey = errorKey;
                repeatCount = 1;
            }

            Log.Debug($"[NToken] Sieve: {funcName} threw: " +
                      $"{Truncate(detailedMsg, 120)}");
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // VALIDATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Проверяет, является ли результат валидным N-токеном.
    /// <para>
    /// Защита от фальшивых обёрток: если символьный состав изменился
    /// менее чем на <see cref="MinDiffCount"/> символов — это просто
    /// reverse/pop/shift, а не настоящая дешифровка.
    /// </para>
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsValidNToken(string? result, string input)
    {
        if (string.IsNullOrEmpty(result)) return false;
        if (result == input) return false;
        if (result.Length < 5 || result.Length > MaxNTokenLength) return false;
        if (result is "undefined" or "null" or "NaN" or "true" or "false") return false;

        foreach (char c in result)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        if (input.Contains(result, StringComparison.Ordinal) ||
            result.Contains(input, StringComparison.Ordinal))
            return false;

        // Substitution check: настоящая дешифровка кардинально меняет символы
        int[] freq = new int[128];
        foreach (char c in input) if (c < 128) freq[c]++;
        foreach (char c in result) if (c < 128) freq[c]--;

        int diffCount = 0;
        foreach (int f in freq) diffCount += Math.Abs(f);

        if (diffCount < MinDiffCount)
        {
            Log.Debug($"[NToken] Rejected trivial mutation: diffCount={diffCount}, " +
                      $"input='{Truncate(input, 20)}', result='{Truncate(result, 20)}'");
            return false;
        }

        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsArrayMethodError(string errorMessage)
    {
        foreach (var marker in ArrayMethodMarkers)
        {
            if (errorMessage.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // JS ARRAY HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static JsValue CreateJsArrayFromString(Engine engine, string token)
    {
        engine.SetValue("__tmpToken", token);
        var arr = engine.Evaluate("__tmpToken.split('')");
        engine.SetValue("__tmpToken", JsValue.Undefined);
        return arr;
    }

    private static string? JoinJsArray(Engine engine, JsValue array)
    {
        try
        {
            engine.SetValue("__tmpArr", array);
            var result = engine.Evaluate("__tmpArr.join('')");
            engine.SetValue("__tmpArr", JsValue.Undefined);

            if (result.IsString()) return result.AsString();
            if (!result.IsUndefined() && !result.IsNull()) return result.ToString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WRAPPER DISCOVERY
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct WrapperCandidate(
        string Name, string Body, int ParamCount, int Position);

    private static List<WrapperCandidate> FindAllShortWrappers(string baseJs, string coreName)
    {
        var candidates = new List<WrapperCandidate>(16);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var span = baseJs.AsSpan();
        var coreSpan = coreName.AsSpan();

        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int corePos = span[searchFrom..].IndexOf(coreSpan, StringComparison.Ordinal);
            if (corePos < 0) break;
            corePos += searchFrom;
            searchFrom = corePos + coreSpan.Length;

            if (corePos > 0 && IsIdentChar(span[corePos - 1])) continue;
            int afterCore = corePos + coreName.Length;
            if (afterCore < span.Length
                && IsIdentChar(span[afterCore])
                && span[afterCore] is not ('(' or '.' or '['))
                continue;
            if (IsDefinitionSite(span, corePos, coreName.Length)) continue;

            var wrapper = TryExtractEnclosingWrapper(baseJs, corePos, coreName);
            if (wrapper is null) continue;
            if (!seenNames.Add(wrapper.Value.Name)) continue;
            if (wrapper.Value.Body.Length > MaxWrapperBodyLength) continue;
            if (wrapper.Value.ParamCount > MaxWrapperParams) continue;

            candidates.Add(wrapper.Value);
            Log.Debug($"[NToken] Sieve candidate: {wrapper.Value.Name}" +
                      $"({wrapper.Value.ParamCount}p) → " +
                      $"{Truncate(wrapper.Value.Body, 100)}");
            if (candidates.Count >= MaxFuzzCandidates) break;
        }

        // L2: обёртки над обёртками
        if (candidates.Count > 0 && candidates.Count < MaxFuzzCandidates)
        {
            var level2 = new List<WrapperCandidate>(8);
            foreach (var c in candidates)
            {
                foreach (var w in FindAllShortWrappersOfName(baseJs, c.Name, coreName))
                {
                    if (!seenNames.Add(w.Name)) continue;
                    if (w.ParamCount > 2) continue;
                    level2.Add(w);
                    Log.Debug($"[NToken] Sieve L2 candidate: {w.Name}" +
                              $"({w.ParamCount}p) → {Truncate(w.Body, 80)}");
                }
                if (candidates.Count + level2.Count >= MaxFuzzCandidates) break;
            }
            level2.AddRange(candidates);
            candidates = level2;
        }

        // Сортировка: 1-param первые, потом 2-param, потом 0-param
        candidates.Sort(static (a, b) =>
        {
            int ap = a.ParamCount == 0 ? 99 : a.ParamCount;
            int bp = b.ParamCount == 0 ? 99 : b.ParamCount;
            int cmp = ap.CompareTo(bp);
            return cmp != 0 ? cmp : a.Position.CompareTo(b.Position);
        });

        return candidates;
    }

    private static List<WrapperCandidate> FindAllShortWrappersOfName(
        string baseJs, string targetName, string coreName)
    {
        var results = new List<WrapperCandidate>(4);
        var span = baseJs.AsSpan();
        var targetSpan = targetName.AsSpan();
        int searchFrom = 0;

        while (searchFrom < span.Length)
        {
            int pos = span[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (pos < 0) break;
            pos += searchFrom;
            searchFrom = pos + targetSpan.Length;

            if (pos > 0 && IsIdentChar(span[pos - 1])) continue;
            int after = pos + targetName.Length;
            if (after < span.Length && IsIdentChar(span[after]) &&
                span[after] is not ('(' or '.' or '['))
                continue;
            if (IsDefinitionSite(span, pos, targetName.Length)) continue;

            var wrapper = TryExtractEnclosingWrapper(baseJs, pos, coreName);
            if (wrapper is null) continue;
            if (wrapper.Value.Name == targetName || wrapper.Value.Name == coreName) continue;
            if (wrapper.Value.Body.Length > 300 || wrapper.Value.ParamCount > 2) continue;

            results.Add(wrapper.Value);
            if (results.Count >= 8) break;
        }
        return results;
    }

    private static WrapperCandidate? TryExtractEnclosingWrapper(
        string baseJs, int callPos, string coreName)
    {
        var span = baseJs.AsSpan();
        int scanLimit = Math.Max(0, callPos - MaxWrapperBodyLength - 100);
        int funcKeywordPos = -1;

        for (int i = callPos - 1; i >= scanLimit; i--)
        {
            if (span[i] != 'f') continue;
            if (i + 8 > span.Length) continue;
            if (!span.Slice(i, 8).SequenceEqual("function")) continue;
            if (i > 0 && IsIdentChar(span[i - 1])) continue;
            funcKeywordPos = i;
            break;
        }
        if (funcKeywordPos < 0) return null;

        int parenStart = -1;
        for (int i = funcKeywordPos + 8; i < span.Length && i < funcKeywordPos + 50; i++)
        {
            if (span[i] == '(') { parenStart = i; break; }
            if (span[i] is not (' ' or '\t' or '*')) break;
        }
        if (parenStart < 0) return null;

        int parenEnd = JsFunctionExtractor.FindMatchingParen(baseJs, parenStart);
        if (parenEnd < 0) return null;

        int paramCount = CountParams(span[(parenStart + 1)..parenEnd]);

        int bodyBrace = -1;
        for (int i = parenEnd + 1; i < span.Length && i < parenEnd + 20; i++)
        {
            if (span[i] == '{') { bodyBrace = i; break; }
            if (span[i] is not (' ' or '\t' or '\n' or '\r')) break;
        }
        if (bodyBrace < 0) return null;

        int braceEnd = JsFunctionExtractor.FindMatchingBrace(baseJs, bodyBrace);
        if (braceEnd < 0) return null;
        if (braceEnd - funcKeywordPos + 1 > MaxWrapperBodyLength + 200) return null;

        var bodySpan = span[bodyBrace..(braceEnd + 1)];
        if (bodySpan.IndexOf("return", StringComparison.Ordinal) < 0) return null;

        string? funcName = ExtractFuncNameBefore(span, funcKeywordPos);
        if (funcName is null || funcName == coreName || funcName.Length > 20) return null;

        var body = span[funcKeywordPos..(braceEnd + 1)].ToString();
        return new WrapperCandidate(funcName, body, paramCount, funcKeywordPos);
    }

    private static string? ExtractFuncNameBefore(ReadOnlySpan<char> span, int funcPos)
    {
        int i = funcPos - 1;
        while (i >= 0 && span[i] is ' ' or '\t') i--;
        if (i < 0 || span[i] != '=') return null;
        i--;
        while (i >= 0 && span[i] is ' ' or '\t') i--;
        int nameEnd = i + 1;
        while (i >= 0 && IsIdentChar(span[i])) i--;
        int nameStart = i + 1;
        return nameStart < nameEnd ? span[nameStart..nameEnd].ToString() : null;
    }

    private static int FindEndOfDefinition(string js, int funcKeywordPos)
    {
        var span = js.AsSpan();
        int parenStart = -1;
        for (int i = funcKeywordPos; i < span.Length && i < funcKeywordPos + 50; i++)
            if (span[i] == '(') { parenStart = i; break; }
        if (parenStart < 0) return -1;

        int parenEnd = JsFunctionExtractor.FindMatchingParen(js, parenStart);
        if (parenEnd < 0) return -1;

        int bodyBrace = -1;
        for (int i = parenEnd + 1; i < span.Length && i < parenEnd + 20; i++)
        {
            if (span[i] == '{') { bodyBrace = i; break; }
            if (span[i] is not (' ' or '\t' or '\n' or '\r')) break;
        }
        if (bodyBrace < 0) return -1;

        int braceEnd = JsFunctionExtractor.FindMatchingBrace(js, bodyBrace);
        if (braceEnd < 0) return -1;

        int end = braceEnd + 1;
        if (end < span.Length && span[end] == ';') end++;
        return end;
    }

    // ═══════════════════════════════════════════════════════════════
    // SCRIPT BUILDERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Runtime wrapper для Sieve-победителя.
    /// Для <c>UsesArrayInput=true</c>: преобразует строку в массив,
    /// вызывает функцию, проверяет и возврат, и мутированный массив.
    /// </summary>
    private static string BuildSieveRuntimeWrapper(
        string funcName, int paramCount, FuzzResult fr)
    {
        var firstArgExpr = fr.UsesArrayInput ? "arr" : "n";
        string callArgs;

        if (fr.UsedArgCount <= 1 || fr.SecondArg is null || fr.SecondArg.IsUndefined())
        {
            callArgs = firstArgExpr;
        }
        else
        {
            var secondJs = MapSecondArgToJs(fr.SecondArg);
            callArgs = paramCount switch
            {
                2 => $"{firstArgExpr}, {secondJs}",
                3 => $"{firstArgExpr}, {secondJs}, undefined",
                _ => string.Join(", ", new[] { firstArgExpr, secondJs }
                    .Concat(Enumerable.Repeat("undefined",
                        Math.Max(0, paramCount - 2))))
            };
        }

        if (fr.UsesArrayInput)
        {
            return $$"""
            var __lastError = '';
            function __decryptorTransform(n) {
                __lastError = '';
                try {
                    var f = window['{{funcName}}'];
                    if (typeof f !== 'function') {
                        __lastError = 'not a function';
                        return n;
                    }
                    var arr = n.split('');
                    var r = f({{callArgs}});
                    if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                    if (Array.isArray(r)) { 
                        var s = r.join(''); 
                        if (s.length > 0 && s !== n) return s; 
                    }
                    var m = arr.join('');
                    if (m.length > 0 && m !== n) return m;
                    __lastError = 'no valid result (array mode)';
                    return n;
                } catch(e) { 
                    __lastError = 'array-wrapper: ' + (e.message || String(e)); 
                    return n; 
                }
            }
            """;
        }

        return $$"""
        var __lastError = '';
        function __decryptorTransform(n) {
            __lastError = '';
            try {
                var f = window['{{funcName}}'];
                if (typeof f !== 'function') {
                    __lastError = 'not a function';
                    return n;
                }
                var r = f({{callArgs}});
                if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                __lastError = 'no valid result (string mode)';
                return n;
            } catch(e) { 
                __lastError = 'string-wrapper: ' + (e.message || String(e)); 
                return n; 
            }
        }
        """;
    }

    private static string MapSecondArgToJs(JsValue secondArg)
    {
        if (!secondArg.IsNumber()) return "undefined";

        var num = (int)secondArg.AsNumber();
        int testLen = TestToken.Length;

        if (num == testLen) return "n.length";
        if (num == testLen - 1) return "n.length-1";
        if (num == testLen / 2) return "Math.floor(n.length/2)";
        if (num == testLen + 1) return "n.length+1";

        return num.ToString();
    }

    /// <summary>
    /// Wrapper для прямого вызова core-функции с magic numbers.
    /// <para>
    /// Поддерживает ядра с разным числом параметров (3 и 4).
    /// Пробует строковый и массивный вход, с разным количеством аргументов.
    /// Каждая попытка изолирована в свой try/catch для максимальной диагностики.
    /// </para>
    /// </summary>
    private static string BuildCoreWrapperScript(string funcName, MagicNumbers magic) => $$"""
        var __lastError = '';
        function __decryptorTransform(n) {
            __lastError = '';
            try {
                var f = window['{{funcName}}'];
                if (typeof f !== 'function') { __lastError = 'not a function'; return n; }
                
                var magicArgs = [{{magic.ToJsArgPrefix()}}];
                // Remove trailing empty element if no magic args
                if (magicArgs.length === 1 && magicArgs[0] === undefined) magicArgs = [];
                
                // ═══ Try string input with 1..2 trailing args ═══
                var strAttempts = [
                    function() { return f.apply(null, magicArgs.concat([n])); },
                    function() { return f.apply(null, magicArgs.concat([n, n])); },
                    function() { return f.apply(null, magicArgs.concat([n, undefined])); }
                ];
                
                for (var i = 0; i < strAttempts.length; i++) {
                    try {
                        var r = strAttempts[i]();
                        if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                        if (Array.isArray(r)) { 
                            var s = r.join(''); 
                            if (s.length > 0 && s !== n) return s; 
                        }
                    } catch(e1) { 
                        __lastError = 'str[' + i + ']: ' + (e1.message || String(e1)); 
                    }
                }
                
                // ═══ Try array input with 1..2 trailing args ═══
                var arrAttempts = [
                    function() { var a=n.split(''); return {ret:f.apply(null,magicArgs.concat([a])),     arr:a}; },
                    function() { var a=n.split(''); return {ret:f.apply(null,magicArgs.concat([a,a])),   arr:a}; },
                    function() { var a=n.split(''); return {ret:f.apply(null,magicArgs.concat([a,void 0])),arr:a}; }
                ];
                
                for (var j = 0; j < arrAttempts.length; j++) {
                    try {
                        var res = arrAttempts[j]();
                        var r2 = res.ret;
                        if (typeof r2 === 'string' && r2.length > 0 && r2 !== n) return r2;
                        if (Array.isArray(r2)) {
                            var s2 = r2.join('');
                            if (s2.length > 0 && s2 !== n) return s2;
                        }
                        var mutated = res.arr.join('');
                        if (mutated.length > 0 && mutated !== n) return mutated;
                    } catch(e2) {
                        __lastError += ' | arr[' + j + ']: ' + (e2.message || String(e2));
                    }
                }
                
                return n;
            } catch(e) { __lastError = 'outer: ' + (e.message || String(e)); return n; }
        }
        """;

    // ═══════════════════════════════════════════════════════════════
    // CORE DISCOVERY + HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static string? FindCoreFunctionName(string baseJs)
    {
        var match = TripleSelfReferenceRegex().Match(baseJs);
        if (!match.Success)
        {
            Log.Debug("[NToken] Triple self-reference pattern not found");
            return null;
        }
        var arrayName = match.Groups[1].Value;
        Log.Debug($"[NToken] Triple self-ref array '{arrayName}' at position {match.Index}");
        var containingFunc = FindContainingFunction(baseJs, match.Index);
        if (containingFunc is null)
            Log.Warn("[NToken] Cannot find function containing triple self-ref");
        else
            Log.Debug($"[NToken] Core function found: {containingFunc}");
        return containingFunc;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int CountParams(ReadOnlySpan<char> paramList)
    {
        paramList = paramList.Trim();
        if (paramList.IsEmpty) return 0;
        int count = 1;
        foreach (char c in paramList)
            if (c == ',') count++;
        return count;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '$';

    private static bool IsDefinitionSite(ReadOnlySpan<char> span, int namePos, int nameLen)
    {
        int i = namePos + nameLen;
        while (i < span.Length && span[i] is ' ' or '\t') i++;
        if (i >= span.Length || span[i] != '=') return false;
        return i + 1 >= span.Length || span[i + 1] != '=';
    }

    private static string? FindContainingFunction(string js, int position)
    {
        var searchStart = Math.Max(0, position - 10_000);
        string? lastName = null;
        foreach (Match m in FunctionDefinitionRegex().Matches(js, searchStart))
        {
            if (m.Index >= position) break;
            lastName = m.Groups[1].Value;
        }
        return lastName;
    }

    [GeneratedRegex(
        @"(\w+)\s*\[\s*[^\]]+\s*\]\s*=\s*\1\s*;\s*\1\s*\[\s*[^\]]+\s*\]\s*=\s*\1\s*;\s*\1\s*\[\s*[^\]]+\s*\]\s*=\s*\1",
        RegexOptions.None)]
    private static partial Regex TripleSelfReferenceRegex();

    [GeneratedRegex(
        @"(?:^|[;\n}])([a-zA-Z_$][\w$]*)\s*=\s*function\s*\(",
        RegexOptions.Compiled)]
    private static partial Regex FunctionDefinitionRegex();
}