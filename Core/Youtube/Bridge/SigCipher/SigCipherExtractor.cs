using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.SigCipher;

internal static partial class SigCipherExtractor
{
    public static SigCipherManifest? ExtractManifest(string baseJs, string playerVersion)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var funcName = FindDecipherFunctionName(baseJs);
            if (funcName is null) return null;

            var decipherFunc = JsFunctionExtractor.FindFunctionByName(baseJs, funcName);
            if (decipherFunc is null) return null;

            Log.Debug($"[SigCipher] Found decipher function '{funcName}', length: {decipherFunc.Value.Code.Length}");

            var dictArrayName = JsFunctionExtractor.DetectDictArrayName(decipherFunc.Value.Code);
            string[]? dictElements = null;

            if (dictArrayName is not null)
            {
                dictElements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictArrayName);
                if (dictElements is not null)
                    Log.Debug($"[SigCipher] Dict array '{dictArrayName}': {dictElements.Length} elements");
            }
            else
            {
                Log.Warn("[SigCipher] Dict array name not detected in func body.");
            }

            var cipherObjName = FindCipherObjectNameFromCode(
                decipherFunc.Value.Code, dictArrayName);
            if (cipherObjName is null) return null;

            var cipherObjCode = JsFunctionExtractor.FindAnyDefinition(baseJs, cipherObjName);
            if (cipherObjCode is null) return null;

            Log.Debug($"[SigCipher] Found cipher object '{cipherObjName}'");

            var methodMap = ParseCipherMethods(cipherObjCode, dictElements);

            var operations = ExtractOperations(
                decipherFunc.Value.Code, cipherObjName,
                methodMap, dictElements, dictArrayName);

            if (operations is null || operations.Count < 3) return null;

            sw.Stop();
            var manifest = new SigCipherManifest(playerVersion, operations, "extracted");
            Log.Info($"[SigCipher] Extracted manifest in {sw.ElapsedMilliseconds}ms: {manifest}");

            return manifest;
        }
        catch (Exception ex)
        {
            Log.Error($"[SigCipher] Extraction failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Finds decipher function name by searching for:
    ///   = funcName ( 26 , decodeURIComponent
    /// Span-based backward scan, zero intermediate allocations.
    /// </summary>
    public static string? FindDecipherFunctionName(string baseJs)
    {
        var span = baseJs.AsSpan();
        ReadOnlySpan<char> marker = "decodeURIComponent";

        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int markerIdx = span[searchFrom..].IndexOf(marker, StringComparison.Ordinal);
            if (markerIdx < 0) return null;
            markerIdx += searchFrom;
            searchFrom = markerIdx + marker.Length;

            // Walk backwards: skip whitespace, expect ',', digits "26", '(', identifier, '='
            int pos = markerIdx - 1;
            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != ',') continue;
            pos--;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;

            // Read digits backwards
            int digitEnd = pos + 1;
            while (pos >= 0 && char.IsAsciiDigit(span[pos])) pos--;
            if (digitEnd - pos - 1 == 0) continue;

            var numSpan = span.Slice(pos + 1, digitEnd - pos - 1);
            if (!numSpan.SequenceEqual("26")) continue;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != '(') continue;
            pos--;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;

            // Read identifier backwards
            int identEnd = pos + 1;
            while (pos >= 0 && (char.IsLetterOrDigit(span[pos]) || span[pos] is '_' or '$'))
                pos--;
            int identStart = pos + 1;
            int identLen = identEnd - identStart;
            if (identLen == 0) continue;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != '=') continue;

            return span.Slice(identStart, identLen).ToString();
        }

        return null;
    }

    /// <summary>
    /// Finds cipher object name (e.g., TZ) from function code.
    /// Searches for pattern: ObjName[dictArrayName[digits]]
    /// </summary>
    private static string? FindCipherObjectNameFromCode(string funcCode, string? dictArrayName)
    {
        var paramNames = JsFunctionExtractor.ExtractParamNames(funcCode);
        var span = funcCode.AsSpan();

        if (dictArrayName is not null)
        {
            // Search: identifier[dictArrayName[digits]]
            var dictTarget = string.Concat(dictArrayName, "[");
            var dictTargetSpan = dictTarget.AsSpan();

            int searchFrom = 0;
            while (searchFrom < span.Length)
            {
                int dictIdx = span[searchFrom..].IndexOf(dictTargetSpan, StringComparison.Ordinal);
                if (dictIdx < 0) break;
                dictIdx += searchFrom;
                searchFrom = dictIdx + dictTargetSpan.Length;

                // dictArrayName must be preceded by '['
                int beforeDict = dictIdx - 1;
                while (beforeDict >= 0 && span[beforeDict] is ' ' or '\t') beforeDict--;
                if (beforeDict < 0 || span[beforeDict] != '[') continue;

                // Read object identifier backwards from '['
                int pos = beforeDict - 1;
                while (pos >= 0 && span[pos] is ' ' or '\t') pos--;

                int identEnd = pos + 1;
                while (pos >= 0 && (char.IsLetterOrDigit(span[pos]) || span[pos] is '_' or '$'))
                    pos--;
                int identStart = pos + 1;
                int identLen = identEnd - identStart;

                if (identLen > 0)
                {
                    var objName = span.Slice(identStart, identLen).ToString();
                    if (!paramNames.Contains(objName))
                        return objName;
                }
            }
        }

        // Fallback: find "objName.methodName(arg" pattern
        return FindDirectMethodCallObject(span, paramNames);
    }

    /// <summary>
    /// Fallback: scans for "identifier.identifier(" and returns first non-param identifier.
    /// </summary>
    private static string? FindDirectMethodCallObject(
        ReadOnlySpan<char> code, HashSet<string> paramNames)
    {
        int i = 0;
        while (i < code.Length)
        {
            if (!IsIdentStart(code[i])) { i++; continue; }

            int identStart = i;
            i++;
            while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '$'))
                i++;

            int identEnd = i;

            // Check for '.' followed by identifier and '('
            if (i >= code.Length || code[i] != '.') continue;

            int afterDot = i + 1;
            if (afterDot >= code.Length || !IsIdentStart(code[afterDot])) continue;

            int methodEnd = afterDot;
            while (methodEnd < code.Length &&
                   (char.IsLetterOrDigit(code[methodEnd]) || code[methodEnd] is '_' or '$'))
                methodEnd++;

            // Skip whitespace, check for '('
            int pos = methodEnd;
            while (pos < code.Length && code[pos] is ' ' or '\t') pos++;

            if (pos < code.Length && code[pos] == '(')
            {
                pos++;
                while (pos < code.Length && code[pos] is ' ' or '\t') pos++;

                if (pos < code.Length && (IsIdentStart(code[pos]) || char.IsAsciiDigit(code[pos])))
                {
                    var objName = code.Slice(identStart, identEnd - identStart).ToString();
                    if (!paramNames.Contains(objName))
                        return objName;
                }
            }

            // Continue scanning from after the dot-access
            i = methodEnd;
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentStart(char c) =>
        char.IsLetter(c) || c is '_' or '$';

    // ═══════════════════════════════════════════════════════════════
    // PARSE METHODS & EXTRACT OPERATIONS
    // ═══════════════════════════════════════════════════════════════

    [GeneratedRegex(@"(\w+)\s*:\s*function\s*\([^)]*\)\s*\{([^}]+)\}")]
    private static partial Regex MethodDefRegex();

    [GeneratedRegex(@"\w+\s*\[\s*\w+\s*\[\s*(\d+)\s*\]\s*\]\s*\(\s*\)")]
    private static partial Regex NoArgDictCallRegex();

    [GeneratedRegex(@"\w+\s*\[\s*\w+\s*\[\s*(\d+)\s*\]\s*\]\s*\(\s*0\s*,")]
    private static partial Regex TwoArgDictCallRegex();

    [GeneratedRegex(@"^\s*\w+\s*\[\s*\w+\s*\[\s*\d+\s*\]\s*\]\s*\(\s*\)\s*;?\s*$")]
    private static partial Regex SingleNoArgCallRegex();

    private static Dictionary<string, SigCipherOpType> ParseCipherMethods(
        string objCode, string[]? dictArray)
    {
        var result = new Dictionary<string, SigCipherOpType>();

        foreach (Match m in MethodDefRegex().Matches(objCode))
        {
            var methodName = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            var bodySpan = body.AsSpan();

            SigCipherOpType? opType = null;

            if (bodySpan.Contains(".reverse()", StringComparison.Ordinal) ||
                bodySpan.Contains("[1]()", StringComparison.Ordinal))
            {
                opType = SigCipherOpType.Reverse;
            }
            else if (bodySpan.Contains(".splice(0,", StringComparison.Ordinal) ||
                     bodySpan.Contains("[20](0,", StringComparison.Ordinal))
            {
                opType = SigCipherOpType.Splice;
            }
            else if (bodySpan.Contains("[0]", StringComparison.Ordinal) &&
                     bodySpan.Contains("%", StringComparison.Ordinal))
            {
                opType = SigCipherOpType.Swap;
            }
            else if (dictArray is not null)
            {
                var noArgMatch = NoArgDictCallRegex().Match(body);
                if (noArgMatch.Success &&
                    int.TryParse(noArgMatch.Groups[1].ValueSpan, out int idx) &&
                    idx < dictArray.Length)
                {
                    if (dictArray[idx] == "reverse")
                        opType = SigCipherOpType.Reverse;
                }

                var twoArgMatch = TwoArgDictCallRegex().Match(body);
                if (twoArgMatch.Success &&
                    int.TryParse(twoArgMatch.Groups[1].ValueSpan, out idx) &&
                    idx < dictArray.Length)
                {
                    if (dictArray[idx] == "splice")
                        opType = SigCipherOpType.Splice;
                }
            }

            if (opType is null)
            {
                if (SingleNoArgCallRegex().IsMatch(body))
                    opType = SigCipherOpType.Reverse;
                else if (bodySpan.Trim().Contains("(0,", StringComparison.Ordinal) &&
                         !bodySpan.Trim().Contains("[0]", StringComparison.Ordinal))
                    opType = SigCipherOpType.Splice;
            }

            if (opType.HasValue) result[methodName] = opType.Value;
        }

        return result;
    }

    private static List<SigCipherOperation>? ExtractOperations(
        string funcCode, string cipherObjName,
        Dictionary<string, SigCipherOpType> methodMap,
        string[]? dictArray, string? dictArrayName)
    {
        var ops = ExtractDirectCalls(funcCode, cipherObjName, methodMap);
        if (ops.Count >= 3) return ops;

        if (dictArray is null || dictArrayName is null)
            return ops.Count > 0 ? ops : null;

        ops = ExtractArrayCalls(funcCode, cipherObjName, dictArrayName, dictArray, methodMap);
        return ops.Count >= 3 ? ops : null;
    }

    /// <summary>
    /// Extracts direct method calls: cipherObj.methodName(arg, param)
    /// Span-based scanning, no Regex.
    /// </summary>
    private static List<SigCipherOperation> ExtractDirectCalls(
        string code, string cipherObjName,
        Dictionary<string, SigCipherOpType> methodMap)
    {
        var ops = new List<SigCipherOperation>();
        var span = code.AsSpan();
        var objPrefix = string.Concat(cipherObjName, ".");
        var objPrefixSpan = objPrefix.AsSpan();

        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int idx = span[searchFrom..].IndexOf(objPrefixSpan, StringComparison.Ordinal);
            if (idx < 0) break;
            idx += searchFrom;
            searchFrom = idx + objPrefixSpan.Length;

            // Word boundary before obj name
            if (idx > 0 && (char.IsLetterOrDigit(span[idx - 1]) || span[idx - 1] is '_' or '$'))
                continue;

            // Read method name
            int methodStart = idx + objPrefixSpan.Length;
            int methodEnd = methodStart;
            while (methodEnd < span.Length &&
                   (char.IsLetterOrDigit(span[methodEnd]) || span[methodEnd] is '_' or '$'))
                methodEnd++;

            if (methodEnd == methodStart) continue;

            var methodNameSpan = span[methodStart..methodEnd];

            // Skip whitespace, expect '('
            int pos = methodEnd;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != '(') continue;
            pos++;

            // Skip first arg (identifier)
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            while (pos < span.Length &&
                   (char.IsLetterOrDigit(span[pos]) || span[pos] is '_' or '$'))
                pos++;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

            int param = 0;
            if (pos < span.Length && span[pos] == ',')
            {
                pos++;
                while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

                int numStart = pos;
                while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
                if (pos > numStart)
                    int.TryParse(span.Slice(numStart, pos - numStart), out param);
            }

            var methodName = methodNameSpan.ToString();
            if (methodMap.TryGetValue(methodName, out var opType))
                ops.Add(new SigCipherOperation(opType, param));
        }

        return ops;
    }

    /// <summary>
    /// Extracts array-indexed calls: cipherObj[dictArray[idx]](arg, param)
    /// Span-based scanning, no Regex.
    /// </summary>
    private static List<SigCipherOperation> ExtractArrayCalls(
        string code, string cipherObjName, string dictArrayName,
        string[] dictArray, Dictionary<string, SigCipherOpType> methodMap)
    {
        var ops = new List<SigCipherOperation>();
        var span = code.AsSpan();
        var target = string.Concat(cipherObjName, "[");
        var targetSpan = target.AsSpan();
        var dictNameSpan = dictArrayName.AsSpan();

        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int idx = span[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (idx < 0) break;
            idx += searchFrom;
            searchFrom = idx + targetSpan.Length;

            // Word boundary
            if (idx > 0 && (char.IsLetterOrDigit(span[idx - 1]) || span[idx - 1] is '_' or '$'))
                continue;

            int pos = idx + targetSpan.Length;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

            // Expect dictArrayName
            if (pos + dictNameSpan.Length > span.Length) continue;
            if (!span.Slice(pos, dictNameSpan.Length).SequenceEqual(dictNameSpan)) continue;
            pos += dictNameSpan.Length;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != '[') continue;
            pos++;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

            // Read array index
            int numStart = pos;
            while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
            if (pos == numStart) continue;
            if (!int.TryParse(span.Slice(numStart, pos - numStart), out int arrayIdx)) continue;

            // Expect ]]
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != ']') continue;
            pos++;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != ']') continue;
            pos++;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != '(') continue;
            pos++;

            // Skip first argument
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            while (pos < span.Length &&
                   (char.IsLetterOrDigit(span[pos]) || span[pos] is '_' or '$'))
                pos++;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

            int param = 0;
            if (pos < span.Length && span[pos] == ',')
            {
                pos++;
                while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

                int paramStart = pos;
                while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
                if (pos > paramStart)
                    int.TryParse(span.Slice(paramStart, pos - paramStart), out param);
            }

            if (arrayIdx < dictArray.Length)
            {
                var methodName = dictArray[arrayIdx];
                if (methodMap.TryGetValue(methodName, out var opType))
                    ops.Add(new SigCipherOperation(opType, param));
            }
        }

        return ops;
    }
}