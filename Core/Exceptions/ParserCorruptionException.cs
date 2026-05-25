namespace LMP.Core.Exceptions;

/// <summary>
/// Сигнал от парсера о структурном повреждении данных (Validation by Consumption).
/// </summary>
public sealed class ParserCorruptionException : Exception
{
    /// <summary>
    /// Абсолютная байтовая позиция в потоке, где был обнаружен мусор.
    /// Используется слоем самовосстановления для вычисления индекса битого чанка.
    /// </summary>
    public long AbsoluteBytePosition { get; }

    public ParserCorruptionException(long absoluteBytePosition, string message, Exception? inner = null) 
        : base(message, inner)
    {
        AbsoluteBytePosition = absoluteBytePosition;
    }
}