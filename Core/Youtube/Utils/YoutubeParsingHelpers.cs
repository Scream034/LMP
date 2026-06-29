namespace LMP.Core.Youtube.Utils;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;

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

    /// <summary>
    /// Склеивает текст из массива runs без LINQ и без промежуточных коллекций.
    /// Использует stackalloc для строк до 256 символов.
    /// </summary>
    /// <param name="runsElement">JSON-элемент массива runs или null.</param>
    /// <returns>Склеенный текст или null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? ConcatTextRuns(JsonElement? runsElement)
    {
        if (runsElement is null || runsElement.Value.ValueKind != JsonValueKind.Array)
            return null;

        var array = runsElement.Value;
        int len = array.GetArrayLength();
        if (len == 0) return null;
        if (len == 1) return array[0].GetPropertyOrNull("text")?.GetStringOrNull();

        var parts = new string?[len];
        int totalLen = 0;

        for (int i = 0; i < len; i++)
        {
            var t = array[i].GetPropertyOrNull("text")?.GetStringOrNull();
            parts[i] = t;
            if (t is not null) totalLen += t.Length;
        }

        if (totalLen == 0) return null;

        Span<char> buf = totalLen <= 256 ? stackalloc char[totalLen] : new char[totalLen];
        int pos = 0;

        for (int i = 0; i < len; i++)
        {
            if (parts[i] is { } s)
            {
                s.AsSpan().CopyTo(buf[pos..]);
                pos += s.Length;
            }
        }

        return new string(buf[..pos]);
    }

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
                    if (year >= 1900 && year <= 2100) return year;
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
        bool found = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                result = result * 10 + (c - '0');
                found = true;
            }
            else if (found && c != ',' && c != '.' && c != ' ' && c != '\u00A0')
            {
                break;
            }
        }

        return found ? result : null;
    }
}