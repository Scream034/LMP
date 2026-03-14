using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// Извлекает магические числа для вызова N-token функции.
/// 
/// <para><b>Алгоритм (v4 — параметрически-нейтральный):</b></para>
/// 
/// <para>
/// N-token функция — мульти-dispatch: разные ветки активируются разными
/// значениями первого параметра. Имена параметров извлекаются из сигнатуры
/// функции (<c>function(D,X,B,C)</c> → param1=D), а имя словаря — из тела.
/// </para>
/// </summary>
internal static partial class MagicNumbersExtractor
{
    private const int MaxCandidates = 10;

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════

    public static List<MagicNumbers> ExtractCandidates(string baseJs, string funcName)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseJs);
        ArgumentException.ThrowIfNullOrEmpty(funcName);

        var candidates = new List<MagicNumbers>();
        var seen = new HashSet<string>();

        var funcDef = JsFunctionExtractor.FindFunctionDefinition(baseJs, funcName);

        // ═══ Strategy 1: Алгебраический анализ (основная) ═══
        if (funcDef is not null)
        {
            foreach (var mn in ResolveViaAlgebraicAnalysis(baseJs, funcDef, funcName))
                AddCandidate(candidates, seen, mn, "algebraic");
        }

        // ═══ Strategy 2: Прямые вызовы (fallback, только если алгебра не дала результат) ═══
        // ВАЖНО: фильтруем по N-token condition чтобы не добавлять внутренние рекурсивные вызовы
        if (candidates.Count == 0 && funcDef is not null)
        {
            var ctx = ExtractContext(funcDef);
            BranchCondition? nTokenCond = null;
            if (ctx is not null)
            {
                var allConds = FindAllBranchConditions(funcDef, ctx);
                nTokenCond = FindNTokenBranchCondition(funcDef, ctx, allConds);
            }

            var calls = FindAllCalls(baseJs, funcName);
            Log.Debug($"[MagicNumbers] Found {calls.Count} call(s) of '{funcName}'");
            foreach (var mn in ResolveDirectCalls(calls, nTokenCond))
                AddCandidate(candidates, seen, mn, "direct");
        }

        if (candidates.Count == 0)
            Log.Warn($"[MagicNumbers] No magic candidates found for '{funcName}'");
        else
            Log.Info($"[MagicNumbers] Candidates: {string.Join(" | ", candidates)}");

        return candidates;
    }

    public static MagicNumbers? Extract(string baseJs, string funcName)
    {
        var candidates = ExtractCandidates(baseJs, funcName);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static void AddCandidate(
        List<MagicNumbers> list, HashSet<string> seen, MagicNumbers mn, string source)
    {
        var key = mn.ToString();
        if (!seen.Add(key)) return;
        if (list.Count >= MaxCandidates) return;
        list.Add(mn);
        Log.Debug($"[MagicNumbers] Candidate [{list.Count}] via {source}: {mn}");
    }

    // ═══════════════════════════════════════════════════════════════
    // CONTEXT: параметры функции и имя словаря
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Контекст конкретной N-token функции: имена параметров и словаря.
    /// </summary>
    private sealed record FuncContext(
        string Param1,    // Первый параметр (r, D, X)
        string Param2,    // Второй параметр (p, X, F)  
        string XorVar,    // Переменная b = param2 ^ param1
        string DictName); // Имя словаря (O, h, E)

    /// <summary>
    /// Извлекает контекст из определения функции.
    /// </summary>
    private static FuncContext? ExtractContext(string funcDef)
    {
        // Извлекаем имена параметров из сигнатуры
        var sigMatch = Regex.Match(funcDef, @"function\s*\((\w+)\s*,\s*(\w+)");
        if (!sigMatch.Success) return null;

        var param1 = sigMatch.Groups[1].Value;
        var param2 = sigMatch.Groups[2].Value;

        // Ищем var V = param2 ^ param1 (или param1 ^ param2)
        var xorPattern = $@"var\s+(\w+)\s*=\s*(?:{Regex.Escape(param2)}\s*\^\s*{Regex.Escape(param1)}|{Regex.Escape(param1)}\s*\^\s*{Regex.Escape(param2)})\s*;";
        var xorMatch = Regex.Match(funcDef, xorPattern);
        if (!xorMatch.Success)
        {
            Log.Debug($"[MagicNumbers] XOR variable not found for params {param1},{param2}");
            return null;
        }
        var xorVar = xorMatch.Groups[1].Value;

        // Ищем имя словаря: первое вхождение IDENT[xorVar^NUM]
        var dictPattern = $@"(\w{{1,3}})\[{Regex.Escape(xorVar)}\^\d+\]";
        var dictMatch = Regex.Match(funcDef, dictPattern);
        if (!dictMatch.Success)
        {
            Log.Debug($"[MagicNumbers] Dictionary name not found via XOR pattern");
            return null;
        }
        var dictName = dictMatch.Groups[1].Value;

        // Проверяем что это не имя параметра или локальной переменной
        if (dictName == param1 || dictName == param2 || dictName == xorVar)
        {
            // Ищем второе вхождение
            var dictMatch2 = Regex.Match(funcDef, dictPattern, RegexOptions.None,
                TimeSpan.FromSeconds(1));
            // Пробуем другой паттерн — param[DICT[xorVar^NUM]]
            var altDictPattern = $@"\w\[(\w{{1,3}})\[{Regex.Escape(xorVar)}\^\d+\]\]";
            var altMatch = Regex.Match(funcDef, altDictPattern);
            if (altMatch.Success)
                dictName = altMatch.Groups[1].Value;
        }

        Log.Debug($"[MagicNumbers] Context: param1={param1}, param2={param2}, " +
                  $"xorVar={xorVar}, dict={dictName}");
        return new FuncContext(param1, param2, xorVar, dictName);
    }

    // ═══════════════════════════════════════════════════════════════
    // STRATEGY 1: Algebraic analysis
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Алгебраический анализ N-token функции.
    /// <para>
    /// ИЗМЕНЕНИЕ v6: передаём загруженный словарь в FindSplitXorConstants
    /// для точной валидации split vs indexOf.
    /// </para>
    /// </summary>
    private static IEnumerable<MagicNumbers> ResolveViaAlgebraicAnalysis(
       string baseJs, string funcDef, string funcName)
    {
        var ctx = ExtractContext(funcDef);
        if (ctx is null) yield break;

        var dictElements = LoadGlobalDictionary(baseJs, ctx.DictName);
        if (dictElements is null || dictElements.Length < 50) yield break;

        int splitIdx = Array.IndexOf(dictElements, "split");
        int emptyIdx = Array.IndexOf(dictElements, "");

        if (splitIdx < 0 || emptyIdx < 0) yield break;

        // ═══ CHANGED: передаём dictElements для валидации split vs indexOf ═══
        var xorConstants = FindSplitXorConstants(funcDef, ctx, dictElements);
        if (xorConstants is null) yield break;

        int b;
        if (xorConstants.Value.IsK2Xored)
        {
            // K2 зашифрован через XOR → указывает на "" (разделитель)
            b = xorConstants.Value.K2 ^ emptyIdx;
        }
        else
        {
            // K2 прямой индекс → K1 указывает на "split"
            b = xorConstants.Value.K1 ^ splitIdx;
        }

        var allConditions = FindAllBranchConditions(funcDef, ctx);
        var nTokenCondition = FindNTokenBranchCondition(funcDef, ctx, allConditions);
        var otherConditions = allConditions
            .Where(c => !ReferenceEquals(c, nTokenCondition)).ToList();

        if (nTokenCondition is not null)
        {
            foreach (var mn in GenerateCandidatesFromCondition(
                nTokenCondition, otherConditions, b))
                yield return mn;
        }
        else
        {
            foreach (var cond in allConditions)
            {
                foreach (var mn in GenerateCandidatesFromCondition(cond,
                    allConditions.Where(c => !ReferenceEquals(c, cond)).ToList(), b))
                    yield return mn;
            }
        }
    }

    /// <summary>
    /// Определяет, какая branch condition соответствует N-token ветке.
    /// </summary>
    private static BranchCondition? FindNTokenBranchCondition(
      string funcDef, FuncContext ctx, List<BranchCondition> allConditions)
    {
        var p1 = Regex.Escape(ctx.Param1);
        var xv = Regex.Escape(ctx.XorVar);
        var dn = Regex.Escape(ctx.DictName);

        var splitPattern = $@"\w\[{dn}\[{xv}\^\d+\]\]\({dn}\[{xv}\^\d+\][^\)]*\)";
        var splitMatch = Regex.Match(funcDef, splitPattern);
        if (!splitMatch.Success)
        {
            var altPattern = $@"\w\[{dn}\[{xv}\^\d+\]\]\({dn}\[\d+\][^\)]*\)";
            splitMatch = Regex.Match(funcDef, altPattern);
            if (!splitMatch.Success) return null;
        }

        int splitPos = splitMatch.Index;

        BranchCondition? bestMatch = null;
        int bestDistance = int.MaxValue;

        // Идеальный поиск: берём ближайшее условие, стоящее ВЫШЕ вызова split
        foreach (var cond in allConditions)
        {
            if (splitPos > cond.Position)
            {
                int distance = splitPos - cond.Position;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestMatch = cond;
                }
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Генерирует кандидаты MagicNumbers из конкретной branch condition.
    /// <para>
    /// Для каждого типа condition алгебраически вычисляет значения <c>r</c>,
    /// которые активируют эту ветку, затем проверяет что другие ветки
    /// не конфликтуют, и вычисляет <c>p = b ^ r</c>.
    /// </para>
    /// </summary>
    private static IEnumerable<MagicNumbers> GenerateCandidatesFromCondition(
     BranchCondition targetCondition,
     List<BranchCondition> otherConditions,
     int b)
    {
        var rValues = GenerateRValuesForCondition(targetCondition);

        foreach (int r in rValues)
        {
            if (!EvaluateCondition(r, targetCondition))
                continue;

            int p = b ^ r;
            Log.Debug($"[MagicNumbers] Safe r={r}, p={p} (b={b}, " +
                      $"condition={targetCondition.Type})");
            yield return new MagicNumbers([r, p]);
        }
    }

    /// <summary>
    /// Генерирует значения <c>r</c>, удовлетворяющие конкретному condition.
    /// <para>
    /// Для <c>AndEquals(mask)</c>: все подмножества битов mask.
    /// Для <c>ModEquals(mask, expected, add)</c>: <c>r = expected + add + k*(mask+1)</c>.
    /// Для остальных: brute-force [0..200].
    /// </para>
    /// </summary>
    private static IEnumerable<int> GenerateRValuesForCondition(BranchCondition cond)
    {
        switch (cond.Type)
        {
            case BranchType.AndEquals:
                return EnumerateSubsets(cond.Mask);

            case BranchType.ModEquals:
                {
                    // (r - sub & mask) == expected
                    // => (r - sub) mod (mask + 1) == expected
                    // => r = expected + sub + k * (mask + 1)
                    var results = new List<int>(32);
                    int modulus = cond.Mask + 1;
                    int baseR = cond.ExpectedValue + cond.AddValue;
                    for (int k = 0; baseR + k * modulus < 256; k++)
                        results.Add(baseR + k * modulus);
                    return results;
                }

            case BranchType.ShiftAndEquals:
                {
                    // (r >> shift & mask) == expected
                    // => (r >> shift) содержит expected в нижних битах mask
                    var results = new List<int>(64);
                    int shiftedBase = cond.ExpectedValue;
                    // r = shiftedBase << shift + любые нижние биты
                    int lowerBits = 1 << cond.ShiftRight;
                    for (int high = shiftedBase; high < 256 >> cond.ShiftRight; high++)
                    {
                        if ((high & cond.Mask) != cond.ExpectedValue) continue;
                        for (int low = 0; low < lowerBits; low++)
                        {
                            int r = (high << cond.ShiftRight) | low;
                            if (r < 256) results.Add(r);
                        }
                    }
                    return results;
                }

            case BranchType.AddShiftEquals:
                {
                    // (r + add) >> shift == expected
                    // => r + add ∈ [expected << shift, (expected+1) << shift)
                    // => r ∈ [expected << shift - add, (expected+1) << shift - add)
                    var results = new List<int>(32);
                    int lo = (cond.ExpectedValue << cond.ShiftRight) - cond.AddValue;
                    int hi = ((cond.ExpectedValue + 1) << cond.ShiftRight) - cond.AddValue;
                    for (int r = Math.Max(0, lo); r < Math.Min(256, hi); r++)
                        results.Add(r);
                    return results;
                }

            default:
                // Brute-force для сложных/lambda conditions
                return Enumerable.Range(0, 256);
        }
    }

    /// <summary>
    /// Генерирует тестовые значения r для конкретного branch condition.
    /// </summary>
    private static IEnumerable<int> GenerateTestValues(BranchCondition cond)
    {
        if (cond.Type == BranchType.AndEquals)
            return EnumerateSubsets(cond.Mask);

        if (cond.Type == BranchType.ModEquals)
        {
            // (param1 - sub & modMask) == expected
            // param1 - sub ≡ expected (mod modMask+1)
            // param1 = expected + sub + k*(modMask+1)
            var results = new List<int>();
            int modulus = cond.Mask + 1;
            int base_r = cond.ExpectedValue + cond.AddValue;
            for (int k = 0; k < 20 && base_r + k * modulus < 300; k++)
                results.Add(base_r + k * modulus);
            return results;
        }

        // Для сложных условий — brute force до 200
        return Enumerable.Range(0, 200);
    }

    private static bool IsOnlyNTokenBranchActive(
       int r, int mask, List<BranchCondition> conditions)
    {
        // Считаем, сколько "сильных" не-N-token веток активируется.
        // "Сильная" ветка — та, что имеет return или присваивает результат,
        // а не просто делает side-effect (как URL-парсер).
        // Сейчас упростим: разрешим активацию до 1 дополнительной ветки.
        int otherActiveBranches = 0;

        foreach (var cond in conditions)
        {
            if (cond.Mask == mask && cond.Type == BranchType.AndEquals)
                continue; // Это наша ветка

            if (EvaluateCondition(r, cond))
            {
                // Не считаем URL-парсер как "конфликтующую" ветку
                if (cond.Type == BranchType.AndEquals && cond.Mask == 125)
                    continue;

                otherActiveBranches++;
            }
        }

        // Разрешаем активацию основной ветки + не более 1 другой
        return otherActiveBranches <= 1;
    }

    private static bool EvaluateCondition(int r, BranchCondition cond)
    {
        return cond.Type switch
        {
            BranchType.AndEquals => (r & cond.Mask) == r,
            BranchType.XorShiftRange => cond.Eval?.Invoke(r) ?? false,
            BranchType.ShiftAndEquals => ((r >> cond.ShiftRight) & cond.Mask) == cond.ExpectedValue,
            BranchType.AddShiftEquals => ((r + cond.AddValue) >> cond.ShiftRight) == cond.ExpectedValue,
            BranchType.ShiftAndZero => ((r >> cond.ShiftRight) & cond.Mask) == 0,
            BranchType.ModEquals => ((r - cond.AddValue) & cond.Mask) == cond.ExpectedValue,
            _ => false
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // BRANCH CONDITION PARSING
    // ═══════════════════════════════════════════════════════════════

    private enum BranchType
    {
        AndEquals,       // (r & MASK) == r
        XorShiftRange,   // сложная логика с >= и <
        ShiftAndEquals,  // (r>>A & B) == C
        AddShiftEquals,  // r+A>>B == C
        ShiftAndZero,    // r>>A & B (|| context)
        ModEquals        // (r-A & MASK) == C
    }


    private sealed record BranchCondition(
        BranchType Type,
        int Position,
        int Mask = 0,
        int ShiftRight = 0,
        int ExpectedValue = 0,
        int AddValue = 0,
        Func<int, bool>? Eval = null);

    /// <summary>
    /// Извлекает ВСЕ branch conditions, заменяя имя параметра на generic.
    /// </summary>
    private static List<BranchCondition> FindAllBranchConditions(
      string funcDef, FuncContext ctx)
    {
        var conditions = new List<BranchCondition>();
        var p1 = Regex.Escape(ctx.Param1);

        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}\s*&\s*(\d+)\)\s*==\s*{p1}"))
        {
            if (int.TryParse(m.Groups[1].Value, out int mask))
                conditions.Add(new BranchCondition(BranchType.AndEquals, m.Index, Mask: mask));
        }

        foreach (Match m in Regex.Matches(funcDef, $@"\((\d+)\s*&\s*{p1}\)\s*==\s*{p1}"))
        {
            if (int.TryParse(m.Groups[1].Value, out int mask))
                conditions.Add(new BranchCondition(BranchType.AndEquals, m.Index, Mask: mask));
        }

        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}\^(\d+)\)>>(\d+)>=(\d+)&&\({p1}\^(\d+)\)<(\d+)"))
        {
            if (int.TryParse(m.Groups[1].Value, out int xorA) && int.TryParse(m.Groups[2].Value, out int shiftB) &&
                int.TryParse(m.Groups[3].Value, out int geC) && int.TryParse(m.Groups[4].Value, out int xorD) &&
                int.TryParse(m.Groups[5].Value, out int ltE))
            {
                conditions.Add(new BranchCondition(BranchType.XorShiftRange, m.Index,
                    Eval: r => ((r ^ xorA) >> shiftB) >= geC && (r ^ xorD) < ltE));
            }
        }

        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}-(\d+)&(\d+)\)\s*==\s*(\d+)"))
        {
            if (int.TryParse(m.Groups[1].Value, out int sub) && int.TryParse(m.Groups[2].Value, out int mask) &&
                int.TryParse(m.Groups[3].Value, out int expected))
            {
                conditions.Add(new BranchCondition(BranchType.ModEquals, m.Index, Mask: mask, ExpectedValue: expected, AddValue: sub));
            }
        }

        // НОВОЕ: Паттерн для e42f4bf8 -> if((l-1^30)>=l&&l-7<<2<l)
        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}-(\d+)\^(\d+)\)>={p1}&&{p1}-(\d+)<<(\d+)<{p1}"))
        {
            if (int.TryParse(m.Groups[1].Value, out int sub1) && int.TryParse(m.Groups[2].Value, out int xor) &&
                int.TryParse(m.Groups[3].Value, out int sub2) && int.TryParse(m.Groups[4].Value, out int shift))
            {
                conditions.Add(new BranchCondition(BranchType.XorShiftRange, m.Index,
                    Eval: r => ((r - sub1) ^ xor) >= r && ((r - sub2) << shift) < r));
            }
        }

        // НОВОЕ: Паттерн для 99f55c01 -> if((D+4&35)<D&&(D-5|31)>=D)
        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}\+(\d+)&(\d+)\)<{p1}&&\({p1}-(\d+)\|(\d+)\)>={p1}"))
        {
            if (int.TryParse(m.Groups[1].Value, out int add) && int.TryParse(m.Groups[2].Value, out int andVal) &&
                int.TryParse(m.Groups[3].Value, out int sub) && int.TryParse(m.Groups[4].Value, out int orVal))
            {
                conditions.Add(new BranchCondition(BranchType.XorShiftRange, m.Index,
                    Eval: r => ((r + add) & andVal) < r && ((r - sub) | orVal) >= r));
            }
        }

        return conditions;
    }

    // ═══════════════════════════════════════════════════════════════
    // XOR CONSTANT EXTRACTION
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct XorConstants(int K1, int K2, bool IsK2Xored);

   /// <summary>
