namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Числовые аргументы, передаваемые JS-функции перед основным входным параметром.
/// <para>
/// YouTube вызывает дешифраторы с фиксированными числовыми аргументами:
/// </para>
/// <list type="bullet">
///   <item><c>Tx(48, 8079, token, token)</c> → <c>Args = [48, 8079]</c></item>
///   <item><c>Xp(6, 4494, nToken)</c> → <c>Args = [6, 4494]</c></item>
///   <item><c>decrypt(nToken)</c> → <c>Args = []</c></item>
/// </list>
/// </summary>
/// <param name="Args">Массив магических чисел.</param>
internal sealed record MagicNumbers(int[] Args)
{
    /// <summary>Нет числовых аргументов.</summary>
    public static MagicNumbers None { get; } = new([]);

    /// <summary>Конструктор для двух magic numbers (наиболее частый случай).</summary>
    /// <param name="r">Первое число.</param>
    /// <param name="p">Второе число.</param>
    public MagicNumbers(int r, int p) : this([r, p]) { }

    /// <summary>Есть ли хотя бы один числовой аргумент.</summary>
    public bool HasArgs => Args.Length > 0;

    /// <summary>
    /// Формирует строку аргументов для вставки в JS-массив.
    /// <para>Примеры:</para>
    /// <list type="bullet">
    ///   <item><c>[48, 8079]</c> → <c>"48, 8079"</c></item>
    ///   <item><c>[]</c> → <c>"undefined"</c> (для корректного JS-массива без пустых слотов)</item>
    /// </list>
    /// </summary>
    /// <returns>JS-совместимая строка элементов массива.</returns>
    public string ToJsArgPrefix() => Args.Length == 0
        ? "undefined"
        : string.Join(", ", Args);

    /// <inheritdoc/>
    public override string ToString() => Args.Length == 0
        ? "MagicNumbers(none)"
        : $"MagicNumbers({string.Join(", ", Args)})";
}