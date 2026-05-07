using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// Извлекает магические числа для вызова N-token функции.
/// <para>
/// Поддерживает гибридный подход: 
/// 1. Скрапинг прямых вызовов функции в коде плеера.
/// 2. Разрешение XOR-вычисленных аргументов внутри обёрток (indirect calls).
/// 3. Алгебраический анализ с поддержкой multi-dispatch ядер (как Tx).
/// </para>
/// </summary>
internal static partial class MagicNumbersExtractor
{
    private const int MaxCandidates = 50;

    /// <summary>Извлекает список кандидатов магических чисел.</summary>
    public static List<MagicNumbers> ExtractCandidates(string baseJs, string funcName)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseJs);
        ArgumentException.ThrowIfNullOrEmpty(funcName);

        var candidates = new List<MagicNumbers>();
        var seen = new HashSet<string>();

        // 1. Скрапинг реальных вызовов
        var calls = FindAllCalls(baseJs, funcName);
        foreach (var call in calls)
        {
            var nums = new List<int>();
            foreach (var arg in call.RawArgs)
            {
                var cleanArg = arg.Trim();
                
                if (int.TryParse(cleanArg, out var num))
                {
                    nums.Add(num);
                }
                else if (nums.Count > 0)
                {
                    break;
                }
            }

            if (nums.Count > 0)
                AddCandidate(candidates, seen, new MagicNumbers([.. nums]), "direct_call");
        }

        // 2. Indirect XOR resolution — вызовы ядра внутри обёрток с XOR-аргументами
        foreach (var mn in ResolveIndirectXorCalls(baseJs, funcName))
            AddCandidate(candidates, seen, mn, "indirect_xor");

        // 3. Алгебраический анализ
        var funcDef = JsFunctionExtractor.FindFunctionDefinition(baseJs, funcName);
        if (funcDef is not null)
        {
            foreach (var mn in ResolveViaAlgebraicAnalysis(baseJs, funcDef, funcName))
                AddCandidate(candidates, seen, mn, "algebraic");
        }

        if (candidates.Count == 0)
            Log.Warn($"[MagicNumbers] No magic candidates found for '{funcName}'");

        return candidates;
    }

    private static void AddCandidate(List<MagicNumbers> list, HashSet<string> seen, MagicNumbers mn, string source)
    {
        var key = mn.ToString();
        if (!seen.Add(key)) return;
        if (list.Count >= MaxCandidates) return;
        
        list.Add(mn);
        Log.Debug($"[MagicNumbers] Candidate [{list.Count}] via {source}: {mn}");
    }

    // ═══════════════════════════════════════════════════════════════
    // INDIRECT XOR RESOLUTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Находит вызовы ядра <paramref name="coreName"/> внутри обёрток,
    /// где аргументы — XOR-выражения вида <c>T^число</c>.
    /// <para>
    /// Паттерн YouTube: обёртка x4(Z, k, N, a) вычисляет T = k^Z,
    /// затем вызывает coreName(T^const1, T^const2, N).
    /// Мы знаем вызов x4(7, 2271, ...) из direct_call, поэтому
    /// можем вычислить T = 2271^7 = 2264 → coreName(2264^const1, 2264^const2, ...).
    /// </para>
    /// </summary>
    private static List<MagicNumbers> ResolveIndirectXorCalls(string baseJs, string coreName)
    {
        var results = new List<MagicNumbers>(8);
        var span = baseJs.AsSpan();
        var coreSpan = coreName.AsSpan();

        // Находим все вызовы coreName(expr1, expr2, ...) где expr содержит "^"
        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int corePos = span[searchFrom..].IndexOf(coreSpan, StringComparison.Ordinal);
            if (corePos < 0) break;
            corePos += searchFrom;
            searchFrom = corePos + coreSpan.Length;

            // Проверяем что это не определение и не часть другого идентификатора
            if (corePos > 0 && IsIdentCharStrict(span[corePos - 1])) continue;
            int afterCore = corePos + coreName.Length;
            if (afterCore < span.Length && IsIdentCharStrict(span[afterCore])
                && span[afterCore] is not '(') continue;

            // Ищем открывающую скобку
            int p = afterCore;
            while (p < span.Length && span[p] is ' ' or '\t') p++;
            if (p >= span.Length || span[p] != '(') continue;

            // Парсим аргументы
            var args = ParseCallArguments(baseJs, p);
            if (args is null || args.Count < 2) continue;

            // Проверяем что первые два аргумента — XOR-выражения (содержат ^)
            var arg0 = args[0].Trim();
            var arg1 = args[1].Trim();
            if (!arg0.Contains('^') || !arg1.Contains('^')) continue;

            // Извлекаем паттерн: VAR^число
            var xor0 = ParseXorExpression(arg0);
            var xor1 = ParseXorExpression(arg1);
            if (xor0 is null || xor1 is null) continue;
            if (xor0.Value.VarName != xor1.Value.VarName) continue;

            var localVar = xor0.Value.VarName;
            int const0 = xor0.Value.Constant;
            int const1 = xor1.Value.Constant;

            // Ищем определение localVar в объемлющей функции
            // Паттерн: var T = k ^ Z; (где k, Z — параметры обёртки)
            var enclosingFunc = FindEnclosingFunction(baseJs, corePos);
            if (enclosingFunc is null) continue;

            var localVarValues = ResolveLocalXorVar(
                baseJs, enclosingFunc.Value.Body, enclosingFunc.Value.Params,
                localVar, enclosingFunc.Value.Name, coreName);

            foreach (int tValue in localVarValues)
            {
                int magic1 = tValue ^ const0;
                int magic2 = tValue ^ const1;
                results.Add(new MagicNumbers(magic1, magic2));

                if (results.Count >= 16) return results;
            }
        }

        return results;
    }

    /// <summary>
    /// Парсит выражение вида "VAR^число" или "число^VAR".
    /// </summary>
    private static (string VarName, int Constant)? ParseXorExpression(string expr)
    {
        var parts = expr.Split('^');
        if (parts.Length != 2) return null;

        var left = parts[0].Trim();
        var right = parts[1].Trim();

        if (int.TryParse(right, out int constRight) && IsValidIdentifier(left))
            return (left, constRight);
        if (int.TryParse(left, out int constLeft) && IsValidIdentifier(right))
            return (right, constLeft);

        return null;
    }

    private static bool IsValidIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] is not ('_' or '$')) return false;
        for (int i = 1; i < s.Length; i++)
            if (!IsIdentCharStrict(s[i])) return false;
        return true;
    }

    /// <summary>
    /// Информация об объемлющей функции.
    /// </summary>
    private readonly record struct EnclosingFuncInfo(
        string Name, string[] Params, string Body, int BodyStart);

    /// <summary>
    /// Находит функцию, содержащую позицию <paramref name="pos"/>.
    /// </summary>
    private static EnclosingFuncInfo? FindEnclosingFunction(string baseJs, int pos)
    {
        var span = baseJs.AsSpan();

        // Ищем ближайшее "function" перед pos
        int searchLimit = Math.Max(0, pos - 5000);
        int bestFuncPos = -1;

        for (int i = pos - 1; i >= searchLimit; i--)
        {
            if (span[i] != 'f') continue;
            if (i + 8 > span.Length) continue;
            if (!span.Slice(i, 8).SequenceEqual("function")) continue;
            if (i > 0 && IsIdentCharStrict(span[i - 1])) continue;

            // Нашли "function" — проверяем что pos внутри тела
            int parenStart = -1;
            for (int j = i + 8; j < span.Length && j < i + 80; j++)
            {
                if (span[j] == '(') { parenStart = j; break; }
                if (span[j] is not (' ' or '\t' or '*')) break;
            }
            if (parenStart < 0) continue;

            int parenEnd = JsFunctionExtractor.FindMatchingParen(baseJs, parenStart);
            if (parenEnd < 0) continue;

            int bodyBrace = -1;
            for (int j = parenEnd + 1; j < span.Length && j < parenEnd + 20; j++)
            {
                if (span[j] == '{') { bodyBrace = j; break; }
                if (span[j] is not (' ' or '\t' or '\n' or '\r')) break;
            }
            if (bodyBrace < 0) continue;

            int braceEnd = JsFunctionExtractor.FindMatchingBrace(baseJs, bodyBrace);
            if (braceEnd < 0 || braceEnd < pos) continue;

            // pos внутри этой функции
            bestFuncPos = i;

            // Извлекаем имя
            int nameEnd = i - 1;
            while (nameEnd >= 0 && span[nameEnd] is ' ' or '\t') nameEnd--;
            if (nameEnd >= 0 && span[nameEnd] == '=')
            {
                nameEnd--;
                while (nameEnd >= 0 && span[nameEnd] is ' ' or '\t') nameEnd--;
                int nameStart = nameEnd;
                while (nameStart >= 0 && IsIdentCharStrict(span[nameStart])) nameStart--;
                nameStart++;

                string funcName = span[nameStart..(nameEnd + 1)].ToString();
                string paramsStr = span[(parenStart + 1)..parenEnd].ToString();
                string[] parms = paramsStr.Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                string body = baseJs[(bodyBrace + 1)..braceEnd];

                return new EnclosingFuncInfo(funcName, parms, body, bodyBrace + 1);
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Разрешает значения локальной переменной <paramref name="localVar"/>,
    /// определённой как XOR параметров обёртки: <c>var T = k ^ Z;</c>.
    /// <para>
    /// Находит все прямые вызовы обёртки с числовыми аргументами
    /// и вычисляет T = arg1 ^ arg0 для каждого вызова.
    /// </para>
    /// </summary>
    private static List<int> ResolveLocalXorVar(string baseJs, string funcBody,
        string[] funcParams, string localVar, string funcName, string coreName)
    {
        var values = new List<int>(4);

        if (funcParams.Length < 2) return values;

        // Ищем "var localVar = paramX ^ paramY;"
        var varPattern = $@"var\s+{Regex.Escape(localVar)}\s*=\s*(\w+)\s*\^\s*(\w+)";
        var varMatch = Regex.Match(funcBody, varPattern);
        if (!varMatch.Success) return values;

        var xorLeft = varMatch.Groups[1].Value;
        var xorRight = varMatch.Groups[2].Value;

        // Определяем какие параметры обёртки XOR-ятся
        int leftParamIdx = Array.IndexOf(funcParams, xorLeft);
        int rightParamIdx = Array.IndexOf(funcParams, xorRight);
        if (leftParamIdx < 0 || rightParamIdx < 0) return values;

        // Ищем все вызовы обёртки funcName(число, число, ...) в base.js
        var wrapperCalls = FindAllCalls(baseJs, funcName);
        foreach (var call in wrapperCalls)
        {
            if (call.RawArgs.Count <= Math.Max(leftParamIdx, rightParamIdx)) continue;

            var leftArg = call.RawArgs[leftParamIdx].Trim();
            var rightArg = call.RawArgs[rightParamIdx].Trim();

            // Аргументы могут быть XOR-выражениями сами (C^8138)
            if (TryResolveNumericExpr(leftArg, out int leftVal)
                && TryResolveNumericExpr(rightArg, out int rightVal))
            {
                int tValue = leftVal ^ rightVal;
                if (!values.Contains(tValue))
                {
                    values.Add(tValue);
                    Log.Debug($"[MagicNumbers] Indirect: {funcName}({leftArg}, {rightArg}, ...) " +
                              $"→ {localVar}={tValue}");
                }
            }
        }

        // Рекурсивный случай: аргументы обёртки тоже XOR-выражения
        // Пример: x4(C^8138, C^5906, m, N) где C=8141
        // Нужно найти вызовы обёрток верхнего уровня
        if (values.Count == 0)
        {
            values.AddRange(ResolveXorArgsRecursively(
                baseJs, funcName, funcParams, leftParamIdx, rightParamIdx, coreName, depth: 0));
        }

        return values;
    }

    /// <summary>
    /// Рекурсивно разрешает XOR-аргументы, прослеживая цепочку вызовов.
    /// <para>
    /// Пример: rP вызывает x4(C^8138, C^5906, m, N), где C = k^Z.
    /// Мы знаем rP(5, 8136, ...) → C = 8136^5 = 8141 → x4(8141^8138, 8141^5906) = x4(7, 2271).
    /// Внутри x4: T = 2271^7 = 2264 → rP(2264^2271, 2264^2493) = rP(7, 357).
    /// </para>
    /// </summary>
    private static List<int> ResolveXorArgsRecursively(string baseJs, string funcName,
        string[] funcParams, int leftIdx, int rightIdx, string coreName, int depth)
    {
        var values = new List<int>(4);
        if (depth > 3) return values; // предел рекурсии

        var span = baseJs.AsSpan();
        var funcSpan = funcName.AsSpan();
        int searchFrom = 0;

        while (searchFrom < span.Length)
        {
            int pos = span[searchFrom..].IndexOf(funcSpan, StringComparison.Ordinal);
            if (pos < 0) break;
            pos += searchFrom;
            searchFrom = pos + funcSpan.Length;

            if (pos > 0 && IsIdentCharStrict(span[pos - 1])) continue;
            int afterName = pos + funcName.Length;
            if (afterName < span.Length && IsIdentCharStrict(span[afterName])
                && span[afterName] != '(') continue;

            int p = afterName;
            while (p < span.Length && span[p] is ' ' or '\t') p++;
            if (p >= span.Length || span[p] != '(') continue;

            var args = ParseCallArguments(baseJs, p);
            if (args is null || args.Count <= Math.Max(leftIdx, rightIdx)) continue;

            var leftArg = args[leftIdx].Trim();
            var rightArg = args[rightIdx].Trim();

            // Оба аргумента — XOR-выражения с одним и тем же VAR
            var xorL = ParseXorExpression(leftArg);
            var xorR = ParseXorExpression(rightArg);
            if (xorL is null || xorR is null) continue;
            if (xorL.Value.VarName != xorR.Value.VarName) continue;

            var outerVar = xorL.Value.VarName;

            // Найдём объемлющую функцию для этого вызова
            var enclosing = FindEnclosingFunction(baseJs, pos);
            if (enclosing is null) continue;

            // Ищем "var outerVar = paramA ^ paramB;" в объемлющей функции
            var varPat = $@"var\s+{Regex.Escape(outerVar)}\s*=\s*(\w+)\s*\^\s*(\w+)";
            var varMatch = Regex.Match(enclosing.Value.Body, varPat);
            if (!varMatch.Success) continue;

            var outerLeft = varMatch.Groups[1].Value;
            var outerRight = varMatch.Groups[2].Value;

            int outerLeftIdx = Array.IndexOf(enclosing.Value.Params, outerLeft);
            int outerRightIdx = Array.IndexOf(enclosing.Value.Params, outerRight);
            if (outerLeftIdx < 0 || outerRightIdx < 0) continue;

            // Ищем прямые вызовы объемлющей функции
            var outerCalls = FindAllCalls(baseJs, enclosing.Value.Name);
            foreach (var outerCall in outerCalls)
            {
                if (outerCall.RawArgs.Count <= Math.Max(outerLeftIdx, outerRightIdx)) continue;

                if (TryResolveNumericExpr(outerCall.RawArgs[outerLeftIdx].Trim(), out int oL)
                    && TryResolveNumericExpr(outerCall.RawArgs[outerRightIdx].Trim(), out int oR))
                {
                    int outerVarVal = oL ^ oR;
                    int resolvedLeft = outerVarVal ^ xorL.Value.Constant;
                    int resolvedRight = outerVarVal ^ xorR.Value.Constant;
                    int tValue = resolvedLeft ^ resolvedRight;

                    if (!values.Contains(tValue))
                    {
                        values.Add(tValue);
                        Log.Debug($"[MagicNumbers] Indirect recursive: " +
                                  $"{enclosing.Value.Name}→{funcName} " +
                                  $"outerVar={outerVarVal} → T={tValue}");
                    }
                }
            }

            // Если прямые вызовы тоже XOR — рекурсия
            if (values.Count == 0 && outerCalls.Count > 0)
            {
                values.AddRange(ResolveXorArgsRecursively(
                    baseJs, enclosing.Value.Name, enclosing.Value.Params,
                    outerLeftIdx, outerRightIdx, coreName, depth + 1));
            }
        }

        return values;
    }

    /// <summary>
    /// Пробует вычислить числовое выражение: литерал или VAR^число 
    /// (для случаев когда VAR — известная константа, переданная как аргумент).
    /// </summary>
    private static bool TryResolveNumericExpr(string expr, out int value)
    {
        value = 0;
        if (int.TryParse(expr, out value)) return true;

        // Простой XOR двух литералов: число^число
        var parts = expr.Split('^');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out int a)
            && int.TryParse(parts[1].Trim(), out int b))
        {
            value = a ^ b;
            return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // PARSING: Direct Calls
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct CallInfo(int Position, List<string> RawArgs, bool IsCallMethod);

    /// <summary>
    /// Строгий парсер идентификаторов (без учета точки).
    /// </summary>
    private static bool IsIdentCharStrict(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

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

            if (idx > 0 && IsIdentCharStrict(span[idx - 1])) continue;
            if (IsDefinitionOfFunction(span, idx, funcName.Length)) continue;

            int p = idx + funcName.Length;
            while (p < span.Length && span[p] is ' ' or '\t' or '\n' or '\r') p++;
            if (p >= span.Length) continue;

            char nextChar = span[p];
            bool isCallMethod = false;

            if (nextChar == '.')
            {
                if (p + 5 <= span.Length && span.Slice(p + 1, 4).SequenceEqual("call"))
                {
                    isCallMethod = true;
                    p += 5;
                }
                else continue;
            }
            else if (nextChar == '[')
            {
                int bracketEnd = JsFunctionExtractor.FindMatchingBracket(baseJs, p);
                if (bracketEnd < 0) continue;
                p = bracketEnd + 1;
                isCallMethod = true;
            }

            while (p < span.Length && span[p] is ' ' or '\t' or '\n' or '\r') p++;
            if (p >= span.Length || span[p] != '(') continue;

            var args = ParseCallArguments(baseJs, p);
            if (args is null || args.Count == 0) continue;

            if (isCallMethod)
            {
                if (args.Count < 2) continue;
                args = [.. args.Skip(1)];
            }

            results.Add(new CallInfo(idx, args, isCallMethod));
        }

        return results;
    }

    private static bool IsDefinitionOfFunction(ReadOnlySpan<char> span, int funcNamePos, int funcNameLength)
    {
        int afterName = funcNamePos + funcNameLength;
        while (afterName < span.Length && span[afterName] is ' ' or '\t' or '\n' or '\r') afterName++;
        if (afterName < span.Length && span[afterName] == '=')
        {
            if (afterName + 1 < span.Length && span[afterName + 1] == '=') return false;
            return true;
        }

        int beforeStart = funcNamePos - 1;
        while (beforeStart >= 0 && span[beforeStart] is ' ' or '\t' or '\n' or '\r') beforeStart--;
        if (beforeStart >= 7)
        {
            int kwStart = beforeStart - 7;
            if (kwStart >= 0 && span.Slice(kwStart, 8).SequenceEqual("function"))
                return kwStart == 0 || !IsIdentCharStrict(span[kwStart - 1]);
        }

        return false;
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
                    i = JsFunctionExtractor.SkipString(js, i); break;
                default: i++; break;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // ALGEBRAIC ANALYSIS
    // ═══════════════════════════════════════════════════════════════

    private sealed record FuncContext(string Param1, string Param2, string XorVar, string DictName);

    private static FuncContext? ExtractContext(string funcDef)
    {
        var sigMatch = Regex.Match(funcDef, @"function\s*\((\w+)\s*,\s*(\w+)");
        if (!sigMatch.Success) return null;

        var param1 = sigMatch.Groups[1].Value;
        var param2 = sigMatch.Groups[2].Value;

        var xorPattern = $@"var\s+(\w+)\s*=\s*(?:{Regex.Escape(param2)}\s*\^\s*{Regex.Escape(param1)}|{Regex.Escape(param1)}\s*\^\s*{Regex.Escape(param2)})\s*;";
        var xorMatch = Regex.Match(funcDef, xorPattern);
        if (!xorMatch.Success) return null;
        
        var xorVar = xorMatch.Groups[1].Value;
        var dictPattern = $@"(\w+?)\[{Regex.Escape(xorVar)}\^\d+\]";
        var dictMatch = Regex.Match(funcDef, dictPattern);
        if (!dictMatch.Success) return null;
        
        var dictName = dictMatch.Groups[1].Value;

        if (dictName == param1 || dictName == param2 || dictName == xorVar)
        {
            var altDictPattern = $@"\w+\[(\w+?)\[{Regex.Escape(xorVar)}\^\d+\]\]";
            var altMatch = Regex.Match(funcDef, altDictPattern);
            if (altMatch.Success) dictName = altMatch.Groups[1].Value;
        }

        return new FuncContext(param1, param2, xorVar, dictName);
    }

    private static IEnumerable<MagicNumbers> ResolveViaAlgebraicAnalysis(string baseJs, string funcDef, string funcName)
    {
        var ctx = ExtractContext(funcDef);
        if (ctx is null) yield break;

        var dictElements = LoadGlobalDictionary(baseJs, ctx.DictName);
        if (dictElements is null || dictElements.Length < 50) yield break;

        var bValues = FindAllBValues(funcDef, ctx, dictElements);
        if (bValues.Count == 0) yield break;

        var allConditions = FindAllBranchConditions(funcDef, ctx);
        var nTokenCondition = FindNTokenBranchCondition(funcDef, ctx, allConditions);

        var targetConditions = nTokenCondition is not null ? [nTokenCondition] : allConditions;

        foreach (var b in bValues)
        {
            foreach (var cond in targetConditions)
            {
                foreach (var r in GenerateRValuesForCondition(cond))
                {
                    if (!EvaluateCondition(r, cond)) continue;
                    int p = b ^ r;
                    yield return new MagicNumbers([r, p]);
                }
            }
        }
    }

    /// <summary>
    /// Находит все возможные значения XOR-ключа b, сканируя ВСЕ вхождения
    /// пустых строк "" и слова "split" в словаре.
    /// </summary>
    private static HashSet<int> FindAllBValues(string funcDef, FuncContext ctx, string[] dictElements)
    {
        var bValues = new HashSet<int>();
        var xv = Regex.Escape(ctx.XorVar);
        var dn = Regex.Escape(ctx.DictName);

        var splitIndices = new List<int>();
        var emptyIndices = new List<int>();
        for (int i = 0; i < dictElements.Length; i++)
        {
            if (dictElements[i] == "split") splitIndices.Add(i);
            if (dictElements[i] == "") emptyIndices.Add(i);
        }

        if (splitIndices.Count == 0 || emptyIndices.Count == 0) return bValues;

        // Pattern 1: K2 is direct (not XORed)
        var pattern = $@"\w+\[{dn}\[{xv}\^(\d+)\]\]\({dn}\[(\d+)\]((?:,[^)]*)?)\)";
        foreach (Match match in Regex.Matches(funcDef, pattern))
        {
            int k1 = int.Parse(match.Groups[1].Value);
            int k2 = int.Parse(match.Groups[2].Value);
            string secondArg = match.Groups[3].Value;

            if (secondArg.Contains(ctx.XorVar)) continue;
            if (!emptyIndices.Contains(k2)) continue;

            foreach (var sIdx in splitIndices)
            {
                bValues.Add(sIdx ^ k1);
            }
        }

        // Pattern 2: K2 is XORed
        var xorPattern = $@"\w+\[{dn}\[{xv}\^(\d+)\]\]\({dn}\[{xv}\^(\d+)\]((?:,[^)]*)?)\)";
        foreach (Match match in Regex.Matches(funcDef, xorPattern))
        {
            int k1 = int.Parse(match.Groups[1].Value);
            int k2 = int.Parse(match.Groups[2].Value);
            string secondArg = match.Groups[3].Value;

            if (secondArg.Contains(ctx.XorVar)) continue;

            foreach (var sIdx in splitIndices)
            {
                foreach (var eIdx in emptyIndices)
                {
                    if ((sIdx ^ k1) == (eIdx ^ k2))
                        bValues.Add(sIdx ^ k1);
                }
            }
        }

        return bValues;
    }

    private static BranchCondition? FindNTokenBranchCondition(string funcDef, FuncContext ctx, List<BranchCondition> allConditions)
    {
        var xv = Regex.Escape(ctx.XorVar);
        var dn = Regex.Escape(ctx.DictName);

        var splitPattern = $@"\w+\[{dn}\[{xv}\^\d+\]\]\({dn}\[{xv}\^\d+\][^\)]*\)";
        var splitMatch = Regex.Match(funcDef, splitPattern);
        if (!splitMatch.Success)
        {
            var altPattern = $@"\w+\[{dn}\[{xv}\^\d+\]\]\({dn}\[\d+\][^\)]*\)";
            splitMatch = Regex.Match(funcDef, altPattern);
            if (!splitMatch.Success) return null;
        }

        int splitPos = splitMatch.Index;
        BranchCondition? bestMatch = null;
        int bestDistance = int.MaxValue;

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

    private static IEnumerable<int> GenerateRValuesForCondition(BranchCondition cond)
    {
        switch (cond.Type)
        {
            case BranchType.AndEquals:
                var bits = new List<int>(8);
                for (int i = 0; i < 30; i++) if ((cond.Mask & (1 << i)) != 0) bits.Add(i);
                int subsetCount = 1 << bits.Count;
                for (int s = 0; s < subsetCount; s++)
                {
                    int value = 0;
                    for (int i = 0; i < bits.Count; i++) if ((s & (1 << i)) != 0) value |= 1 << bits[i];
                    yield return value;
                }
                break;
            case BranchType.ModEquals:
                int modulus = cond.Mask + 1;
                int baseR = cond.ExpectedValue + cond.AddValue;
                for (int k = 0; baseR + k * modulus < 256; k++) yield return baseR + k * modulus;
                break;
            case BranchType.ShiftAndEquals:
                int shiftedBase = cond.ExpectedValue;
                int lowerBits = 1 << cond.ShiftRight;
                for (int high = shiftedBase; high < 256 >> cond.ShiftRight; high++)
                {
                    if ((high & cond.Mask) != cond.ExpectedValue) continue;
                    for (int low = 0; low < lowerBits; low++)
                    {
                        int r = (high << cond.ShiftRight) | low;
                        if (r < 256) yield return r;
                    }
                }
                break;
            case BranchType.AddShiftEquals:
                int lo = (cond.ExpectedValue << cond.ShiftRight) - cond.AddValue;
                int hi = ((cond.ExpectedValue + 1) << cond.ShiftRight) - cond.AddValue;
                for (int r = Math.Max(0, lo); r < Math.Min(256, hi); r++) yield return r;
                break;
            default:
                for (int r = 0; r < 256; r++) yield return r;
                break;
        }
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

    private enum BranchType { AndEquals, XorShiftRange, ShiftAndEquals, AddShiftEquals, ShiftAndZero, ModEquals }

    private sealed record BranchCondition(BranchType Type, int Position, int Mask = 0, int ShiftRight = 0, int ExpectedValue = 0, int AddValue = 0, Func<int, bool>? Eval = null);

    private static List<BranchCondition> FindAllBranchConditions(string funcDef, FuncContext ctx)
    {
        var conditions = new List<BranchCondition>();
        var p1 = Regex.Escape(ctx.Param1);

        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}\s*&\s*(\d+)\)\s*==\s*{p1}")) if (int.TryParse(m.Groups[1].Value, out int mask)) conditions.Add(new BranchCondition(BranchType.AndEquals, m.Index, Mask: mask));
        foreach (Match m in Regex.Matches(funcDef, $@"\((\d+)\s*&\s*{p1}\)\s*==\s*{p1}")) if (int.TryParse(m.Groups[1].Value, out int mask)) conditions.Add(new BranchCondition(BranchType.AndEquals, m.Index, Mask: mask));
        
        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}\^(\d+)\)>>(\d+)>=(\d+)&&\({p1}\^(\d+)\)<(\d+)"))
            if (int.TryParse(m.Groups[1].Value, out int xorA) && int.TryParse(m.Groups[2].Value, out int shiftB) && int.TryParse(m.Groups[3].Value, out int geC) && int.TryParse(m.Groups[4].Value, out int xorD) && int.TryParse(m.Groups[5].Value, out int ltE))
                conditions.Add(new BranchCondition(BranchType.XorShiftRange, m.Index, Eval: r => ((r ^ xorA) >> shiftB) >= geC && (r ^ xorD) < ltE));
        
        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}-(\d+)&(\d+)\)\s*==\s*(\d+)")) 
            if (int.TryParse(m.Groups[1].Value, out int sub) && int.TryParse(m.Groups[2].Value, out int mask) && int.TryParse(m.Groups[3].Value, out int expected)) 
                conditions.Add(new BranchCondition(BranchType.ModEquals, m.Index, Mask: mask, ExpectedValue: expected, AddValue: sub));
        
        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}-(\d+)\^(\d+)\)>={p1}&&{p1}-(\d+)<<(\d+)<{p1}"))
            if (int.TryParse(m.Groups[1].Value, out int sub1) && int.TryParse(m.Groups[2].Value, out int xor) && int.TryParse(m.Groups[3].Value, out int sub2) && int.TryParse(m.Groups[4].Value, out int shift))
                conditions.Add(new BranchCondition(BranchType.XorShiftRange, m.Index, Eval: r => ((r - sub1) ^ xor) >= r && ((r - sub2) << shift) < r));
        
        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}\+(\d+)&(\d+)\)<{p1}&&\({p1}-(\d+)\|(\d+)\)>={p1}"))
            if (int.TryParse(m.Groups[1].Value, out int add) && int.TryParse(m.Groups[2].Value, out int andVal) && int.TryParse(m.Groups[3].Value, out int sub) && int.TryParse(m.Groups[4].Value, out int orVal))
                conditions.Add(new BranchCondition(BranchType.XorShiftRange, m.Index, Eval: r => ((r + add) & andVal) < r && ((r - sub) | orVal) >= r));

        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}\|(\d+)\)>=(\d+)&&{p1}\+(\d+)<(\d+)"))
            if (int.TryParse(m.Groups[1].Value, out int orVal) && int.TryParse(m.Groups[2].Value, out int geVal) && int.TryParse(m.Groups[3].Value, out int addVal) && int.TryParse(m.Groups[4].Value, out int ltVal))
                conditions.Add(new BranchCondition(BranchType.XorShiftRange, m.Index, Eval: r => (r | orVal) >= geVal && (r + addVal) < ltVal));

        foreach (Match m in Regex.Matches(funcDef, $@"\({p1}>>(\d+)&(\d+)\)\s*==\s*(\d+)"))
            if (int.TryParse(m.Groups[1].Value, out int shift) && int.TryParse(m.Groups[2].Value, out int mask) && int.TryParse(m.Groups[3].Value, out int expected))
                conditions.Add(new BranchCondition(BranchType.ShiftAndEquals, m.Index, Mask: mask, ShiftRight: shift, ExpectedValue: expected));

        foreach (Match m in Regex.Matches(funcDef, $@"!\({p1}-(\d+)&(\d+)\)"))
            if (int.TryParse(m.Groups[1].Value, out int sub) && int.TryParse(m.Groups[2].Value, out int mask))
                conditions.Add(new BranchCondition(BranchType.ModEquals, m.Index, Mask: mask, ExpectedValue: 0, AddValue: sub));
        
        return conditions;
    }

    private static string[]? LoadGlobalDictionary(string baseJs, string dictName)
    {
        var elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
        if (elements is not null && elements.Length > 50) return elements;

        var splitPattern = $@"var\s+{Regex.Escape(dictName)}\s*=\s*""[^""]+?""\s*\.\s*split\s*\(";
        if (Regex.IsMatch(baseJs, splitPattern))
        {
            elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (elements is not null && elements.Length > 50) return elements;
        }

        var bracketPattern = $@"var\s+{Regex.Escape(dictName)}\s*=\s*\[";
        if (Regex.IsMatch(baseJs, bracketPattern))
        {
            elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (elements is not null && elements.Length > 50) return elements;
        }
        return null;
    }
}