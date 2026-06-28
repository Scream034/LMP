namespace LMP.Core.Youtube.Utils;

using System.Globalization;
using System.Runtime.CompilerServices;

/// <summary>
/// Обеспечивает централизованный высокопроизводительный парсинг локализованных данных YouTube.
/// </summary>
internal static class YoutubeParsingHelpers
{
    public static readonly string[] DateFormats =
    [
        "d MMM yyyy 'г.'",
        "d MMMM yyyy 'г.'",
        "d MMM yyyy",
        "d MMMM yyyy",
        "MMM d, yyyy",
        "MMMM d, yyyy",
    ];

    public static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    public static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRelativeDate(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var span = text.AsSpan();
        return span.Contains("сегодня".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("today".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("вчера".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("yesterday".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("назад".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("ago".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("час".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("hour".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("минут".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("minute".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("день".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("day".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("недел".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("week".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("месяц".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("month".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int? ParseYearFromText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 4) return null;
        for (int i = 0; i <= text.Length - 4; i++)
        {
            if (char.IsAsciiDigit(text[i])
                && char.IsAsciiDigit(text[i + 1])
                && char.IsAsciiDigit(text[i + 2])
                && char.IsAsciiDigit(text[i + 3]))
            {
                bool leftOk = i == 0 || !char.IsAsciiDigit(text[i - 1]);
                bool rightOk = i + 4 == text.Length || !char.IsAsciiDigit(text[i + 4]);

                if (leftOk && rightOk)
                {
                    int year = (text[i] - '0') * 1000
                             + (text[i + 1] - '0') * 100
                             + (text[i + 2] - '0') * 10
                             + (text[i + 3] - '0');
                    if (year >= 1900 && year <= 2100)
                        return year;
                }
            }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long? ParseLongFromText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        long result = 0;
        bool foundDigit = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                result = result * 10 + (c - '0');
                foundDigit = true;
            }
            else if (foundDigit && c != ',' && c != '.' && c != ' ' && c != '\u00A0')
            {
                break;
            }
        }

        return foundDigit ? result : null;
    }
}