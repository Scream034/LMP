using System.Runtime.CompilerServices;
using System.Text;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Предоставляет высокопроизводительные и низкоаллокационные методы расширения для работы со строками.
/// </summary>
internal static class StringExtensions
{
    /// <summary>
    /// Безопасно усекает строку до заданной длины и добавляет многоточие.
    /// Предотвращает аллокации, если строка укладывается в заданный лимит.
    /// </summary>
    /// <param name="s">Исходная строка.</param>
    /// <param name="len">Максимальная длина результирующей строки (включая многоточие).</param>
    /// <returns>Усеченная строка с многоточием либо исходная строка.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Truncate(this string? s, int len = 20)
    {
        if (s is null) return "null";
        return s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");
    }

    /// <summary>
    /// Возвращает <c>null</c>, если строка состоит только из пробелов; в противном случае возвращает исходную строку.
    /// </summary>
    /// <param name="str">Исходная строка.</param>
    /// <returns>Строка или <c>null</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? NullIfWhiteSpace(this string str) =>
        !string.IsNullOrWhiteSpace(str) ? str : null;

    /// <summary>
    /// Вырезает подстроку от начала до первого вхождения разделителя (не включая его).
    /// </summary>
    /// <param name="str">Исходная строка.</param>
    /// <param name="sub">Подстрока-разделитель.</param>
    /// <param name="comparison">Метод сравнения строк.</param>
    /// <returns>Подстрока до разделителя или исходная строка, если разделитель не найден.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string SubstringUntil(
        this string str,
        string sub,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var index = str.IndexOf(sub, comparison);
        return index < 0 ? str : str[..index];
    }

    /// <summary>
    /// Вырезает подстроку, следующую сразу после первого вхождения разделителя.
    /// </summary>
    /// <param name="str">Исходная строка.</param>
    /// <param name="sub">Подстрока-разделитель.</param>
    /// <param name="comparison">Метод сравнения строк.</param>
    /// <returns>Подстрока после разделителя или пустая строка, если разделитель не найден.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string SubstringAfter(
        this string str,
        string sub,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var index = str.IndexOf(sub, comparison);
        return index < 0
            ? string.Empty
            : str[(index + sub.Length)..];
    }

    /// <summary>
    /// Очищает строку от всех символов, не являющихся цифрами.
    /// Сначала производит быструю проверку без аллокаций.
    /// </summary>
    /// <param name="str">Исходная строка.</param>
    /// <returns>Строка, содержащая только цифры.</returns>
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

    /// <summary>
    /// Очищает строку от нецифровых символов с использованием оптимизированного <see cref="StringBuilder"/>.
    /// </summary>
    /// <param name="str">Исходная строка.</param>
    /// <returns>Очищенная строка.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    /// <summary>
    /// Высокопроизводительно переворачивает строку в обратном порядке без аллокаций промежуточных массивов.
    /// </summary>
    /// <param name="str">Исходная строка.</param>
    /// <returns>Строка в обратном порядке.</returns>
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

    /// <summary>
    /// Быстро меняет два символа местами по их индексам в куче с помощью <see cref="string.Create{TState}"/>.
    /// </summary>
    /// <param name="str">Исходная строка.</param>
    /// <param name="firstCharIndex">Индекс первого символа.</param>
    /// <param name="secondCharIndex">Индекс второго символа.</param>
    /// <returns>Новая строка с переставленными символами.</returns>
    public static string SwapChars(this string str, int firstCharIndex, int secondCharIndex)
    {
        return string.Create(str.Length, (str, firstCharIndex, secondCharIndex), static (span, state) =>
        {
            state.str.AsSpan().CopyTo(span);
            (span[state.firstCharIndex], span[state.secondCharIndex]) = (span[state.secondCharIndex], span[state.firstCharIndex]);
        });
    }
}