namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Декодер сжатого аудио в PCM.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Декодирует сжатый фрейм в PCM float32.
    /// </summary>
    /// <param name="encodedData">Сжатые данные (один фрейм)</param>
    /// <param name="outputBuffer">Выходной буфер (float32, interleaved)</param>
    /// <returns>Количество семплов на канал</returns>
    int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer);
    
    /// <summary>
    /// Декодирует со сбросом состояния (после seek).
    /// </summary>
    int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer);
    
    /// <summary>Частота дискретизации</summary>
    int SampleRate { get; }
    
    /// <summary>Количество каналов</summary>
    int Channels { get; }
    
    /// <summary>Максимальный размер выходного буфера в семплах на канал</summary>
    int MaxFrameSize { get; }
    
    /// <summary>Тип кодека</summary>
    AudioCodec Codec { get; }
    
    /// <summary>Инициализирован ли декодер</summary>
    bool IsInitialized { get; }
}