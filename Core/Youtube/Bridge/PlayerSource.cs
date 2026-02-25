using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge.Cipher;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlayerSource(string content)
{
    /// <summary>
    /// Кеш для скомпилированных Regex, используемых в GetMethodName.
    /// Ключ - ключевое слово (keyword), значение - скомпилированный Regex.
    /// </summary>
    private static readonly Dictionary<string, Regex> s_methodNameRegexCache = new();

    /// <summary>
    /// Кеш для скомпилированных Regex, используемых в ExtractCipherOperations.
    /// Ключ - имя объекта (objName), значение - скомпилированный Regex.
    /// </summary>
    private static readonly Dictionary<string, Regex> s_objDefRegexCache = new();

    /// <summary>
    /// Кеш для скомпилированных Regex, используемых в ExtractCipherOperations для поиска вызовов.
    /// Ключ - имя объекта (objName), значение - скомпилированный Regex.
    /// </summary>
    private static readonly Dictionary<string, Regex> s_opCallsRegexCache = new();
    /// <summary>
    /// Legacy CipherManifest — извлекает SignatureTimestamp и (если получится) операции.
    /// Сама дешифровка sig теперь идёт через SigCipherDecryptor.
    /// </summary>
    public CipherManifest? CipherManifest
    {
        get
        {
            try
            {
                // 1. SignatureTimestamp (sts) — нужен для fallback-запроса
                var stsMatch = SignatureTimestampRegex().Match(content);
                if (!stsMatch.Success)
                {
                    Log.Debug("[PlayerSource] SignatureTimestamp not found");
                    return null;
                }

                var signatureTimestamp = stsMatch.Groups[1].Value;

                // 2. Пробуем извлечь операции (для обратной совместимости)
                // Если не получится — не критично, SigCipherDecryptor справится
                var operations = ExtractCipherOperations();

                if (operations is null || operations.Count == 0)
                {
                    Log.Debug("[PlayerSource] Legacy cipher operations not found (expected with new YouTube format)");
                    // Возвращаем манифест только с sts, без операций
                    return new CipherManifest(signatureTimestamp, []);
                }

                return new CipherManifest(signatureTimestamp, operations);
            }
            catch (Exception ex)
            {
                Log.Debug($"[PlayerSource] CipherManifest extraction failed: {ex.Message}");
                return null;
            }
        }
    }

    private List<ICipherOperation>? ExtractCipherOperations()
    {
        // 1. Находим функцию расшифровки: a=a.split("") ... a.join("")
        var decipherFuncBodyMatch = DeciphererFunctionRegex().Match(content);
        if (!decipherFuncBodyMatch.Success)
        {
            Log.Debug("[PlayerSource] Decipher function definition not found");
            return null;
        }

        var decipherFuncBody = decipherFuncBodyMatch.Groups[2].Value;

        // 2. Находим имя объекта-манипулятора (например "AB" в "AB.xy(a,1)")
        var operationCallMatch = OperationCallRegex().Match(decipherFuncBody);
        if (!operationCallMatch.Success)
        {
            Log.Debug("[PlayerSource] No operation calls found in decipher function");
            return null;
        }

        var objName = operationCallMatch.Groups[1].Value;

        // 3. Находим определение объекта в JS: var AB={...};
        if (!s_objDefRegexCache.TryGetValue(objName, out var objDefRegex))
        {
            objDefRegex = new Regex(
                $@"var\s+{Regex.Escape(objName)}\s*=\s*\{{([\s\S]+?)\}};",
                RegexOptions.Singleline | RegexOptions.Compiled);
            s_objDefRegexCache[objName] = objDefRegex;
        }

        var objDefMatch = objDefRegex.Match(content);
        if (!objDefMatch.Success)
        {
            Log.Debug($"[PlayerSource] Definition for object '{objName}' not found");
            return null;
        }

        var objBody = objDefMatch.Groups[1].Value;

        // 4. Определяем маппинг: имя метода → тип операции
        var reverseMethod = GetMethodName(objBody, "reverse");
        var spliceMethod = GetMethodName(objBody, "splice");
        var swapMethod = GetMethodName(objBody, "var", "slice");

        if (string.IsNullOrEmpty(reverseMethod) &&
            string.IsNullOrEmpty(spliceMethod) &&
            string.IsNullOrEmpty(swapMethod))
        {
            Log.Debug("[PlayerSource] Could not identify cipher methods mapping");
            return null;
        }

        Log.Debug($"[PlayerSource] Methods found - " +
                  $"Reverse: {reverseMethod}, Splice: {spliceMethod}, Swap: {swapMethod}");

        // 5. Парсим вызовы из тела функции и создаём операции
        var operations = new List<ICipherOperation>();

        if (!s_opCallsRegexCache.TryGetValue(objName, out var callsRegex))
        {
            callsRegex = new Regex(
                $@"{Regex.Escape(objName)}\.([a-zA-Z0-9_$]+)\(a,(\d+)\)",
                RegexOptions.Compiled);
            s_opCallsRegexCache[objName] = callsRegex;
        }
        var calls = callsRegex.Matches(decipherFuncBody);

        foreach (Match call in calls)
        {
            var methodName = call.Groups[1].Value;
            var param = int.Parse(call.Groups[2].Value);

            if (methodName == reverseMethod)
                operations.Add(new ReverseCipherOperation());
            else if (methodName == swapMethod)
                operations.Add(new SwapCipherOperation(param));
            else if (methodName == spliceMethod)
                operations.Add(new SpliceCipherOperation(param));
            else
                Log.Debug($"[PlayerSource] Unknown cipher operation: {methodName}");
        }

        return operations;
    }

    private static string? GetMethodName(string objBody, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (!s_methodNameRegexCache.TryGetValue(keyword, out var regex))
            {
                regex = new Regex(
                    @"([a-zA-Z0-9_$]+)\s*:\s*function\b[^}]*" + Regex.Escape(keyword),
                    RegexOptions.Singleline | RegexOptions.Compiled);
                s_methodNameRegexCache[keyword] = regex;
            }

            var match = regex.Match(objBody);
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }

    [GeneratedRegex(@"(?:signatureTimestamp|sts)\s*[:=]\s*(\d+)")]
    private static partial Regex SignatureTimestampRegex();

    // Ищет: function_name = function(a) { a=a.split(""); ... return a.join("") }
    [GeneratedRegex(
        @"([a-zA-Z0-9_$]+)\s*=\s*function\([a-zA-Z0-9_$]+\)\s*\{\s*[a-zA-Z0-9_$]+\s*=\s*[a-zA-Z0-9_$]+\.split\(""""\);\s*([\s\S]+?)\s*;?\s*return\s*[a-zA-Z0-9_$]+\.join\(""""\)",
        RegexOptions.Singleline)]
    private static partial Regex DeciphererFunctionRegex();

    // Ищет вызовы: OBJ.METHOD(a, 123)
    [GeneratedRegex(
        @"([a-zA-Z0-9_$]+)\.[a-zA-Z0-9_$]+\([a-zA-Z0-9_$]+,\d+\)",
        RegexOptions.None)]
    private static partial Regex OperationCallRegex();
}

internal partial class PlayerSource
{
    public static PlayerSource Parse(string raw) => new(raw);
}