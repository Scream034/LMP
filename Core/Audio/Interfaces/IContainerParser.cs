namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Парсер контейнерного формата (WebM, MP4 и т.д.).
/// </summary>
public interface IContainerParser : IAsyncDisposable, IDisposable
{
    /// <summary>Длительность в миллисекундах.</summary>
    long DurationMs { get; }
    
    /// <summary>Кодек аудио данных.</summary>
    AudioCodec Codec { get; }
    
    /// <summary>Decoder-specific config (ASC для AAC, CodecPrivate для Opus).</summary>
    byte[]? DecoderConfig { get; }
    
    /// <summary>Sample rate (0 если неизвестен).</summary>
    int SampleRate { get; }
    
    /// <summary>Channels (0 если неизвестно).</summary>
    int Channels { get; }
    
    /// <summary>
    /// Парсит заголовки контейнера.
    /// </summary>
    ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Читает следующий аудио-фрейм.
    /// </summary>
    ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Находит позицию для seek.
    /// </summary>
    (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs);
    
    /// <summary>
    /// Сбрасывает состояние парсера после seek.
    /// </summary>
    void Reset();
}