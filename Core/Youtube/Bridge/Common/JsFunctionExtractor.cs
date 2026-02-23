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
        "setPrototypeOf", "getPrototypeOf"
    ]);

    [ThreadStatic]
    private static StringBuilder? t_concatBuilder;

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════

    public static string? ExtractBundle(string fullJs, string entryFuncName)
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

            var dictName = DetectDictArrayName(entryDef);
            string? dictArrayCode = null;

            if (dictName is not null)
            {
                dictArrayCode = FindDictArrayDefinition(fullJs, dictName);
                if (dictArrayCode is not null)
                    Log.Debug($"[JsExtractor] Dict array '{dictName}': {dictArrayCode.Length} chars");
                else
                    Log.Warn($"[JsExtractor] Dict array '{dictName}' not found");
            }

            var definitions = new Dictionary<string, string>(64);
            var visited = new HashSet<string>(SkipNames);
            var notFound = new HashSet<string>();

            if (dictName is not null) visited.Add(dictName);

            var queue = new Queue<string>();
            queue.Enqueue(entryFuncName);

            int iterations = 0;
            const int maxIterations = 200;

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

                // Trim trailing ; , whitespace via span, then materialize once
                var cleanSpan = def.AsSpan().TrimEnd([';', ',', ' ', '\n', '\r']);
                definitions[currentName] = ConcatSpans(
                    "var ".AsSpan(),
                    currentName.AsSpan(),
                    "=".AsSpan(),
                    cleanSpan,
                    ";".AsSpan());
            }

            // Redo the above properly — string.Concat only has overloads up to 3 ReadOnlySpan<char>.
            // Let's fix by using the 3-arg overload plus a small helper:
            // Actually the simplest correct approach: interpolated string with .ToString()
            // But that allocates the span. Let's just use StringBuilder append for this one spot.
            // 
            // CORRECTION: The loop above needs to be fixed. Let me redo it inline:

            // RE-COLLECT definitions with correct string building:
            definitions.Clear();
            visited = new HashSet<string>(SkipNames);
            if (dictName is not null) visited.Add(dictName);
            queue.Clear();
            queue.Enqueue(entryFuncName);
            iterations = 0;

            // Reusable StringBuilder for definition building (avoids per-iteration alloc)
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

                var cleanSpan = def.AsSpan().TrimEnd([';', ',', ' ', '\n', '\r']);

                defBuilder.Clear();
                defBuilder.Append("var ");
                defBuilder.Append(currentName);
                defBuilder.Append('=');
                defBuilder.Append(cleanSpan);
                defBuilder.Append(';');
                definitions[currentName] = defBuilder.ToString();

                foreach (var dep in FindReferencedNames(def))
                {
                    if (!visited.Contains(dep))
                        queue.Enqueue(dep);
                }
            }

            if (definitions.Count < 3)
            {
                Log.Warn($"[JsExtractor] Too few definitions ({definitions.Count}), but building bundle for debug...");
            }

            var guardVars = FindTypeofGuardVars(entryDef, definitions, dictName);
            foreach (var def in definitions.Values)
            {
                foreach (var guardVar in FindTypeofGuardVars(def, definitions, dictName))
                    guardVars.Add(guardVar);
            }

            if (guardVars.Count > 0)
                Log.Debug($"[JsExtractor] Guard vars (typeof === 'undefined'): {string.Join(", ", guardVars)}");

            // Build the final bundle with pre-calculated capacity
            var totalSize = (dictArrayCode?.Length ?? 0)
                          + definitions.Values.Sum(static d => d.Length)
                          + guardVars.Count * 16
                          + 1024;

            var sb = new StringBuilder(totalSize);

            if (dictArrayCode is not null) sb.AppendLine(dictArrayCode);

            foreach (var guardVar in guardVars)
                sb.Append("var ").Append(guardVar).AppendLine("=0;");

            foreach (var kv in definitions)
            {
                if (string.Equals(kv.Key, entryFuncName, StringComparison.Ordinal))
                    continue;
                sb.Append("try{").Append(kv.Value).AppendLine("}catch(e){}");
            }

            if (definitions.TryGetValue(entryFuncName, out var entryCode))
                sb.AppendLine(entryCode);

            sb.Append("window['").Append(entryFuncName).Append("']=").Append(entryFuncName).AppendLine(";");

            sw.Stop();
            var result = sb.ToString();
            var defCount = definitions.Count + (dictArrayCode is not null ? 1 : 0);
            var reduction = fullJs.Length > 0 ? 100 - result.Length * 100 / fullJs.Length : 0;

            Log.Info($"[JsExtractor] Extracted {defCount} definitions, " +
                     $"{fullJs.Length / 1024}KB -> {result.Length / 1024}KB " +
                     $"({reduction}% reduction) in {sw.ElapsedMilliseconds}ms");

            if (notFound.Count > 0)
                Log.Debug($"[JsExtractor] Not found ({notFound.Count}): {string.Join(", ", notFound.Take(20))}");

            return result;
        }
        catch (Exception ex)
        {
            Log.Debug($"[JsExtractor] Extraction failed: {ex.Message}");
            return null;
        }
    }

    public static string[]? ExtractArrayElements(string fullJs, string name)
    {
        var def = FindDictArrayDefinition(fullJs, name);
        if (def is null) return null;

        var defSpan = def.AsSpan();

        // Try split pattern: "content".split("separator")
        if (TryParseSplitExpression(defSpan, out var content, out var separator))
            return SplitToArray(content, separator);

        // Try bracket array: [elem1, elem2, ...]
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

    public static string? DetectDictArrayName(string funcCode)
    {
        var paramNames = ExtractParamNames(funcCode);
        var countDict = new Dictionary<string, int>(8);

        var span = funcCode.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            int bracketPos = span[i..].IndexOf('[');
            if (bracketPos < 0) break;
            bracketPos += i;

            // After '[' must be digits followed by ']'
            int afterBracket = bracketPos + 1;
            if (afterBracket < span.Length && char.IsAsciiDigit(span[afterBracket]))
            {
                int digitEnd = afterBracket;
                while (digitEnd < span.Length && char.IsAsciiDigit(span[digitEnd])) digitEnd++;

                if (digitEnd < span.Length && span[digitEnd] == ']')
                {
                    // Extract identifier before '['
                    int nameEnd = bracketPos;
                    int nameStart = nameEnd - 1;
                    while (nameStart >= 0 && IsIdentChar(span[nameStart])) nameStart--;
                    nameStart++;

                    int nameLen = nameEnd - nameStart;
                    if (nameLen is >= 1 and <= 3 &&
                        (nameStart == 0 || !IsIdentChar(span[nameStart - 1])))
                    {
                        var arrName = span.Slice(nameStart, nameLen).ToString();
                        if (!paramNames.Contains(arrName) && !SkipNames.Contains(arrName))
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
        int bestCount = 1; // minimum 2 required
        foreach (var kv in countDict)
        {
            if (kv.Value > bestCount)
            {
                bestCount = kv.Value;
                best = kv.Key;
            }
        }

        return best;
    }

    public static HashSet<string> ExtractParamNames(string funcCode)
    {
        var result = new HashSet<string>(4);
        var span = funcCode.AsSpan();

        // Try: function(param1, param2)
        if (span.StartsWith("function"))
        {
            int parenStart = span.IndexOf('(');
            if (parenStart >= 0)
            {
                int parenEnd = FindMatchingParen(funcCode, parenStart);
                if (parenEnd > parenStart)
                {
                    ParseCommaSeparatedIdents(
                        span.Slice(parenStart + 1, parenEnd - parenStart - 1), result);
                    return result;
                }
            }
        }

        // Try: (params) => ...  or  async (params) => ...
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
                ParseCommaSeparatedIdents(
                    span.Slice(openParen + 1, closeParen - openParen - 1), result);
                return result;
            }
        }

        // Try: singleParam => ...
        int arrowIdx = span.IndexOf("=>");
        if (arrowIdx > 0)
        {
            var paramPart = span[..arrowIdx].Trim();
            if (paramPart.Length > 0 && s_identStartChars.Contains(paramPart[0]))
                result.Add(paramPart.ToString());
        }

        return result;
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
        // target: "name=function("
        var target = string.Concat(name, "=function(");
        var targetSpan = target.AsSpan();

        int searchFrom = 0;
        while (searchFrom < fullSpan.Length)
        {
            int idx = fullSpan[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += searchFrom;
            searchFrom = idx + targetSpan.Length;

            if (idx > 0 && IsIdentChar(fullSpan[idx - 1])) continue;

            int funcStart = idx + name.Length + 1;
            int braceOffset = fullSpan[funcStart..].IndexOf('{');
            if (braceOffset < 0 || braceOffset > 300) continue;

            int braceStart = funcStart + braceOffset;
            int braceEnd = FindMatchingBrace(fullJs, braceStart);
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

            if (idx > 0 && IsIdentChar(fullSpan[idx - 1])) continue;

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

            if (idx > 0 && IsIdentChar(fullSpan[idx - 1])) continue;

            int afterEq = idx + name.Length + 1;
            if (afterEq < fullSpan.Length && fullSpan[afterEq] == '=') continue;
            if (!IsStatementBoundary(fullSpan, idx)) continue;

            int valueStart = SkipHorizontalWhitespace(fullSpan, afterEq);
            if (valueStart >= fullSpan.Length) continue;

            // Check for "function" keyword — 8 chars
            if (valueStart + 8 <= fullSpan.Length &&
                fullSpan.Slice(valueStart, 8).SequenceEqual("function"))
                continue;
            if (IsArrowFunctionStart(fullJs, valueStart)) continue;

            int valueEnd = SkipValue(fullJs, valueStart);
            if (valueEnd <= valueStart) continue;
            if (valueEnd < fullSpan.Length && fullSpan[valueEnd] is ';' or ',') valueEnd++;

            var valueSpan = fullSpan[valueStart..valueEnd];
            if (!IsValidValue(valueSpan)) continue;

            return fullJs[valueStart..valueEnd];
        }
        return null;
    }

    /// <summary>
    /// Fallback for object definitions (handles cases like TZ = {...}).
    /// </summary>
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

            // Word boundary: before
            if (idx > 0 && (char.IsLetterOrDigit(fullSpan[idx - 1]) ||
                            fullSpan[idx - 1] is '_' or '$' or '.'))
                continue;

            // Word boundary: after
            int afterName = idx + name.Length;
            if (afterName < fullSpan.Length &&
                (char.IsLetterOrDigit(fullSpan[afterName]) || fullSpan[afterName] is '_' or '$'))
                continue;

            int pos = SkipWhitespace(fullSpan, afterName);
            if (pos >= fullSpan.Length || fullSpan[pos] != '=') continue;
            pos++;

            // Skip '==' (comparison)
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

        // Pattern 1: name=function(
        var funcTarget = string.Concat(name, "=function(");
        int idx = jsSpan.IndexOf(funcTarget.AsSpan(), StringComparison.Ordinal);
        if (idx >= 0 && (idx == 0 || !IsIdentChar(jsSpan[idx - 1])))
        {
            int funcStart = idx + name.Length + 1;
            int braceOffset = jsSpan[funcStart..].IndexOf('{');
            if (braceOffset >= 0 && braceOffset <= 300)
            {
                int braceStart = funcStart + braceOffset;
                int braceEnd = FindMatchingBrace(js, braceStart);
                if (braceEnd > 0)
                    return new JsFunctionInfo(name, js[funcStart..(braceEnd + 1)], idx);
            }
        }

        // Pattern 2: name=...=>  (arrow function)
        var eqTarget = string.Concat(name, "=");
        var eqTargetSpan = eqTarget.AsSpan();

        int searchFrom = 0;
        while (searchFrom < jsSpan.Length)
        {
            idx = jsSpan[searchFrom..].IndexOf(eqTargetSpan, StringComparison.Ordinal);
            if (idx < 0) break;
            idx += searchFrom;
            searchFrom = idx + eqTargetSpan.Length;

            if (idx > 0 && IsIdentChar(jsSpan[idx - 1])) continue;

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
            int braceOffset2 = jsSpan[funcStart2..].IndexOf('{');
            if (braceOffset2 >= 0 && braceOffset2 <= 300)
            {
                int braceStart = funcStart2 + braceOffset2;
                int braceEnd = FindMatchingBrace(js, braceStart);
                if (braceEnd > 0)
                    return new JsFunctionInfo(name, js[funcStart2..(braceEnd + 1)], idx);
            }
        }

        return null;
    }

    public static HashSet<string> FindReferencedNames(string code)
    {
        var result = new HashSet<string>(32);
        var span = code.AsSpan();
        int i = 0;

        while (i < span.Length)
        {
            char c = span[i];

            // Skip strings
            if (c is '"' or '\'' or '`')
            {
                i = SkipString(code, i);
                continue;
            }

            // Skip comments
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
            }

            // Identifier start
            if (s_identStartChars.Contains(c))
            {
                int start = i;
                i++;
                while (i < span.Length &&
                       (char.IsLetterOrDigit(span[i]) || span[i] is '_' or '$'))
                    i++;

                int len = i - start;
                if (len is >= 2 and <= 20 &&
                    (start == 0 || span[start - 1] != '.'))
                {
                    var ident = span.Slice(start, len).ToString();
                    if (!SkipNames.Contains(ident))
                        result.Add(ident);
                }
                continue;
            }

            // Skip numeric tokens
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

        // Optimized: scan for quote or backslash using IndexOfAny
        var span = js.AsSpan();
        while (i < span.Length)
        {
            int found = span[i..].IndexOfAny(quote, '\\');
            if (found < 0) return span.Length; // unterminated string
            i += found;

            if (span[i] == '\\') { i += 2; continue; }

            return i + 1; // closing quote
        }

        return i;
    }

    public readonly record struct JsFunctionInfo(string Name, string Code, int Position);

    // ═══════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Skips ' ' and '\t', returns new position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipHorizontalWhitespace(ReadOnlySpan<char> span, int pos)
    {
        while (pos < span.Length && span[pos] is ' ' or '\t') pos++;
        return pos;
    }

    /// <summary>Skips all whitespace chars, returns new position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipWhitespace(ReadOnlySpan<char> span, int pos)
    {
        while (pos < span.Length && char.IsWhiteSpace(span[pos])) pos++;
        return pos;
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

            if (idx > 0 && IsIdentChar(fullSpan[idx - 1])) continue;

            int bracketPos = idx + name.Length + 1;
            int bracketEnd = FindMatchingBracket(fullJs, bracketPos);
            if (bracketEnd < 0) continue;

            int elementCount = CountTopLevelCommas(fullJs, bracketPos + 1, bracketEnd) + 1;
            if (elementCount < 50) continue;

            int sampleEnd = Math.Min(bracketPos + 500, bracketEnd);
            var sampleSpan = fullSpan[bracketPos..sampleEnd];
            if (!sampleSpan.ContainsAny(s_quoteChars)) continue;

            int end = bracketEnd + 1;
            if (end < fullSpan.Length && fullSpan[end] == ';') end++;

            var valuePart = fullSpan[bracketPos..end];
            return string.Concat("var ".AsSpan(), name.AsSpan(), "=".AsSpan(), valuePart);
        }
        return null;
    }

    private static string? FindSplitArrayDefinition(string fullJs, string name)
    {
        var fullSpan = fullJs.AsSpan();

        // Build targets outside loop to avoid stackalloc-in-loop warning
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

                if (idx > 0 && IsIdentChar(fullSpan[idx - 1])) continue;

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
                int semicolonCount = 0;
                foreach (char c in stringContent)
                {
                    if (c == ';') semicolonCount++;
                }
                if (semicolonCount + 1 < 50) continue;

                var definitionSpan = fullSpan[(idx + name.Length + 1)..end];

                // Fix trailing comma
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

        // Skip function(...) { ... }
        if (c == 'f' && i + 8 <= span.Length && span.Slice(i, 8).SequenceEqual("function"))
        {
            int braceOffset = span[(i + 8)..].IndexOf('{');
            if (braceOffset < 0 || braceOffset > 200) return i;
            int braceStart = i + 8 + braceOffset;
            int braceEnd = FindMatchingBrace(js, braceStart);
            return braceEnd >= 0 ? braceEnd + 1 : i;
        }

        if (c == '{') { int end = FindMatchingBrace(js, i); return end >= 0 ? end + 1 : i; }
        if (c == '[') { int end = FindMatchingBracket(js, i); return end >= 0 ? end + 1 : i; }
        if (c == '(') { int end = FindMatchingParen(js, i); return end >= 0 ? end + 1 : i; }
        if (c is '"' or '\'' or '`') return SkipString(js, i);

        // Skip new Class(...)
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

        // Skip to next statement boundary
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
    /// Universal matching bracket finder, respecting strings and comments.
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
                    break;
            }
            i++;
        }

        return depth == 0 ? i - 1 : -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // SPAN-BASED PARSING HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Zero-alloc parse of "content".split("separator") pattern.
    /// </summary>
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

        // Skip whitespace
        int ws = 0;
        while (ws < rest.Length && rest[ws] is ' ' or '\t') ws++;
        rest = rest[ws..];

        if (!rest.StartsWith(".split(")) return false;
        rest = rest[7..]; // skip ".split("

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

    /// <summary>
    /// Splits content by separator. Single allocation for result array.
    /// </summary>
    private static string[] SplitToArray(ReadOnlySpan<char> content, ReadOnlySpan<char> separator)
    {
        // Count occurrences first
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

    /// <summary>
    /// Splits bracket array elements, trimming quotes and whitespace.
    /// </summary>
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
                if (elemLen > 0)
                    list.Add(inner.Slice(start, elemLen).ToString());
                if (i < inner.Length) i++; // skip closing quote
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

    /// <summary>
    /// Parses comma-separated identifiers from span into result set.
    /// </summary>
    private static void ParseCommaSeparatedIdents(ReadOnlySpan<char> span, HashSet<string> result)
    {
        int start = 0;
        while (start < span.Length)
        {
            // Skip whitespace
            while (start < span.Length && span[start] is ' ' or '\t' or '\n' or '\r') start++;
            if (start >= span.Length) break;

            // Find comma or end
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

    /// <summary>
    /// Concatenates spans into a single string via thread-local StringBuilder.
    /// </summary>
    private static string ConcatSpans(
        ReadOnlySpan<char> a, ReadOnlySpan<char> b,
        ReadOnlySpan<char> c, ReadOnlySpan<char> d)
    {
        var sb = t_concatBuilder ??= new StringBuilder(256);
        sb.Clear();
        sb.Append(a).Append(b).Append(c).Append(d);
        return sb.ToString();
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

    // ═══════════════════════════════════════════════════════════════
    // GENERATED REGEX
    // ═══════════════════════════════════════════════════════════════

    [GeneratedRegex(@"typeof\s+([a-zA-Z_$][\w$]*)\s*===?\s*""undefined""")]
    private static partial Regex TypeofUndefinedRegex();

    [GeneratedRegex(@"typeof\s+([a-zA-Z_$][\w$]*)\s*===?\s*[a-zA-Z_$]{1,3}\[\d+\]")]
    private static partial Regex TypeofArrayRegex();
}