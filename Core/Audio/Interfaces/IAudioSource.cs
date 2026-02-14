namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Источник аудио данных (сетевой стрим, файл и т.д.).
/// Предоставляет сжатые аудио-фреймы для декодирования.
/// </summary>
public interface IAudioSource : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Инициализирует источник и подготавливает к стримингу.
    /// </summary>
    ValueTask<bool> InitializeAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Читает следующий сжатый аудио-фрейм.
    /// </summary>
    ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Перемещается к указанной позиции.
    /// </summary>
    ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default);
    
    /// <summary>Общая длительность в миллисекундах (-1 если неизвестно)</summary>
    long DurationMs { get; }
    
    /// <summary>Текущая позиция в миллисекундах</summary>
    long PositionMs { get; }
    
    /// <summary>Поддерживает ли источник seeking</summary>
    bool CanSeek { get; }
    
    /// <summary>Тип кодека аудио данных</summary>
    AudioCodec Codec { get; }
    
    /// <summary>Прогресс буферизации (0-100)</summary>
    double BufferProgress { get; }
    
    /// <summary>Полностью загружен</summary>
    bool IsFullyBuffered { get; }
    
    /// <summary>
    /// Decoder-specific config (ASC для AAC, CodecPrivate для Opus).
    /// Null если не требуется или недоступен.
    /// </summary>
    byte[]? DecoderConfig { get; }
    
    /// <summary>Sample rate из контейнера (0 если неизвестен)</summary>
    int SampleRate { get; }
    
    /// <summary>Количество каналов из контейнера (0 если неизвестно)</summary>
    int Channels { get; }
    
    /// <summary>Буферизованные диапазоны для визуализации</summary>
    IReadOnlyList<(double Start, double End)> GetBufferedRanges();
    
    /// <summary>Освобождает RAM буферы</summary>
    void ReleaseRamBuffers();
    
    /// <summary>Отмена текущих операций</summary>
    void CancelPendingOperations();
}

/// <summary>
/// Аудио-фрейм с метаданными
/// </summary>
public readonly struct AudioFrame
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required long TimestampMs { get; init; }
    public required int DurationMs { get; init; }
    public bool IsKeyFrame { get; init; }
}

/// <summary>
/// Поддерживаемые аудио кодеки
/// </summary>
public enum AudioCodec
{
    Unknown = 0,
    Opus = 1,
    Aac = 2,
    Vorbis = 3
}