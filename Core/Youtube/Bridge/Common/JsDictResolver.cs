// Core/Youtube/Bridge/Common/JsDictResolver.cs
using System.Text;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Resolves dictionary-indexed property accesses in JavaScript code.
/// Replaces patterns like obj[q[N]] with obj.resolvedName or obj["resolvedName"],
/// and q[N] in other contexts with the literal string value.
/// </summary>
internal static class JsDictResolver
{
    /// <summary>
    /// Resolves all dictName[N] references in code using the provided dictionary elements.
    /// 
    /// Transformations:
    ///   identifier[dictName[N]]  →  identifier.value     (if value is valid JS identifier)
    ///   identifier[dictName[N]]  →  identifier["value"]  (if value contains special chars)
    ///   dictName[N]              →  "value"              (standalone references)
    /// </summary>
    public static string Resolve(string code, string dictName, string[] elements)
    {
        if (elements.Length == 0 || string.IsNullOrEmpty(code))
            return code;

        var span = code.AsSpan();
        var sb = new StringBuilder(code.Length);
        int pos = 0;

        var dictPrefix = string.Concat(dictName, "[");
        var dictPrefixSpan = dictPrefix.AsSpan();

        while (pos < span.Length)
        {
            int idx = span[pos..].IndexOf(dictPrefixSpan, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(span[pos..]);
                break;
            }

            idx += pos;

            // Word boundary: dictName must not be part of a larger identifier
            if (idx > 0 && IsIdentChar(span[idx - 1]))
            {
                sb.Append(span[pos..(idx + 1)]);
                pos = idx + 1;
                continue;
            }

            // Read the index number after dictName[
            int numStart = idx + dictPrefixSpan.Length;
            int numEnd = numStart;
            while (numEnd < span.Length && char.IsAsciiDigit(span[numEnd]))
                numEnd++;

            // Must have digits followed by ]
            if (numEnd == numStart || numEnd >= span.Length || span[numEnd] != ']')
            {
                sb.Append(span[pos..(idx + dictPrefixSpan.Length)]);
                pos = idx + dictPrefixSpan.Length;
                continue;
            }

            if (!int.TryParse(span[numStart..numEnd], out int dictIdx) ||
                dictIdx < 0 || dictIdx >= elements.Length)
            {
                sb.Append(span[pos..(numEnd + 1)]);
                pos = numEnd + 1;
                continue;
            }

            var resolvedValue = elements[dictIdx];
            int afterCloseBracket = numEnd + 1; // position after ]

            // Check context: is dictName[N] inside a property access bracket?
            // Pattern: something[dictName[N]]  →  something.value or something["value"]
            int lookBack = idx - 1;
            while (lookBack >= pos && span[lookBack] is ' ' or '\t') lookBack--;

            if (lookBack >= pos && span[lookBack] == '[')
            {
                // Found [dictName[N]] — check that there's an identifier before the outer [
                int beforeBracket = lookBack - 1;
                while (beforeBracket >= pos && span[beforeBracket] is ' ' or '\t') beforeBracket--;

                if (beforeBracket >= 0 &&
                    (IsIdentChar(span[beforeBracket]) || span[beforeBracket] is ')' or ']'))
                {
                    // Check for closing ] after dictName[N]]
                    int afterOuter = afterCloseBracket;
                    while (afterOuter < span.Length && span[afterOuter] is ' ' or '\t')
                        afterOuter++;

                    if (afterOuter < span.Length && span[afterOuter] == ']')
                    {
                        // Full pattern confirmed: obj[dictName[N]]
                        // Replace: remove outer [ before, dictName[N], outer ] after
                        // Append everything before the outer [
                        sb.Append(span[pos..lookBack]);

                        if (IsValidJsIdentifier(resolvedValue))
                        {
                            sb.Append('.');
                            sb.Append(resolvedValue);
                        }
                        else
                        {
                            sb.Append("[\"");
                            AppendEscaped(sb, resolvedValue);
                            sb.Append("\"]");
                        }

                        pos = afterOuter + 1; // skip past outer ]
                        continue;
                    }
                }
            }

            // Not a property access — standalone dictName[N] → "value"
            sb.Append(span[pos..idx]);
            sb.Append('"');
            AppendEscaped(sb, resolvedValue);
            sb.Append('"');
            pos = afterCloseBracket;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Detects the dictionary name used in a code fragment.
    /// Looks for patterns like: identifier[shortName[digits]]
    /// where shortName is 1-3 chars and appears frequently.
    /// </summary>
    public static string? DetectDictName(string code)
    {
        var counts = new Dictionary<string, int>(4);
        var span = code.AsSpan();
        int pos = 0;

        while (pos < span.Length)
        {
            int bracketIdx = span[pos..].IndexOf('[');
            if (bracketIdx < 0) break;
            bracketIdx += pos;
            pos = bracketIdx + 1;

            // Read identifier after [
            int identStart = pos;
            while (identStart < span.Length && span[identStart] is ' ' or '\t') identStart++;

            int identEnd = identStart;
            while (identEnd < span.Length &&
                   (char.IsLetterOrDigit(span[identEnd]) || span[identEnd] is '_' or '$'))
                identEnd++;

            int identLen = identEnd - identStart;
            if (identLen is < 1 or > 3) continue;

            // Must be followed by [digits]
            int afterIdent = identEnd;
            while (afterIdent < span.Length && span[afterIdent] is ' ' or '\t') afterIdent++;
            if (afterIdent >= span.Length || span[afterIdent] != '[') continue;

            int numCheckStart = afterIdent + 1;
            while (numCheckStart < span.Length && span[numCheckStart] is ' ' or '\t') numCheckStart++;
            if (numCheckStart >= span.Length || !char.IsAsciiDigit(span[numCheckStart])) continue;

            var name = span.Slice(identStart, identLen).ToString();

            if (counts.TryGetValue(name, out int count))
                counts[name] = count + 1;
            else
                counts[name] = 1;
        }

        string? best = null;
        int bestCount = 2; // minimum 3 occurrences
        foreach (var kv in counts)
        {
            if (kv.Value > bestCount)
            {
                bestCount = kv.Value;
                best = kv.Key;
            }
        }

        return best;
    }

    /// <summary>
    /// Full pipeline: detect dict name → find dict definition → resolve all references.
    /// Returns resolved code, or original if no dict found.
    /// </summary>
    public static string ResolveFromFullJs(string code, string fullJs)
    {
        var dictName = DetectDictName(code);
        if (dictName is null) return code;

        var elements = JsFunctionExtractor.ExtractArrayElements(fullJs, dictName);
        if (elements is null || elements.Length < 10) return code;

        Log.Debug($"[JsDictResolver] Resolving '{dictName}' ({elements.Length} elements) in {code.Length} chars");

        var resolved = Resolve(code, dictName, elements);

        Log.Debug($"[JsDictResolver] Resolved: {code.Length} → {resolved.Length} chars");
        return resolved;
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '$';

    private static bool IsValidJsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!char.IsLetter(value[0]) && value[0] is not '_' and not '$') return false;

        for (int i = 1; i < value.Length; i++)
        {
            if (!char.IsLetterOrDigit(value[i]) && value[i] is not '_' and not '$')
                return false;
        }

        return true;
    }

    private static void AppendEscaped(StringBuilder sb, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
    }
}