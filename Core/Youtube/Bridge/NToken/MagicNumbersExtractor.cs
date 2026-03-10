using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// Извлекает магические числа для вызова N-token функции.
/// 
/// <para><b>Алгоритм (v3 — алгебраический):</b></para>
/// 
/// <para>
/// N-token функция (например Dp) — мульти-dispatch: разные ветки
/// активируются разными значениями <c>r</c>. N-token ветка определяется
/// условием <c>(r &amp; MASK) == r</c>, где MASK — константа (например 90).
/// </para>
/// 
/// <para>Внутри ветки код делает <c>I[O[b^K1]](O[b^K2])</c>,
/// где b=p^r, K1/K2 — литеральные XOR-константы, а O — глобальный словарь.
/// Мы ищем b такое, что O[b^K1]="split" и O[b^K2]="" (или наоборот),
/// затем подбираем r, который активирует ТОЛЬКО n-token ветку.</para>
/// 
/// <para>Этот подход не требует трассировки caller chain и работает
/// для любой версии плеера, пока структура мульти-dispatch сохраняется.</para>
/// </summary>
internal static partial class MagicNumbersExtractor
{
    /// <summary>Максимальное количество итоговых кандидатов для тестирования.</summary>
    private const int MaxCandidates = 10;

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Извлекает все возможные варианты магических чисел.
    /// Возвращает список кандидатов, отсортированный по приоритету.
    /// </summary>
    public static List<MagicNumbers> ExtractCandidates(string baseJs, string funcName)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseJs);
        ArgumentException.ThrowIfNullOrEmpty(funcName);

        var candidates = new List<MagicNumbers>();
        var seen = new HashSet<string>();

        // ═══ Strategy 1 (PRIMARY): Алгебраический анализ ветвления ═══
        var funcDef = JsFunctionExtractor.FindFunctionDefinition(baseJs, funcName);
        if (funcDef is not null)
        {
            foreach (var mn in ResolveViaAlgebraicAnalysis(baseJs, funcDef, funcName))
                AddCandidate(candidates, seen, mn, "algebraic");
        }

        // ═══ Strategy 2 (FALLBACK): Прямые числовые аргументы в вызовах ═══
        if (candidates.Count == 0)
        {
            var calls = FindAllCalls(baseJs, funcName);
            Log.Debug($"[MagicNumbers] Found {calls.Count} call(s) of '{funcName}'");

            foreach (var mn in ResolveDirectCalls(calls))
                AddCandidate(candidates, seen, mn, "direct");
        }

        if (candidates.Count == 0)
        {
            Log.Warn($"[MagicNumbers] No magic candidates found for '{funcName}'");
        }
        else
        {
            Log.Info($"[MagicNumbers] Candidates: {string.Join(" | ", candidates)}");
        }

        return candidates;
    }

    /// <summary>
    /// Основная точка входа. Возвращает первого кандидата или null.
    /// </summary>
    public static MagicNumbers? Extract(string baseJs, string funcName)
    {
        var candidates = ExtractCandidates(baseJs, funcName);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static void AddCandidate(
        List<MagicNumbers> list,
        HashSet<string> seen,
        MagicNumbers mn,
        string source)
    {
        var key = mn.ToString();
        if (!seen.Add(key)) return;
        if (list.Count >= MaxCandidates) return;

        list.Add(mn);
        Log.Debug($"[MagicNumbers] Candidate [{list.Count}] via {source}: {mn}");
    }

    // ═══════════════════════════════════════════════════════════════
    // STRATEGY 1: Algebraic analysis
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Алгебраически вычисляет magic numbers из структуры N-token функции.
    /// 
    /// <para>Шаги:</para>
    /// <list type="number">
    ///   <item>Находит N-token ветку по triple self-reference паттерну</item>
    ///   <item>Извлекает branch condition mask (из <c>(r&amp;MASK)==r</c>)</item>
    ///   <item>Находит <c>I[O[b^K1]](O[b^K2])</c> — вызов split("")</item>
    ///   <item>Используя O-словарь, вычисляет b: O[b^K1]="split", O[b^K2]=""</item>
    ///   <item>Находит все другие branch conditions в функции</item>
    ///   <item>Подбирает r, который активирует ТОЛЬКО n-token ветку</item>
    ///   <item>p = b ^ r</item>
    /// </list>
    /// </summary>
    private static IEnumerable<MagicNumbers> ResolveViaAlgebraicAnalysis(
        string baseJs, string funcDef, string funcName)
    {
        // 1. Загружаем O-словарь
        var dictElements = LoadGlobalDictionary(baseJs);
        if (dictElements is null || dictElements.Length < 50)
        {
            Log.Debug("[MagicNumbers] Global dictionary O not found or too small");
            yield break;
        }

        // 2. Находим индексы нужных строк в O
        int splitIdx = Array.IndexOf(dictElements, "split");
        int emptyIdx = Array.IndexOf(dictElements, "");
        int joinIdx = Array.IndexOf(dictElements, "join");

        if (splitIdx < 0 || emptyIdx < 0)
        {
            Log.Debug($"[MagicNumbers] Required O entries not found: split={splitIdx}, empty={emptyIdx}");
            yield break;
        }

        Log.Debug($"[MagicNumbers] O indices: split={splitIdx}, empty={emptyIdx}, join={joinIdx}");

        // 3. Ищем паттерн I[O[b^K1]](O[b^K2]) в N-token ветке
        var xorConstants = FindSplitXorConstants(funcDef);
        if (xorConstants is null)
        {
            Log.Debug("[MagicNumbers] XOR constants for split pattern not found");
            yield break;
        }

        int k1 = xorConstants.Value.K1; // b^K1 → index of "split"  
        int k2 = xorConstants.Value.K2; // b^K2 → index of ""

        Log.Debug($"[MagicNumbers] XOR constants: K1={k1} (for split), K2={k2} (for separator)");

        // 4. Вычисляем b
        // b ^ K1 = splitIdx → b = K1 ^ splitIdx
        int b_from_split = k1 ^ splitIdx;
        // b ^ K2 = emptyIdx → b = K2 ^ emptyIdx
        int b_from_empty = k2 ^ emptyIdx;

        if (b_from_split != b_from_empty)
        {
            Log.Debug($"[MagicNumbers] b mismatch: from split={b_from_split}, from empty={b_from_empty}");
            // Попробуем join вместо split для второй проверки
            if (joinIdx >= 0)
            {
                // Может K1 для join, K2 для empty
                var joinConstants = FindJoinXorConstants(funcDef);
                if (joinConstants is not null)
                {
                    int b_from_join = joinConstants.Value.K1 ^ joinIdx;
                    int b_from_join_sep = joinConstants.Value.K2 ^ emptyIdx;
                    if (b_from_join == b_from_join_sep && b_from_join == b_from_split)
                    {
                        Log.Debug($"[MagicNumbers] Confirmed b={b_from_split} via join pattern");
                    }
                }
            }
            // Используем b от split как primary
        }

        int b = b_from_split;
        Log.Debug($"[MagicNumbers] Computed b={b}");

        // 5. Находим branch mask
        int? branchMask = FindBranchMask(funcDef);
        if (branchMask is null)
        {
            Log.Debug("[MagicNumbers] Branch mask not found, trying common values");
            // Пробуем распространённые маски
            foreach (var mask in new[] { 90, 122, 126, 62, 94 })
            {
                foreach (var mn in TryMask(funcDef, b, mask))
                    yield return mn;
            }
            yield break;
        }

        Log.Debug($"[MagicNumbers] Branch mask: {branchMask.Value} (0b{Convert.ToString(branchMask.Value, 2)})");

        foreach (var mn in TryMask(funcDef, b, branchMask.Value))
            yield return mn;
    }

    /// <summary>
    /// Для заданного b и mask, находит безопасные r значения и генерирует MagicNumbers.
    /// </summary>
    private static IEnumerable<MagicNumbers> TryMask(string funcDef, int b, int mask)
    {
        // Собираем все другие branch conditions
        var otherConditions = FindAllBranchConditions(funcDef);

        // Перебираем допустимые r (подмножества mask)
        var validRValues = EnumerateSubsets(mask);

        foreach (int r in validRValues)
        {
            // Проверяем что только n-token ветка активируется
            if (IsOnlyNTokenBranchActive(r, mask, otherConditions))
            {
                int p = b ^ r;
                Log.Debug($"[MagicNumbers] Safe r={r}, p={p} (b={b}, mask={mask})");
                yield return new MagicNumbers([r, p]);
            }
        }
    }

    /// <summary>
    /// Проверяет, что для данного r активируется ТОЛЬКО n-token ветка.
    /// Все другие ветки должны быть неактивны.
    /// </summary>
    private static bool IsOnlyNTokenBranchActive(
        int r, int mask, List<BranchCondition> conditions)
    {
        foreach (var cond in conditions)
        {
            if (cond.Mask == mask && cond.Type == BranchType.AndEquals)
                continue; // Это наша ветка

            if (EvaluateCondition(r, cond))
                return false; // Другая ветка тоже активируется — unsafe
        }

        return true;
    }

    /// <summary>
    /// Вычисляет результат branch condition для заданного r.
    /// </summary>
    private static bool EvaluateCondition(int r, BranchCondition cond)
    {
        return cond.Type switch
        {
            // (r & MASK) == r
            BranchType.AndEquals => (r & cond.Mask) == r,

            // (r ^ A) >> B >= C && (r ^ D) < E
            BranchType.XorShiftRange => cond.Eval?.Invoke(r) ?? false,

            // (r >> A & B) == C
            BranchType.ShiftAndEquals => ((r >> cond.ShiftRight) & cond.Mask) == cond.ExpectedValue,

            // r + A >> B == C
            BranchType.AddShiftEquals => ((r + cond.AddValue) >> cond.ShiftRight) == cond.ExpectedValue,

            // r >> A & B (as boolean, in || context — true when == 0)
            BranchType.ShiftAndZero => ((r >> cond.ShiftRight) & cond.Mask) == 0,

            _ => false
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // BRANCH CONDITION PARSING
    // ═══════════════════════════════════════════════════════════════

    private enum BranchType
    {
        AndEquals,       // (r & MASK) == r
        XorShiftRange,   // (r^A)>>B >= C && (r^D) < E
        ShiftAndEquals,  // (r>>A & B) == C
        AddShiftEquals,  // r+A>>B == C
        ShiftAndZero     // r>>A & B (|| context)
    }

    private sealed record BranchCondition(
        BranchType Type,
        int Mask = 0,
        int ShiftRight = 0,
        int ExpectedValue = 0,
        int AddValue = 0,
        Func<int, bool>? Eval = null);

    /// <summary>
    /// Извлекает ВСЕ branch conditions из функции.
    /// Парсит характерные паттерны YouTube обфускации.
    /// </summary>
    private static List<BranchCondition> FindAllBranchConditions(string funcDef)
    {
        var conditions = new List<BranchCondition>();

        // Pattern: (r&MASK)==r
        foreach (Match m in AndEqualsRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int mask))
                conditions.Add(new BranchCondition(BranchType.AndEquals, Mask: mask));
        }

        // Pattern: (r^A)>>B>=C && (r^D)<E  or  (r^A)>>B>=C&&(r^D)<E
        foreach (Match m in XorShiftRangeRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int xorA) &&
                int.TryParse(m.Groups[2].Value, out int shiftB) &&
                int.TryParse(m.Groups[3].Value, out int geC) &&
                int.TryParse(m.Groups[4].Value, out int xorD) &&
                int.TryParse(m.Groups[5].Value, out int ltE))
            {
                conditions.Add(new BranchCondition(
                    BranchType.XorShiftRange,
                    Eval: r => ((r ^ xorA) >> shiftB) >= geC && (r ^ xorD) < ltE));
            }
        }

        // Pattern: (r>>A&B)==C
        foreach (Match m in ShiftAndEqualsRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int shift) &&
                int.TryParse(m.Groups[2].Value, out int mask) &&
                int.TryParse(m.Groups[3].Value, out int expected))
            {
                conditions.Add(new BranchCondition(
                    BranchType.ShiftAndEquals,
                    Mask: mask,
                    ShiftRight: shift,
                    ExpectedValue: expected));
            }
        }

        // Pattern: r+A>>B==C
        foreach (Match m in AddShiftEqualsRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int add) &&
                int.TryParse(m.Groups[2].Value, out int shift) &&
                int.TryParse(m.Groups[3].Value, out int expected))
            {
                conditions.Add(new BranchCondition(
                    BranchType.AddShiftEquals,
                    ShiftRight: shift,
                    ExpectedValue: expected,
                    AddValue: add));
            }
        }

        // Pattern: r>>A&B|| (in boolean/|| context — activates when result is 0)
        foreach (Match m in ShiftAndBoolRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int shift) &&
                int.TryParse(m.Groups[2].Value, out int mask))
            {
                conditions.Add(new BranchCondition(
                    BranchType.ShiftAndZero,
                    Mask: mask,
                    ShiftRight: shift));
            }
        }

        // ═══ Additional patterns from Dp ═══
        // r-A<B && ((r^C)&D)>=E
        foreach (Match m in SubLtAndXorMaskRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int sub) &&
                int.TryParse(m.Groups[2].Value, out int lt) &&
                int.TryParse(m.Groups[3].Value, out int xor) &&
                int.TryParse(m.Groups[4].Value, out int mask) &&
                int.TryParse(m.Groups[5].Value, out int ge))
            {
                conditions.Add(new BranchCondition(
                    BranchType.XorShiftRange,
                    Eval: r => (r - sub) < lt && ((r ^ xor) & mask) >= ge));
            }
        }

        // (r|A)==r
        foreach (Match m in OrEqualsRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int orVal))
            {
                conditions.Add(new BranchCondition(
                    BranchType.XorShiftRange,
                    Eval: r => (r | orVal) == r));
            }
        }

        // !((r^A)>>B)
        foreach (Match m in NotXorShiftRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int xor) &&
                int.TryParse(m.Groups[2].Value, out int shift))
            {
                conditions.Add(new BranchCondition(
                    BranchType.XorShiftRange,
                    Eval: r => ((r ^ xor) >> shift) == 0));
            }
        }

        // (r-A<<B)>=r && (r-C|D)<r variants
        // r-A<<B>=r&&(r+C^D)<r
        foreach (Match m in SubShiftGeqRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int sub) &&
                int.TryParse(m.Groups[2].Value, out int shift) &&
                int.TryParse(m.Groups[3].Value, out int add) &&
                int.TryParse(m.Groups[4].Value, out int xor))
            {
                conditions.Add(new BranchCondition(
                    BranchType.XorShiftRange,
                    Eval: r => ((r - sub) << shift) >= r && ((r + add) ^ xor) < r));
            }
        }

        // (r|A)>>B
        foreach (Match m in OrShiftRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int orVal) &&
                int.TryParse(m.Groups[2].Value, out int shift))
            {
                // In !(...) context: activates when ==0
                conditions.Add(new BranchCondition(
                    BranchType.XorShiftRange,
                    Eval: r => ((r | orVal) >> shift) == 0));
            }
        }

        // (r&A)==r — already handled above, but also r-B<<C>=r&&(r+D^E)<r
        // r-A<B patterns (standalone)
        foreach (Match m in SubLtRegex().Matches(funcDef))
        {
            if (int.TryParse(m.Groups[1].Value, out int sub) &&
                int.TryParse(m.Groups[2].Value, out int lt))
            {
                // This is usually combined with && — handle as weak condition
                // Don't add standalone, they're parts of complex conditions
            }
        }

        Log.Debug($"[MagicNumbers] Found {conditions.Count} branch conditions");
        return conditions;
    }

    /// <summary>
    /// Находит branch mask из паттерна <c>(r&amp;MASK)==r</c>.
    /// Это условие активации N-token ветки.
    /// </summary>
    private static int? FindBranchMask(string funcDef)
    {
        // Ищем (r&MASK)==r где r — первый параметр
        var match = NTokenBranchMaskRegex().Match(funcDef);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int mask))
            return mask;

        // Альтернативный формат: (MASK&r)==r
        match = NTokenBranchMaskAltRegex().Match(funcDef);
        if (match.Success && int.TryParse(match.Groups[1].Value, out mask))
            return mask;

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // XOR CONSTANT EXTRACTION
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct XorConstants(int K1, int K2);

    /// <summary>
    /// Находит XOR-константы из паттерна <c>I[O[b^K1]](O[b^K2])</c> (вызов split).
    /// 
    /// В N-token ветке первый вызов на третьем параметре (I) —
    /// это <c>I.split("")</c>, закодированное как <c>I[O[b^K1]](O[b^K2])</c>.
    /// </summary>
    private static XorConstants? FindSplitXorConstants(string funcDef)
    {
        // Паттерн: I[O[VARXORK1]](O[VARXORK2])
        // Где VAR — имя переменной b (var b=p^r)
        // Ищем: FirstParam[O[LocalVar^NUM1]](O[LocalVar^NUM2])

        // Сначала найдём имя переменной b: "var b=p^r" или "var V=X^D"
        var xorVarMatch = XorVarDefinitionRegex().Match(funcDef);
        if (!xorVarMatch.Success) return null;

        var xorVarName = xorVarMatch.Groups[1].Value;
        Log.Debug($"[MagicNumbers] XOR variable: {xorVarName}");

        // Теперь ищем первый I[O[xorVar^K1]](O[xorVar^K2]) в N-token ветке
        var pattern = $@"(\w)\[O\[{Regex.Escape(xorVarName)}\^(\d+)\]\]\(O\[{Regex.Escape(xorVarName)}\^(\d+)\]\)";
        var splitMatch = Regex.Match(funcDef, pattern);
        if (!splitMatch.Success) return null;

        if (int.TryParse(splitMatch.Groups[2].Value, out int k1) &&
            int.TryParse(splitMatch.Groups[3].Value, out int k2))
        {
            Log.Debug($"[MagicNumbers] Split pattern: {splitMatch.Groups[1].Value}" +
                      $"[O[{xorVarName}^{k1}]](O[{xorVarName}^{k2}])");
            return new XorConstants(k1, k2);
        }

        return null;
    }

    /// <summary>
    /// Находит XOR-константы для join паттерна (второе вхождение — результат).
    /// </summary>
    private static XorConstants? FindJoinXorConstants(string funcDef)
    {
        var xorVarMatch = XorVarDefinitionRegex().Match(funcDef);
        if (!xorVarMatch.Success) return null;

        var xorVarName = xorVarMatch.Groups[1].Value;

        // Ищем ПОСЛЕДНИЙ J[O[b^K1]](O[b^K2]) — это join("")
        var pattern = $@"\w\[O\[{Regex.Escape(xorVarName)}\^(\d+)\]\]\(O\[{Regex.Escape(xorVarName)}\^(\d+)\]\)";
        Match? lastMatch = null;
        foreach (Match m in Regex.Matches(funcDef, pattern))
            lastMatch = m;

        if (lastMatch is not null &&
            int.TryParse(lastMatch.Groups[1].Value, out int k1) &&
            int.TryParse(lastMatch.Groups[2].Value, out int k2))
        {
            return new XorConstants(k1, k2);
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // DICTIONARY LOADING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Загружает глобальный словарь из base.js.
    /// Поддерживает два формата:
    /// <list type="bullet">
    ///   <item><c>var O="...".split(";")</c> — split-формат</item>
    ///   <item><c>var h=["...", "...", ...]</c> — bracket-формат</item>
    /// </list>
    /// </summary>
    private static string[]? LoadGlobalDictionary(string baseJs)
    {
        // ═══ 1. Split-формат: var X="...".split(";") ═══
        var splitMatch = GlobalDictSplitRegex().Match(baseJs);
        if (splitMatch.Success)
        {
            var dictName = splitMatch.Groups[1].Value;
            var elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (elements is not null && elements.Length > 50)
            {
                Log.Debug($"[MagicNumbers] Loaded split dictionary '{dictName}': {elements.Length} elements");
                return elements;
            }
        }

        // ═══ 2. Bracket-формат: var h=["...", "...", ...] ═══
        var bracketMatch = GlobalDictBracketRegex().Match(baseJs);
        if (bracketMatch.Success)
        {
            var dictName = bracketMatch.Groups[1].Value;
            var elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (elements is not null && elements.Length > 50)
            {
                Log.Debug($"[MagicNumbers] Loaded bracket dictionary '{dictName}': {elements.Length} elements");
                return elements;
            }
        }

        // ═══ 3. Детекция по entry function — ищем имя словаря из кода функции ═══
        // Если стандартные паттерны не сработали, ищем по XOR-паттерну в функции
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // SUBSET ENUMERATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Перечисляет все подмножества битов маски (2^popcount вариантов).
    /// Для mask=90 (0b1011010, 4 бита) → 16 вариантов.
    /// </summary>
    private static IEnumerable<int> EnumerateSubsets(int mask)
    {
        // Собираем позиции установленных битов
        var bits = new List<int>(8);
        for (int i = 0; i < 30; i++)
        {
            if ((mask & (1 << i)) != 0)
                bits.Add(i);
        }

        int subsetCount = 1 << bits.Count;
        for (int s = 0; s < subsetCount; s++)
        {
            int value = 0;
            for (int i = 0; i < bits.Count; i++)
            {
                if ((s & (1 << i)) != 0)
                    value |= 1 << bits[i];
            }
            yield return value;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // STRATEGY 2: Direct numeric calls (fallback)
    // ═══════════════════════════════════════════════════════════════

    private sealed record CallInfo(
        int Position,
        List<string> RawArgs,
        bool IsCallMethod);

    private static List<CallInfo> FindAllCalls(string baseJs, string funcName)
    {
        var results = new List<CallInfo>();
        var span = baseJs.AsSpan();
        var funcNameSpan = funcName.AsSpan();
        int searchFrom = 0;

        while (searchFrom < span.Length)
        {
            int idx = span[searchFrom..].IndexOf(funcNameSpan, StringComparison.Ordinal);
            if (idx < 0) break;
            idx += searchFrom;
            searchFrom = idx + funcNameSpan.Length;

            if (idx > 0 && IsIdentChar(span[idx - 1])) continue;
            int afterName = idx + funcName.Length;
            if (afterName >= span.Length) continue;

            char afterChar = span[afterName];
            if (IsIdentChar(afterChar) && afterChar is not '(' and not '.' and not '[')
                continue;

            if (IsDefinitionOfFunction(span, idx, funcName.Length)) continue;

            bool isCallMethod = false;
            int parenSearchStart = afterName;

            if (afterChar == '.')
            {
                if (afterName + 5 <= span.Length &&
                    span.Slice(afterName + 1, 4).SequenceEqual("call"))
                {
                    isCallMethod = true;
                    parenSearchStart = afterName + 5;
                }
                else continue;
            }
            else if (afterChar == '[')
            {
                int bracketEnd = FindMatchingBracket(span, afterName);
                if (bracketEnd < 0) continue;
                int afterBracket = bracketEnd + 1;
                while (afterBracket < span.Length && span[afterBracket] is ' ' or '\t') afterBracket++;
                if (afterBracket >= span.Length || span[afterBracket] != '(') continue;
                isCallMethod = true;
                parenSearchStart = afterBracket;
            }

            int parenPos = parenSearchStart;
            while (parenPos < span.Length && span[parenPos] is ' ' or '\t') parenPos++;
            if (parenPos >= span.Length || span[parenPos] != '(') continue;

            var args = ParseCallArguments(baseJs, parenPos);
            if (args is null || args.Count == 0) continue;

            if (isCallMethod)
            {
                if (args.Count < 2) continue;
                args = args.Skip(1).ToList();
            }

            results.Add(new CallInfo(idx, args, isCallMethod));
        }

        return results;
    }

    private static IEnumerable<MagicNumbers> ResolveDirectCalls(List<CallInfo> calls)
    {
        foreach (var call in calls)
        {
            if (call.RawArgs.Count < 2) continue;

            var lastArg = call.RawArgs[^1].Trim();
            if (lastArg.Length == 0 || int.TryParse(lastArg, out _)) continue;

            var numericArgs = new List<int>();
            bool allNumeric = true;

            for (int i = 0; i < call.RawArgs.Count - 1; i++)
            {
                if (int.TryParse(call.RawArgs[i].Trim(), out var num))
                    numericArgs.Add(num);
                else { allNumeric = false; break; }
            }

            if (allNumeric && numericArgs.Count > 0 && numericArgs[0] is >= 0 and <= 200)
                yield return new MagicNumbers([.. numericArgs]);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // UTILITY
    // ═══════════════════════════════════════════════════════════════

    private static bool IsDefinitionOfFunction(
        ReadOnlySpan<char> span, int funcNamePos, int funcNameLength)
    {
        int afterName = funcNamePos + funcNameLength;
        if (afterName < span.Length)
        {
            int checkAfter = afterName;
            while (checkAfter < span.Length && span[checkAfter] is ' ' or '\t') checkAfter++;
            if (checkAfter < span.Length && span[checkAfter] == '=')
            {
                if (checkAfter + 1 < span.Length && span[checkAfter + 1] == '=') return false;
                return true;
            }
        }

        int beforeStart = funcNamePos - 1;
        while (beforeStart >= 0 && span[beforeStart] is ' ' or '\t') beforeStart--;
        if (beforeStart >= 7)
        {
            int kwEnd = beforeStart + 1;
            int kwStart = kwEnd - 8;
            if (kwStart >= 0 && span.Slice(kwStart, 8).SequenceEqual("function"))
            {
                if (kwStart == 0 || !IsIdentChar(span[kwStart - 1])) return true;
            }
        }

        return false;
    }

    private static int FindMatchingBracket(ReadOnlySpan<char> s, int openPos)
    {
        if (openPos >= s.Length || s[openPos] != '[') return -1;
        int depth = 1, i = openPos + 1;
        while (i < s.Length && depth > 0)
        {
            char c = s[i];
            if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) return i; }
            else if (c is '"' or '\'' or '`')
            {
                char q = c; i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i += 2; continue; }
                    if (s[i] == q) break;
                    i++;
                }
            }
            i++;
        }
        return -1;
    }

    private static List<string>? ParseCallArguments(string js, int openParenPos)
    {
        if (openParenPos >= js.Length || js[openParenPos] != '(') return null;
        var span = js.AsSpan();
        var args = new List<string>(4);
        int depth = 1, argStart = openParenPos + 1, i = argStart;

        while (i < span.Length && depth > 0)
        {
            char c = span[i];
            switch (c)
            {
                case '(' or '[' or '{': depth++; i++; break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        var lastArg = span[argStart..i].ToString().Trim();
                        if (lastArg.Length > 0) args.Add(lastArg);
                        return args;
                    }
                    i++; break;
                case ']' or '}': depth--; i++; break;
                case ',' when depth == 1:
                    args.Add(span[argStart..i].ToString().Trim());
                    argStart = i + 1;
                    i++; break;
                case '"' or '\'' or '`':
                    i = SkipJsString(js, i); break;
                default: i++; break;
            }
        }
        return null;
    }

    private static int SkipJsString(string s, int pos)
    {
        if (pos >= s.Length) return pos;
        char quote = s[pos++];
        while (pos < s.Length)
        {
            if (s[pos] == '\\' && pos + 1 < s.Length) { pos += 2; continue; }
            if (s[pos] == quote) return pos + 1;
            pos++;
        }
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '$';

    // ═══════════════════════════════════════════════════════════════
    // GENERATED REGEX
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Matches <c>var b=p^r</c> — XOR variable definition at start of function body.</summary>
    [GeneratedRegex(@"var\s+(\w+)\s*=\s*\w+\s*\^\s*\w+\s*;", RegexOptions.None)]
    private static partial Regex XorVarDefinitionRegex();

    /// <summary>Matches <c>(r&amp;90)==r</c> — N-token branch mask.</summary>
    [GeneratedRegex(@"\(r\s*&\s*(\d+)\)\s*==\s*r", RegexOptions.None)]
    private static partial Regex NTokenBranchMaskRegex();

    /// <summary>Matches <c>(90&amp;r)==r</c> — alternate form.</summary>
    [GeneratedRegex(@"\((\d+)\s*&\s*r\)\s*==\s*r", RegexOptions.None)]
    private static partial Regex NTokenBranchMaskAltRegex();

    /// <summary>Matches <c>(r^A)>>B>=C&&(r^D)&lt;E</c> or <c>(r^A)>>B>=C&&(r^D)&lt;E</c>.</summary>
    [GeneratedRegex(@"\(r\^(\d+)\)>>(\d+)>=(\d+)&&\(r\^(\d+)\)<(\d+)", RegexOptions.None)]
    private static partial Regex XorShiftRangeRegex();

    /// <summary>Matches <c>(r&amp;MASK)==r</c> — generic AND-equals pattern.</summary>
    [GeneratedRegex(@"\(r\s*&\s*(\d+)\)\s*==\s*r", RegexOptions.None)]
    private static partial Regex AndEqualsRegex();

    /// <summary>Matches <c>(r>>A&amp;B)==C</c>.</summary>
    [GeneratedRegex(@"\(r>>(\d+)&(\d+)\)==(\d+)", RegexOptions.None)]
    private static partial Regex ShiftAndEqualsRegex();

    /// <summary>Matches <c>r+A>>B==C</c>.</summary>
    [GeneratedRegex(@"r\+(\d+)>>(\d+)==(\d+)", RegexOptions.None)]
    private static partial Regex AddShiftEqualsRegex();

    /// <summary>Matches <c>r>>A&amp;B||</c> — shift-and in boolean context.</summary>
    [GeneratedRegex(@"r>>(\d+)&(\d+)\|\|", RegexOptions.None)]
    private static partial Regex ShiftAndBoolRegex();

    /// <summary>Matches <c>r-A&lt;B&&((r^C)&amp;D)>=E</c>.</summary>
    [GeneratedRegex(@"r-(\d+)<(\d+)&&\(\(r\^(\d+)\)&(\d+)\)>=(\d+)", RegexOptions.None)]
    private static partial Regex SubLtAndXorMaskRegex();

    /// <summary>Matches <c>(r|A)==r</c>.</summary>
    [GeneratedRegex(@"\(r\|(\d+)\)==r", RegexOptions.None)]
    private static partial Regex OrEqualsRegex();

    /// <summary>Matches <c>!((r^A)>>B)</c>.</summary>
    [GeneratedRegex(@"!\(\(r\^(\d+)\)>>(\d+)\)", RegexOptions.None)]
    private static partial Regex NotXorShiftRegex();

    /// <summary>Matches <c>r-A&lt;&lt;B>=r&&(r+C^D)&lt;r</c>.</summary>
    [GeneratedRegex(@"r-(\d+)<<(\d+)>=r&&\(r\+(\d+)\^(\d+)\)<r", RegexOptions.None)]
    private static partial Regex SubShiftGeqRegex();

    /// <summary>Matches <c>(r|A)>>B</c> in negation context.</summary>
    [GeneratedRegex(@"!\(\(r\|(\d+)\)>>(\d+)\)", RegexOptions.None)]
    private static partial Regex OrShiftRegex();

    /// <summary>Matches <c>r-A&lt;B</c> standalone.</summary>
    [GeneratedRegex(@"r-(\d+)<(\d+)", RegexOptions.None)]
    private static partial Regex SubLtRegex();

    /// <summary>Matches <c>var X="...".split(";")</c> — global dictionary.</summary>
    [GeneratedRegex(@"var\s+(\w{1,3})\s*=\s*""[^""]{500,}""\s*\.\s*split\s*\(", RegexOptions.None)]
    private static partial Regex GlobalDictSplitRegex();

    /// <summary>
    /// Matches <c>var h=["...", "...", ...]</c> — global bracket dictionary.
    /// Ищет var с коротким именем (1-3 символа), за которым идёт массив
    /// с минимум 50 строковыми элементами.
    /// </summary>
    [GeneratedRegex(@"var\s+(\w{1,3})\s*=\s*\[""[^""]{0,100}""\s*,\s*""", RegexOptions.None)]
    private static partial Regex GlobalDictBracketRegex();
}