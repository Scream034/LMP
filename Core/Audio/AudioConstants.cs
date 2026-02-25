namespace LMP.Core.Audio;

/// <summary>
/// Централизованные константы аудио системы.
/// Единственный источник истины для всех числовых параметров.
/// </summary>
public static class AudioConstants
{
    // ═══════════════════════════════════════════════════════
    // CHUNK SETTINGS — Управление сегментацией данных
    // ═══════════════════════════════════════════════════════

    /// <summary>Размер чанка для кэширования (64KB = оптимум для HTTP Range + минимум аллокаций).</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>Максимум чанков в RAM (32 × 64KB = 2MB RAM на трек).</summary>
    public const int MaxRamChunks = 32;

    /// <summary>Расстояние от текущей позиции для eviction чанков из RAM.</summary>
    public const int RamEvictionDistance = 10;

    // ═══════════════════════════════════════════════════════
    // PRELOAD SETTINGS — Стратегия упреждающей загрузки
    // ═══════════════════════════════════════════════════════

    /// <summary>Чанков загружать перед стартом воспроизведения (300ms @ 128kbps).</summary>
    public const int InitialChunksToLoad = 3;

    /// <summary>Чанков держать впереди от текущей позиции (adaptive buffering).</summary>
    public const int PreloadAheadChunks = 4;

    /// <summary>Чанков загружать при seek (instant seek UX).</summary>
    public const int SeekPreloadChunks = 2;

    /// <summary>Интервал проверки preload loop (ms).</summary>
    public const int PreloadIntervalMs = 1000;

    /// <summary>Максимум параллельных загрузок чанков (баланс скорость/RAM).</summary>
    public const int MaxConcurrentDownloads = 3;

    // ═══════════════════════════════════════════════════════
    // DOWNLOAD TIMEOUTS — HTTP операции
    // ═══════════════════════════════════════════════════════

    /// <summary>Таймаут загрузки одного чанка (15s = mobile-friendly).</summary>
    public const int DownloadTimeoutMs = 15_000;

    /// <summary>Таймаут ожидания слота загрузки (500ms = non-blocking).</summary>
    public const int DownloadSlotTimeoutMs = 500;

    // ═══════════════════════════════════════════════════════
    // BACKGROUND DOWNLOAD LIMITS — Экономия сети
    // ═══════════════════════════════════════════════════════

    /// <summary>Циклов простоя перед фоновой докачкой (5 × 1s = 5s idle).</summary>
    public const int BackgroundFillIdleCycles = 5;

    /// <summary>Пауза между фоновыми загрузками (5s = gentle network usage).</summary>
    public const int BackgroundFillIntervalMs = 5_000;

    /// <summary>Максимум чанков для фоновой докачки за сессию (0 = unlimited).</summary>
    public const int MaxBackgroundChunksPerSession = 50;

    /// <summary>Минимум буфера впереди для начала фоновой докачки.</summary>
    public const int MinBufferAheadForBackgroundFill = 6;

    // ═══════════════════════════════════════════════════════
    // DECODER SETTINGS — Параметры декодирования
    // ═══════════════════════════════════════════════════════

    /// <summary>Sample rate по умолчанию (48kHz = industry standard).</summary>
    public const int DefaultSampleRate = 48_000;

    /// <summary>Количество каналов по умолчанию (stereo).</summary>
    public const int DefaultChannels = 2;

    /// <summary>Буфер декодирования (samples, не байты).</summary>
    public const int DecoderBufferFrames = 8192;

    /// <summary>Кадров пропустить после seek для Opus (pre-skip compensation).</summary>
    public const int SkipFramesAfterSeekOpus = 2;

    /// <summary>Кадров пропустить после seek для AAC (encoder delay).</summary>
    public const int SkipFramesAfterSeekAac = 5;

    /// <summary>Таймаут graceful shutdown декодера (ms).</summary>
    public const int DecoderStopTimeoutMs = 500;

    // ═══════════════════════════════════════════════════════
    // PLAYBACK BUFFER SETTINGS — PCM циклический буфер
    // ═══════════════════════════════════════════════════════

    /// <summary>Размер PCM буфера в секундах (2s = smooth playback).</summary>
    public const int BufferSizeSeconds = 2;

    /// <summary>Минимальный буфер для старта воспроизведения (ms).</summary>
    public const int MinBufferMs = 300;

    /// <summary>Минимальный буфер для возобновления после seek (ms).</summary>
    public const int MinSeekResumeBufferMs = 80;

    // ═══════════════════════════════════════════════════════
    // POSITION REPORTING — UI обновления
    // ═══════════════════════════════════════════════════════

    /// <summary>Интервал обновления позиции по умолчанию (ms).</summary>
    public const int DefaultPositionUpdateIntervalMs = 200;

    /// <summary>Интервал проверки buffer state (ms).</summary>
    public const int BufferStateUpdateIntervalMs = 500;

