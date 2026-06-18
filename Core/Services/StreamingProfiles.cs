namespace LMP.Core.Services;

/// <summary>
/// Фабрика профилей стриминга для разных условий сети.
/// Каждый профиль — набор предварительно настроенных параметров <see cref="StreamingConfig"/>.
/// </summary>
public static class StreamingProfiles
{
    /// <summary>
    /// Получает конфигурацию стриминга для указанного профиля.
    /// </summary>
    public static StreamingConfig GetConfig(InternetProfile profile) => profile switch
    {
        InternetProfile.Low => Low,
        InternetProfile.Medium => Medium,
        InternetProfile.High => High,
        InternetProfile.Ultra => Ultra,
        _ => Medium
    };

    /// <summary>
    /// Экономия трафика и высокая скорость (смещено из старого Medium / Normal).
    /// Чанк 16 КБ дает минимальный seek latency и быстрый холодный старт при нестабильном соединении.
    /// </summary>
    public static StreamingConfig Low { get; } = new()
    {
        ChunkSizeBytes = 16 * 1024,
        ReadAheadChunks = 4,
        MaxRamChunks = 256,
        RamEvictionDistance = 16,

        MaxConcurrentDownloads = 2,
        DownloadTimeoutMs = 30_000,
        DownloadSlotTimeoutMs = 800,

        MaxNetworkRetries = 4,
        NetworkRetryBaseDelayMs = 800,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 5000,
        PostRefreshDelayMs = 800,

        InitialChunksToLoad = 4,
        SeekPreloadChunks = 6,

        BackgroundFillIdleCycles = 8,
        BackgroundFillIntervalMs = 5000,
        MaxBackgroundChunksPerSession = 60,
        MinBufferAheadForBackgroundFill = 3,

        PreloadIntervalMs = 800
    };

    /// <summary>
    /// Сбалансированный средний профиль Normal (смещено из старого High).
    /// Чанк 64 КБ — оптимальный баланс между накладными расходами HTTP-запросов и задержкой позиционирования.
    /// </summary>
    public static StreamingConfig Medium { get; } = new()
    {
        ChunkSizeBytes = 64 * 1024,
        ReadAheadChunks = 6,
        MaxRamChunks = 96,
        RamEvictionDistance = 14,

        MaxConcurrentDownloads = 3,
        DownloadTimeoutMs = 15_000,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 3,
        NetworkRetryBaseDelayMs = 500,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 3000,
        PostRefreshDelayMs = 500,

        InitialChunksToLoad = 4,
        SeekPreloadChunks = 6,

        BackgroundFillIdleCycles = 5,
        BackgroundFillIntervalMs = 3000,
        MaxBackgroundChunksPerSession = 0,
        MinBufferAheadForBackgroundFill = 4,

        PreloadIntervalMs = 500
    };

    /// <summary>
    /// Стабильное высокое качество (смещено из старого Ultra).
    /// Чанк 128 КБ ориентирован на непрерывное вещание на быстрых домашних и мобильных сетях.
    /// </summary>
    public static StreamingConfig High { get; } = new()
    {
        ChunkSizeBytes = 128 * 1024,
        ReadAheadChunks = 8,
        MaxRamChunks = 64,
        RamEvictionDistance = 16,

        MaxConcurrentDownloads = 4,
        DownloadTimeoutMs = 20_000,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 400,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 2000,
        PostRefreshDelayMs = 300,

        InitialChunksToLoad = 4,
        SeekPreloadChunks = 6,

        BackgroundFillIdleCycles = 3,
        BackgroundFillIntervalMs = 1500,
        MaxBackgroundChunksPerSession = 0,
        MinBufferAheadForBackgroundFill = 6,

        PreloadIntervalMs = 350
    };

    /// <summary>
    /// Сверхвысокое качество для гигабитного интернета и максимального сетевого throughput.
    /// Чанк 256 КБ обеспечивает минимальный overhead HTTP-заголовков.
    /// </summary>
    public static StreamingConfig Ultra { get; } = new()
    {
        ChunkSizeBytes = 256 * 1024,
        ReadAheadChunks = 10,
        MaxRamChunks = 48,
        RamEvictionDistance = 20,

        MaxConcurrentDownloads = 4,
        DownloadTimeoutMs = 25_000,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 400,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 2000,
        PostRefreshDelayMs = 300,

        InitialChunksToLoad = 4,
        SeekPreloadChunks = 6,

        BackgroundFillIdleCycles = 3,
        BackgroundFillIntervalMs = 1500,
        MaxBackgroundChunksPerSession = 0,
        MinBufferAheadForBackgroundFill = 8,

        PreloadIntervalMs = 300
    };
}