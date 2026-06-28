using System.Runtime.CompilerServices;
using System.Text;

namespace LMP.Core.Helpers.Extensions;

internal static class StringExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Truncate(this string? s, int len = 20)
    {
        if (s is null) return "null";
        return s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");
    }

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

        return StripNonDigitOptimized(str);
    }

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

    public static string Repeat(this char c, int count) => new(c, count);
}