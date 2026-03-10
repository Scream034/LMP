// Core/Youtube/Bridge/Common/MagicNumbers.cs

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Числовые аргументы, передаваемые JS-функции перед основным входным параметром.
/// <para>
/// YouTube вызывает дешифраторы с фиксированными числовыми аргументами,
/// которые предшествуют основному параметру (N-токен, сигнатура и т.д.):
/// </para>
/// <list type="bullet">
///   <item><c>Xp(6, 4494, nToken)</c> → <c>Args = [6, 4494]</c></item>
///   <item><c>KM(76, nToken)</c> → <c>Args = [76]</c></item>
///   <item><c>decrypt(nToken)</c> → <c>Args = []</c></item>
/// </list>
/// </summary>
internal sealed record MagicNumbers(int[] Args)
{
    /// <summary>Нет числовых аргументов — функция вызывается только с основным параметром.</summary>
    public static MagicNumbers None { get; } = new([]);

    /// <summary>Есть ли хотя бы один числовой аргумент.</summary>
    public bool HasArgs => Args.Length > 0;

    /// <summary>
    /// Формирует строку числовых аргументов для вставки в JS-вызов.
    /// <para>Примеры:</para>
    /// <list type="bullet">
    ///   <item><c>[6, 4494]</c> → <c>"6, 4494, "</c></item>
    ///   <item><c>[76]</c> → <c>"76, "</c></item>
    ///   <item><c>[]</c> → <c>""</c></item>
    /// </list>
    /// </summary>
    public string ToJsArgPrefix() => Args.Length == 0
        ? ""
        : string.Join(", ", Args) + ", ";

    /// <inheritdoc/>
    public override string ToString() => Args.Length == 0
        ? "MagicNumbers(none)"
        : $"MagicNumbers({string.Join(", ", Args)})";
}