/// Находит XOR-константы из паттерна split/разделитель.
/// <para>
/// ИЗМЕНЕНИЕ v7: 
/// 1. Фильтруем indexOf — если второй аргумент содержит xorVar, это indexOf с динамическим startPos
/// 2. Для K2 direct проверяем согласованность K1: dict[b^K1] должно быть "split"
/// </para>
/// </summary>
private static XorConstants? FindSplitXorConstants(
    string funcDef, FuncContext ctx, string[]? dictElements = null)
{
    var xv = Regex.Escape(ctx.XorVar);
    var dn = Regex.Escape(ctx.DictName);

    // Паттерн: method(dict[xor^K1], dict[K2], secondArg...)
    // secondArg может содержать xorVar (для indexOf) или нет (для split)
    var pattern = $@"\w\[{dn}\[{xv}\^(\d+)\]\]\({dn}\[(\d+)\]((?:,[^)]*)?)\)";
    
    foreach (Match match in Regex.Matches(funcDef, pattern))
    {
        int k1 = int.Parse(match.Groups[1].Value);
        int k2 = int.Parse(match.Groups[2].Value);
        string secondArg = match.Groups[3].Value;

        // ═══ ФИЛЬТР indexOf: второй аргумент содержит xorVar ═══
        // indexOf("", startPos) где startPos = r^CONST
        // split("") обычно без второго аргумента или с константой
        if (secondArg.Contains(ctx.XorVar))
        {
            Log.Debug($"[MagicNumbers] Skipping indexOf candidate: K1={k1}, K2={k2} " +
                      $"(second arg contains xorVar '{ctx.XorVar}')");
            continue;
        }

        // Валидация через словарь
        if (dictElements is not null)
        {
            // K2 должен указывать на пустую строку
            if (k2 < 0 || k2 >= dictElements.Length || dictElements[k2] != "")
            {
                Log.Debug($"[MagicNumbers] K2={k2} validation failed: " +
                          $"dict[{k2}]='{(k2 < dictElements.Length ? dictElements[k2] : "out of bounds")}' != ''");
                continue;
            }

            // K1 должен указывать на "split"
            int splitIdx = Array.IndexOf(dictElements, "split");
            if (splitIdx < 0)
            {
                Log.Debug("[MagicNumbers] 'split' not found in dictionary");
                continue;
            }

            // Вычисляем b и проверяем согласованность
            int candidateB = splitIdx ^ k1;
            
            // Дополнительно: проверяем что K1 не указывает на indexOf
            int indexOfIdx = Array.IndexOf(dictElements, "indexOf");
            if (indexOfIdx >= 0 && (candidateB ^ k1) == indexOfIdx)
            {
                Log.Debug($"[MagicNumbers] K1={k1} points to 'indexOf', skipping");
                continue;
            }

            Log.Debug($"[MagicNumbers] Split XOR constants: K1={k1} (split), " +
                      $"K2={k2} (direct empty), b={candidateB}, validated via dictionary");
            return new XorConstants(k1, k2, false);
        }

        // Без словаря — возвращаем первый match (legacy)
        return new XorConstants(k1, k2, false);
    }

    // Паттерн с XOR в обоих аргументах
    var xorPattern = $@"\w\[{dn}\[{xv}\^(\d+)\]\]\({dn}\[{xv}\^(\d+)\]((?:,[^)]*)?)\)";
    
    foreach (Match match in Regex.Matches(funcDef, xorPattern))
    {
        int k1 = int.Parse(match.Groups[1].Value);
        int k2 = int.Parse(match.Groups[2].Value);
        string secondArg = match.Groups[3].Value;

        if (secondArg.Contains(ctx.XorVar))
        {
            Log.Debug($"[MagicNumbers] Skipping indexOf candidate: K1={k1}, K2={k2} (xor second arg)");
            continue;
        }

        if (dictElements is not null)
        {
            int splitIdx = Array.IndexOf(dictElements, "split");
            int emptyIdx = Array.IndexOf(dictElements, "");
            
            if (splitIdx < 0 || emptyIdx < 0) continue;

            // b должно быть одинаковым для split и empty
            int bFromSplit = splitIdx ^ k1;
            int bFromEmpty = emptyIdx ^ k2;

            if (bFromSplit == bFromEmpty)
            {
                Log.Debug($"[MagicNumbers] Split XOR constants: K1={k1}, K2={k2}, " +
                          $"b={bFromSplit}, validated via dictionary");
                return new XorConstants(k1, k2, true);
            }
            
            continue;
        }

        return new XorConstants(k1, k2, true);
    }

    Log.Debug("[MagicNumbers] Split XOR constants not found");
    return null;
}

    // ═══════════════════════════════════════════════════════════════
    // DICTIONARY LOADING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Загружает глобальный словарь по имени.
    /// </summary>
    private static string[]? LoadGlobalDictionary(string baseJs, string dictName)
    {
        // Сначала пробуем ExtractArrayElements по имени
        var elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
        if (elements is not null && elements.Length > 50)
        {
            var format = elements.Length > 0 && baseJs.Contains($"\"{dictName}\"")
                ? "bracket" : "split";
            Log.Debug($"[MagicNumbers] Loaded {format} dictionary '{dictName}': {elements.Length} elements");
            return elements;
        }

        // Fallback: ищем по паттерну
        var splitPattern = $@"var\s+{Regex.Escape(dictName)}\s*=\s*""[^""]+?""\s*\.\s*split\s*\(";
        if (Regex.IsMatch(baseJs, splitPattern))
        {
            elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (elements is not null && elements.Length > 50)
            {
                Log.Debug($"[MagicNumbers] Loaded split dictionary '{dictName}': {elements.Length} elements");
                return elements;
            }
        }

        var bracketPattern = $@"var\s+{Regex.Escape(dictName)}\s*=\s*\[";
        if (Regex.IsMatch(baseJs, bracketPattern))
        {
            elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (elements is not null && elements.Length > 50)
            {
                Log.Debug($"[MagicNumbers] Loaded bracket dictionary '{dictName}': {elements.Length} elements");
                return elements;
            }
        }

        Log.Debug($"[MagicNumbers] Dictionary '{dictName}' not found");
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // SUBSET ENUMERATION
    // ═══════════════════════════════════════════════════════════════

    private static IEnumerable<int> EnumerateSubsets(int mask)
    {
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
    // STRATEGY 2: Direct numeric calls
    // ═══════════════════════════════════════════════════════════════

    private sealed record CallInfo(int Position, List<string> RawArgs, bool IsCallMethod);

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

    private static IEnumerable<MagicNumbers> ResolveDirectCalls(
     List<CallInfo> calls, BranchCondition? nTokenCondition)
    {
        foreach (var call in calls)
        {
            var numericArgs = new List<int>();

            // Собираем все числа с начала (игнорируя this/строки)
            foreach (var arg in call.RawArgs)
            {
                if (int.TryParse(arg.Trim(), out var num))
                    numericArgs.Add(num);
                else
                    break; // Останавливаемся на первой переменной (например, `l` или `y`)
            }

            if (numericArgs.Count == 0) continue;

            int r = numericArgs[0];
            if (r is < 0 or > 65535) continue;

            if (nTokenCondition is not null && !EvaluateCondition(r, nTokenCondition))
            {
                Log.Debug($"[MagicNumbers] Direct call r={r} skipped: " +
                          $"doesn't activate N-token branch ({nTokenCondition.Type})");
                continue;
            }

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
}