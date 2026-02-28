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

            // ═══ Resolve dictionary references ═══
            // Detect dict name (e.g., "q") and resolve all q[N] → actual values
            string funcCode = decipherFunc.Value.Code;
            string? dictArrayName = null;
            string[]? dictElements = null;

            var detectedDictName = JsDictResolver.DetectDictName(funcCode);
            if (detectedDictName is not null)
            {
                dictElements = JsFunctionExtractor.ExtractArrayElements(baseJs, detectedDictName);
                if (dictElements is not null && dictElements.Length >= 10)
                {
                    dictArrayName = detectedDictName;
                    funcCode = JsDictResolver.Resolve(funcCode, detectedDictName, dictElements);
                    Log.Debug($"[SigCipher] Resolved dict '{detectedDictName}' ({dictElements.Length} elements) in decipher func");
                }
            }

            // Fallback: original dict detection
            if (dictArrayName is null)
            {
                dictArrayName = JsFunctionExtractor.DetectDictArrayName(funcCode);
                if (dictArrayName is not null)
                {
                    dictElements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictArrayName);
                    if (dictElements is not null)
                        Log.Debug($"[SigCipher] Dict array '{dictArrayName}': {dictElements.Length} elements");
                }
            }

            // ═══ Find cipher object ═══
            var cipherObjName = FindCipherObjectNameFromCode(funcCode, dictArrayName);
            if (cipherObjName is null)
            {
                Log.Warn("[SigCipher] Could not find cipher object name");
                return null;
            }

            var cipherObjCode = JsFunctionExtractor.FindAnyDefinition(baseJs, cipherObjName);
            if (cipherObjCode is null)
            {
                Log.Warn($"[SigCipher] Could not find cipher object '{cipherObjName}' definition");
                return null;
            }

            Log.Debug($"[SigCipher] Found cipher object '{cipherObjName}'");

            // ═══ Resolve dict in cipher object too ═══
            string resolvedCipherCode = cipherObjCode;
            if (dictElements is not null && detectedDictName is not null)
            {
                resolvedCipherCode = JsDictResolver.Resolve(cipherObjCode, detectedDictName, dictElements);
                Log.Debug($"[SigCipher] Resolved dict in cipher object '{cipherObjName}'");
            }

            // ═══ Parse methods from resolved cipher object ═══
            var methodMap = ParseCipherMethods(resolvedCipherCode, dictElements);

            if (methodMap.Count == 0)
            {
                Log.Warn("[SigCipher] No cipher methods parsed");
                return null;
            }

            // ═══ Extract operations from resolved decipher function ═══
            var operations = ExtractOperations(
                funcCode, cipherObjName,
                methodMap, dictElements, dictArrayName);

            if (operations is null || operations.Count < 3)
            {
                Log.Warn($"[SigCipher] Too few operations: {operations?.Count ?? 0}");
                return null;
            }

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

    // ═══════════════════════════════════════════════════════════════
    // FIND DECIPHER FUNCTION NAME — multi-strategy
    // ═══════════════════════════════════════════════════════════════

    public static string? FindDecipherFunctionName(string baseJs)
    {
        // Strategy 1: = funcName(N, decodeURIComponent(...)  — any number
        var result = FindByDecodeURIComponentWithNumber(baseJs);
        if (result is not null)
        {
            Log.Debug($"[SigCipher] Found decipher func '{result}' via Strategy 1 (number+decodeURI)");
            return result;
        }

        // Strategy 2: = funcName(decodeURIComponent(...)  — no number prefix
        result = FindByDecodeURIComponentDirect(baseJs);
        if (result is not null)
        {
            Log.Debug($"[SigCipher] Found decipher func '{result}' via Strategy 2 (direct decodeURI)");
            return result;
        }

        // Strategy 3: Regex-based patterns
        result = FindByRegexPatterns(baseJs);
        if (result is not null)
        {
            Log.Debug($"[SigCipher] Found decipher func '{result}' via Strategy 3 (regex)");
            return result;
        }

        Log.Warn("[SigCipher] Could not find decipher function name");
        return null;
    }

    private static string? FindByDecodeURIComponentWithNumber(string baseJs)
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

            int pos = markerIdx - 1;
            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != ',') continue;
            pos--;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;

            int digitEnd = pos + 1;
            while (pos >= 0 && char.IsAsciiDigit(span[pos])) pos--;
            int digitCount = digitEnd - pos - 1;
            if (digitCount == 0) continue;

            var numSpan = span.Slice(pos + 1, digitCount);
            if (!int.TryParse(numSpan, out int num) || num < 1 || num > 999) continue;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != '(') continue;
            pos--;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;

            int identEnd = pos + 1;
            while (pos >= 0 && (char.IsLetterOrDigit(span[pos]) || span[pos] is '_' or '$'))
                pos--;
            int identStart = pos + 1;
            int identLen = identEnd - identStart;
            if (identLen == 0) continue;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != '=') continue;
            if (pos > 0 && span[pos - 1] == '=') continue;

            var funcName = span.Slice(identStart, identLen).ToString();

            if (ValidateDecipherCandidate(baseJs, funcName))
                return funcName;
        }

        return null;
    }

    private static string? FindByDecodeURIComponentDirect(string baseJs)
    {
        var span = baseJs.AsSpan();
        ReadOnlySpan<char> marker = "decodeURIComponent(";

        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int markerIdx = span[searchFrom..].IndexOf(marker, StringComparison.Ordinal);
            if (markerIdx < 0) return null;
            markerIdx += searchFrom;
            searchFrom = markerIdx + marker.Length;

            int pos = markerIdx - 1;
            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != '(') continue;
            pos--;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;

            int identEnd = pos + 1;
            while (pos >= 0 && (char.IsLetterOrDigit(span[pos]) || span[pos] is '_' or '$'))
                pos--;
            int identStart = pos + 1;
            int identLen = identEnd - identStart;
            if (identLen == 0) continue;

            var funcName = span.Slice(identStart, identLen).ToString();
            if (funcName is "encodeURIComponent" or "decodeURIComponent" or "decodeURI" or "encodeURI")
                continue;

            while (pos >= 0 && span[pos] is ' ' or '\t') pos--;
            if (pos < 0 || span[pos] != '=') continue;
            if (pos > 0 && span[pos - 1] == '=') continue;

            if (ValidateDecipherCandidate(baseJs, funcName))
                return funcName;
        }

        return null;
    }

    private static string? FindByRegexPatterns(string baseJs)
    {
        foreach (Match m in SplitJoinFunctionRegex().Matches(baseJs))
        {
            if (ValidateDecipherCandidate(baseJs, m.Groups[1].Value))
                return m.Groups[1].Value;
        }

        foreach (Match m in DecipherCallRegex().Matches(baseJs))
        {
            if (ValidateDecipherCandidate(baseJs, m.Groups[1].Value))
                return m.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Validates candidate by checking if its body (with dict resolved) contains
    /// split("") and join("") — the hallmarks of a signature decipher function.
    /// </summary>
    private static bool ValidateDecipherCandidate(string baseJs, string funcName)
    {
        var funcInfo = JsFunctionExtractor.FindFunctionByName(baseJs, funcName);
        if (funcInfo is null) return false;

        var code = funcInfo.Value.Code;
        if (code.Length < 30) return false;

        // Try resolving dict references first
        var dictName = JsDictResolver.DetectDictName(code);
        if (dictName is not null)
        {
            var elements = JsFunctionExtractor.ExtractArrayElements(baseJs, dictName);
            if (elements is not null && elements.Length >= 10)
                code = JsDictResolver.Resolve(code, dictName, elements);
        }

        var codeSpan = code.AsSpan();

        bool hasSplit = codeSpan.Contains(".split(", StringComparison.Ordinal) ||
                        codeSpan.Contains("[\"split\"]", StringComparison.Ordinal);

        bool hasJoin = codeSpan.Contains(".join(", StringComparison.Ordinal) ||
                       codeSpan.Contains("[\"join\"]", StringComparison.Ordinal);

        if (!hasSplit)
        {
            Log.Debug($"[SigCipher] Candidate '{funcName}' rejected: no split");
            return false;
        }

        // join is optional (some functions return the array and caller joins)
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // CIPHER OBJECT & OPERATIONS
    // ═══════════════════════════════════════════════════════════════

    private static string? FindCipherObjectNameFromCode(string funcCode, string? dictArrayName)
    {
        var paramNames = JsFunctionExtractor.ExtractParamNames(funcCode);
        var span = funcCode.AsSpan();

        // After dict resolution, cipher calls look like: zu.Rb(N, 35)
        // So direct method call detection works
        var directObj = FindDirectMethodCallObject(span, paramNames);
        if (directObj is not null) return directObj;

        // Fallback: dict-indexed pattern (unresoled code)
        if (dictArrayName is not null)
        {
            var dictTarget = string.Concat(dictArrayName, "[");
            var dictTargetSpan = dictTarget.AsSpan();

            int searchFrom = 0;
            while (searchFrom < span.Length)
            {
                int dictIdx = span[searchFrom..].IndexOf(dictTargetSpan, StringComparison.Ordinal);
                if (dictIdx < 0) break;
                dictIdx += searchFrom;
                searchFrom = dictIdx + dictTargetSpan.Length;

                int beforeDict = dictIdx - 1;
                while (beforeDict >= 0 && span[beforeDict] is ' ' or '\t') beforeDict--;
                if (beforeDict < 0 || span[beforeDict] != '[') continue;

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
                    if (!paramNames.Contains(objName) && objName != dictArrayName)
                        return objName;
                }
            }
        }

        return null;
    }

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

            if (i >= code.Length || code[i] != '.') continue;

            int afterDot = i + 1;
            if (afterDot >= code.Length || !IsIdentStart(code[afterDot])) continue;

            int methodEnd = afterDot;
            while (methodEnd < code.Length &&
                   (char.IsLetterOrDigit(code[methodEnd]) || code[methodEnd] is '_' or '$'))
                methodEnd++;

            int pos = methodEnd;
            while (pos < code.Length && code[pos] is ' ' or '\t') pos++;

            if (pos < code.Length && code[pos] == '(')
            {
                pos++;
                while (pos < code.Length && code[pos] is ' ' or '\t') pos++;

                if (pos < code.Length && (IsIdentStart(code[pos]) || char.IsAsciiDigit(code[pos])))
                {
                    var objName = code[identStart..identEnd].ToString();
                    if (!paramNames.Contains(objName))
                        return objName;
                }
            }

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

    [GeneratedRegex(@"([a-zA-Z_$][\w$]{0,3})=function\(\w+\)\{\w+=\w+\.split\(""""\)")]
    private static partial Regex SplitJoinFunctionRegex();

    [GeneratedRegex(@"=\s*([a-zA-Z_$][\w$]{0,5})\s*\(\s*\d+\s*,\s*decodeURIComponent\s*\(")]
    private static partial Regex DecipherCallRegex();

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

            // After dict resolution, standard patterns work directly:
            //   r.reverse()       → Reverse
            //   r.splice(0, n)    → Splice  
            //   r[0] ... % ...    → Swap

            if (bodySpan.Contains(".reverse()", StringComparison.Ordinal) ||
                bodySpan.Contains("[\"reverse\"]()", StringComparison.Ordinal))
            {
                opType = SigCipherOpType.Reverse;
            }
            else if (bodySpan.Contains(".splice(0,", StringComparison.Ordinal) ||
                     bodySpan.Contains("[\"splice\"](0,", StringComparison.Ordinal))
            {
                opType = SigCipherOpType.Splice;
            }
            else if (bodySpan.Contains("[0]", StringComparison.Ordinal) &&
                     bodySpan.Contains("%", StringComparison.Ordinal))
            {
                opType = SigCipherOpType.Swap;
            }

            // Fallback for unresolved dict patterns
            if (opType is null && dictArray is not null)
            {
                opType = InferOpTypeFromDictCalls(body, dictArray);
            }

            if (opType is null)
            {
                // Structural fallback: single no-arg call → reverse
                if (body.Trim().Length < 80 &&
                    !bodySpan.Contains(",", StringComparison.Ordinal) &&
                    bodySpan.Contains("()", StringComparison.Ordinal))
                    opType = SigCipherOpType.Reverse;
                else if (bodySpan.Trim().Contains("(0,", StringComparison.Ordinal) &&
                         !bodySpan.Trim().Contains("[0]", StringComparison.Ordinal))
                    opType = SigCipherOpType.Splice;
            }

            if (opType.HasValue)
            {
                result[methodName] = opType.Value;
                Log.Debug($"[SigCipher] Method '{methodName}' → {opType.Value}");
            }
            else
            {
                Log.Debug($"[SigCipher] Method '{methodName}' → UNKNOWN, body: {body.Trim()}");
            }
        }

        return result;
    }

    private static SigCipherOpType? InferOpTypeFromDictCalls(string body, string[] dictArray)
    {
        var span = body.AsSpan();
        int pos = 0;

        while (pos < span.Length)
        {
            int bracketIdx = span[pos..].IndexOf('[');
            if (bracketIdx < 0) break;
            bracketIdx += pos;
            pos = bracketIdx + 1;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            int identStart = pos;
            while (pos < span.Length && (char.IsLetterOrDigit(span[pos]) || span[pos] is '_' or '$'))
                pos++;
            if (pos == identStart) continue;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != '[') continue;
            pos++;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            int numStart = pos;
            while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
            if (pos == numStart) continue;

            if (!int.TryParse(span[numStart..pos], out int idx)) continue;
            if (idx < 0 || idx >= dictArray.Length) continue;

            var resolved = dictArray[idx];
            if (resolved == "reverse") return SigCipherOpType.Reverse;
            if (resolved == "splice") return SigCipherOpType.Splice;
        }

        if (span.Contains("[0]", StringComparison.Ordinal) &&
            span.Contains("%", StringComparison.Ordinal))
            return SigCipherOpType.Swap;

        return null;
    }

    private static List<SigCipherOperation>? ExtractOperations(
        string funcCode, string cipherObjName,
        Dictionary<string, SigCipherOpType> methodMap,
        string[]? dictArray, string? dictArrayName)
    {
        // After dict resolution, direct calls (zu.Rb) should work
        var ops = ExtractDirectCalls(funcCode, cipherObjName, methodMap);
        if (ops.Count >= 3) return ops;

        // Fallback for unresolved dict-indexed calls
        if (dictArray is not null && dictArrayName is not null)
        {
            ops = ExtractArrayCalls(funcCode, cipherObjName, dictArrayName, dictArray, methodMap);
            if (ops.Count >= 3) return ops;
        }

        return ops.Count > 0 ? ops : null;
    }

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

            if (idx > 0 && (char.IsLetterOrDigit(span[idx - 1]) || span[idx - 1] is '_' or '$'))
                continue;

            int methodStart = idx + objPrefixSpan.Length;
            int methodEnd = methodStart;
            while (methodEnd < span.Length &&
                   (char.IsLetterOrDigit(span[methodEnd]) || span[methodEnd] is '_' or '$'))
                methodEnd++;

            if (methodEnd == methodStart) continue;

            int pos = methodEnd;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != '(') continue;
            pos++;

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
                    int.TryParse(span[numStart..pos], out param);
            }

            var methodName = span[methodStart..methodEnd].ToString();
            if (methodMap.TryGetValue(methodName, out var opType))
                ops.Add(new SigCipherOperation(opType, param));
        }

        return ops;
    }

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

            if (idx > 0 && (char.IsLetterOrDigit(span[idx - 1]) || span[idx - 1] is '_' or '$'))
                continue;

            int pos = idx + targetSpan.Length;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;

            if (pos + dictNameSpan.Length > span.Length) continue;
            if (!span.Slice(pos, dictNameSpan.Length).SequenceEqual(dictNameSpan)) continue;
            pos += dictNameSpan.Length;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != '[') continue;
            pos++;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            int numStart = pos;
            while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
            if (pos == numStart) continue;
            if (!int.TryParse(span[numStart..pos], out int arrayIdx)) continue;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != ']') continue;
            pos++;
            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != ']') continue;
            pos++;

            while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
            if (pos >= span.Length || span[pos] != '(') continue;
            pos++;

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
                    int.TryParse(span[paramStart..pos], out param);
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