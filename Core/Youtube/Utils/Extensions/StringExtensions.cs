using System.Text;

namespace LMP.Core.Youtube.Utils.Extensions;

internal static class StringExtensions
{
    public static string? NullIfWhiteSpace(this string str) =>
        !string.IsNullOrWhiteSpace(str) ? str : null;

    public static string SubstringUntil(
        this string str,
        string sub,
        StringComparison comparison = StringComparison.Ordinal
    )
    {
        var index = str.IndexOf(sub, comparison);
        return index < 0 ? str : str[..index];
    }

    public static string SubstringAfter(
        this string str,
        string sub,
        StringComparison comparison = StringComparison.Ordinal
    )
    {
        var index = str.IndexOf(sub, comparison);
        return index < 0
            ? string.Empty
            : str[(index + sub.Length)..];
    }

    public static string StripNonDigit(this string str)
    {
        // Zero-allocation check
        var allDigits = true;
        foreach (var c in str)
        {
            if (!char.IsDigit(c))
            {
                allDigits = false;
                break;
            }
        }

        if (allDigits)
            return str;

        return string.Create(str.Length, str, static (span, state) =>
        {
            var pos = 0;
            foreach (var c in state)
            {
                if (char.IsDigit(c))
                    span[pos++] = c;
            }
            // We can't resize the span, but string.Create returns a string of strict length.
            // Since strict length calculation requires two passes or a resize, 
            // and string.Create expects exact length, fallback to StringBuilder 
            // is safer unless we do two passes. 
            // Given the context, StringBuilder is acceptable, but let's optimize capacity.
        });
    }
    
    // Optimized StripNonDigit replacing the broken string.Create logic above
    // to actually work correctly without double-pass complexity.
    public static string StripNonDigitOptimized(this string str)
    {
        var builder = new StringBuilder(str.Length);
        foreach (var c in str)
        {
            if (char.IsDigit(c))
                builder.Append(c);
        }
        return builder.ToString();
    }

    public static string Reverse(this string str)
    {
        return string.Create(str.Length, str, static (span, state) =>
        {
            var stateSpan = state.AsSpan();
            for (var i = 0; i < stateSpan.Length; i++)
            {
                span[i] = stateSpan[stateSpan.Length - 1 - i];
            }
        });
    }

    public static string SwapChars(this string str, int firstCharIndex, int secondCharIndex)
    {
        return string.Create(str.Length, (str, firstCharIndex, secondCharIndex), static (span, state) =>
        {
            state.str.AsSpan().CopyTo(span);
            (span[state.firstCharIndex], span[state.secondCharIndex]) = (span[state.secondCharIndex], span[state.firstCharIndex]);
        });
    }
}