    // ═══════════════════════════════════════════════════════
    // RETRY POLICY — Обработка ошибок
    // ═══════════════════════════════════════════════════════

    /// <summary>Максимум попыток повтора операций.</summary>
    public const int MaxRetryAttempts = 3;

    /// <summary>Задержка между попытками (ms).</summary>
    public const int RetryDelayMs = 1_000;

    // ═══════════════════════════════════════════════════════
    // CACHE MANAGEMENT — Дисковый кэш
    // ═══════════════════════════════════════════════════════

    /// <summary>Имя файла метаданных кэша.</summary>
    public const string CacheMetadataFileName = "cache_index.json";

    /// <summary>Расширение файлов кэша.</summary>
    public const string CacheFileExtension = ".audio";

    /// <summary>Интервал автосохранения индекса кэша (ms).</summary>
    public const int CacheAutoSaveIntervalMs = 30_000;

    /// <summary>Таймаут блокировки при сохранении индекса (ms).</summary>
    public const int CacheSaveLockTimeoutMs = 100;

    /// <summary>Размер буфера для файловых операций (64KB).</summary>
    public const int CacheFileBufferSize = 65_536;

    /// <summary>Порог очистки кэша (80% от максимума).</summary>
    public const double CacheCleanupThreshold = 0.8;

    /// <summary>
    /// Нормализация битрейта для кэш-ключей.
    /// 
    /// <para><b>ЕДИНСТВЕННЫЙ ИСТОЧНИК ИСТИНЫ</b> для нормализации битрейта.
    /// Используется в <see cref="AudioSourceFactory.BuildCacheKey"/>
    /// и <see cref="Cache.AudioCacheManager.BuildCacheKey"/>.</para>
    /// 
    /// <para>Группирует близкие битрейты в стандартные bucket'ы,
    /// чтобы один трек не дублировался в кэше из-за minor различий
    /// (например, 127kbps и 128kbps → оба → 128).</para>
    /// </summary>
    /// <param name="bitrate">Битрейт в kbps.</param>
    /// <returns>Нормализованный битрейт.</returns>
    public static int NormalizeBitrate(int bitrate) => bitrate switch
    {
        <= 0 => 0,        // Неизвестный — не нормализуем, 0 = "любой"
        < 50 => 48,       // ~48kbps (Opus low)
        < 80 => 64,       // ~64kbps (Opus medium-low)
        < 110 => 96,      // ~96kbps (AAC standard)
        < 140 => 128,     // ~128kbps (Opus standard)
        < 180 => 160,     // ~160kbps (Opus high / AAC high)
        < 260 => 256,     // ~256kbps (Opus very high)
        _ => 320          // 320kbps+
    };

    // ═══════════════════════════════════════════════════════
    // HLS SETTINGS — Deprecated
    // ═══════════════════════════════════════════════════════

    /// <summary>Сегментов для упреждающей загрузки в HLS.</summary>
    [Obsolete("HLS is deprecated. See HlsStreamSource.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public const int HlsPrefetchSegments = 3;

    /// <summary>Длительность AAC фрейма (ms) для HLS (~1024 samples @ 44.1kHz).</summary>
    [Obsolete("HLS is deprecated. See HlsStreamSource.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public const int HlsAacFrameDurationMs = 23;

    /// <summary>Интервал проверки HLS prefetch (ms).</summary>
    [Obsolete("HLS is deprecated. See HlsStreamSource.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public const int HlsPrefetchIntervalMs = 500;

    // ═══════════════════════════════════════════════════════
    // FORMAT DETECTION — Magic bytes для форматов
    // ═══════════════════════════════════════════════════════

    /// <summary>Размер заголовка для определения формата (bytes).</summary>
    public const int FormatDetectionHeaderSize = 12;

    /// <summary>WebM: EBML header magic bytes.</summary>
    public static ReadOnlySpan<byte> WebMMagic => [0x1A, 0x45, 0xDF, 0xA3];

    /// <summary>MP4: 'ftyp' box identifier at offset 4.</summary>
    public static ReadOnlySpan<byte> Mp4FtypMagic => "ftyp"u8;

    /// <summary>Ogg: 'OggS' page header magic.</summary>
    public static ReadOnlySpan<byte> OggMagic => "OggS"u8;

    // ═══════════════════════════════════════════════════════
    // AAC DECODER TABLES
    // ═══════════════════════════════════════════════════════

    private static readonly int[] AacSampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000
    ];

    /// <summary>Получить sample rate по индексу из AAC Audio Specific Config.</summary>
    public static int GetAacSampleRate(int index) =>
        (uint)index < (uint)AacSampleRates.Length ? AacSampleRates[index] : DefaultSampleRate;

    /// <summary>Получить количество каналов по channel configuration из AAC ASC.</summary>
    public static int GetAacChannels(int channelConfig) => channelConfig switch
    {
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        5 => 5,
        6 => 6,
        7 => 8,
        _ => DefaultChannels
    };
}