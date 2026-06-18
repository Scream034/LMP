namespace LMP.Core.Models;

/// <summary>
/// Конфигурация потоковой передачи и буферизации аудио.
/// Создаётся через <see cref="StreamingProfiles"/> на основе <see cref="InternetProfile"/>.
/// Иммутабельна после создания — безопасно передавать между потоками.
/// </summary>
public sealed record StreamingConfig
{
    #region Chunk Settings

    /// <summary>Размер одного чанка в байтах.</summary>
    public int ChunkSizeBytes { get; init; } = Defaults.ChunkSizeBytes;

    /// <summary>Количество чанков, читаемых вперёд от текущей позиции.</summary>
    public int ReadAheadChunks { get; init; } = Defaults.ReadAheadChunks;

    /// <summary>Максимальное количество чанков в RAM.</summary>
    public int MaxRamChunks { get; init; } = Defaults.MaxRamChunks;

    /// <summary>Расстояние от текущей позиции для eviction чанков из RAM.</summary>
    public int RamEvictionDistance { get; init; } = Defaults.RamEvictionDistance;

    #endregion

    #region Download Settings

    /// <summary>Максимальное количество параллельных загрузок чанков.</summary>
    public int MaxConcurrentDownloads { get; init; } = Defaults.MaxConcurrentDownloads;

    /// <summary>Таймаут загрузки одного чанка (мс).</summary>
    public int DownloadTimeoutMs { get; init; } = Defaults.DownloadTimeoutMs;

    /// <summary>Таймаут ожидания слота загрузки (мс). 0 = без ожидания.</summary>
    public int DownloadSlotTimeoutMs { get; init; } = Defaults.DownloadSlotTimeoutMs;

    #endregion

    #region Retry / Resilience

    /// <summary>Максимум повторных попыток при сетевых ошибках (IOException, HttpRequestException).</summary>
    public int MaxNetworkRetries { get; init; } = Defaults.MaxNetworkRetries;

    /// <summary>Базовая задержка между retry (мс). При exponential backoff умножается на 2^attempt.</summary>
    public int NetworkRetryBaseDelayMs { get; init; } = Defaults.NetworkRetryBaseDelayMs;

    /// <summary>Использовать exponential backoff при retry.</summary>
    public bool UseExponentialBackoff { get; init; } = Defaults.UseExponentialBackoff;

    /// <summary>Максимум последовательных HTTP 403 перед circuit breaker.</summary>
    public int Max403BeforeCircuitBreak { get; init; } = Defaults.Max403BeforeCircuitBreak;

    /// <summary>Минимальный интервал между URL refresh запросами (мс).</summary>
    public int RefreshCooldownMs { get; init; } = Defaults.RefreshCooldownMs;

    /// <summary>Задержка после URL refresh перед retry загрузки (мс).</summary>
    public int PostRefreshDelayMs { get; init; } = Defaults.PostRefreshDelayMs;

    #endregion

    #region Pre-Buffer Settings

    /// <summary>Размер предварительного буфера (в чанках) перед стартом воспроизведения.</summary>
    public int InitialChunksToLoad { get; init; } = Defaults.InitialChunksToLoad;

    /// <summary>Размер предварительного буфера (в чанках) при перетаскивании ползунка (seek).</summary>
    public int SeekPreloadChunks { get; init; } = Defaults.SeekPreloadChunks;

    #endregion

    #region Background Fill

    /// <summary>Циклов простоя перед фоновой докачкой.</summary>
    public int BackgroundFillIdleCycles { get; init; } = Defaults.BackgroundFillIdleCycles;

    /// <summary>Пауза между фоновыми загрузками (мс).</summary>
    public int BackgroundFillIntervalMs { get; init; } = Defaults.BackgroundFillIntervalMs;

    /// <summary>Максимум чанков для фоновой докачки за сессию (0 = unlimited).</summary>
    public int MaxBackgroundChunksPerSession { get; init; } = Defaults.MaxBackgroundChunksPerSession;

    /// <summary>Минимум буфера впереди перед началом фоновой докачки.</summary>
    public int MinBufferAheadForBackgroundFill { get; init; } = Defaults.MinBufferAheadForBackgroundFill;

    #endregion

    #region Preload Loop

    /// <summary>Интервал preload loop (мс).</summary>
    public int PreloadIntervalMs { get; init; } = Defaults.PreloadIntervalMs;

    #endregion

    /// <summary>
    /// Значения по умолчанию — совпадают с обновленным сбалансированным Medium (бывший High) профилем (64 КБ).
    /// </summary>
    public static class Defaults
    {
        // Chunk Settings
        public const int ChunkSizeBytes = 64 * 1024;
        public const int ReadAheadChunks = 6;
        public const int MaxRamChunks = 96;
        public const int RamEvictionDistance = 14;

        // Download Settings
        public const int MaxConcurrentDownloads = 3;
        public const int DownloadTimeoutMs = 15_000;
        public const int DownloadSlotTimeoutMs = 300;

        // Retry / Resilience
        public const int MaxNetworkRetries = 3;
        public const int NetworkRetryBaseDelayMs = 500;
        public const bool UseExponentialBackoff = true;
        public const int Max403BeforeCircuitBreak = 3;
        public const int RefreshCooldownMs = 3000;
        public const int PostRefreshDelayMs = 500;

        // Pre-buffer Settings
        public const int InitialChunksToLoad = 4;
        public const int SeekPreloadChunks = 6;

        // Background Fill
        public const int BackgroundFillIdleCycles = 5;
        public const int BackgroundFillIntervalMs = 3000;
        public const int MaxBackgroundChunksPerSession = 0;

        /// <summary>
        /// Минимум буфера впереди перед началом фоновой докачки.
        /// </summary>
        public const int MinBufferAheadForBackgroundFill = 4;

        // Preload Loop
        public const int PreloadIntervalMs = 500;
    }
}