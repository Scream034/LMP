using LMP.Core.Models;

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
    /// Максимальное сбережение трафика (Ultra-Low Data Saver).
    /// Чанк 8 КБ обеспечивает моментальное начало воспроизведения при поиске и старте на медленных 2G/3G сетях.
    /// Загрузка строго в 1 поток исключает сетевые заторы и снижает потери пакетов.
    /// </summary>
    public static StreamingConfig Low { get; } = new()
    {
        ChunkSizeBytes = 8 * 1024,
        ReadAheadChunks = 3,
        MaxRamChunks = 512,
        RamEvictionDistance = 10,

        MaxConcurrentDownloads = 1,
        DownloadTimeoutMs = 45_000,
        DownloadSlotTimeoutMs = 1000,

        MaxNetworkRetries = 5,
        NetworkRetryBaseDelayMs = 1000,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 6000,
        PostRefreshDelayMs = 1000,

        InitialChunksToLoad = 3,
        SeekPreloadChunks = 4,

        BackgroundFillIdleCycles = 12,
        BackgroundFillIntervalMs = 8000,
        MaxBackgroundChunksPerSession = 40,
        MinBufferAheadForBackgroundFill = 2,

        PreloadIntervalMs = 1000
    };

    /// <summary>
    /// Экономия трафика и высокая скорость (бывший Low). 
    /// Чанк 16 КБ дает минимальный seek latency и быстрый холодный старт при нестабильном соединении.
    /// </summary>
    public static StreamingConfig Medium { get; } = new()
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
    /// Сбалансированный средний профиль (бывший Medium). 
    /// Чанк 64 КБ — оптимальный баланс между накладными расходами HTTP-запросов и задержкой позиционирования.
    /// </summary>
    public static StreamingConfig High { get; } = new()
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
    /// Стабильное высокое качество (бывший High). 
    /// Чанк 128 КБ ориентирован на непрерывное вещание на быстрых домашних и мобильных сетях.
    /// </summary>
    public static StreamingConfig Ultra { get; } = new()
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

    #region Helpers

    /// <summary>
    /// Вычисляет примерное время буфера в секундах.
    /// </summary>
    public static int EstimatedBufferSeconds(StreamingConfig config, int bitrateKbps = 128)
    {
        if (bitrateKbps <= 0) bitrateKbps = 128;
        long bufferBytes = (long)config.ReadAheadChunks * config.ChunkSizeBytes;
        return (int)(bufferBytes * 8 / (bitrateKbps * 1000));
    }

    /// <summary>
    /// Вычисляет объём RAM для буферов (МБ).
    /// </summary>
    public static int EstimatedRamUsageMb(StreamingConfig config) =>
        (int)((long)config.MaxRamChunks * config.ChunkSizeBytes / (1024 * 1024));

    /// <summary>
    /// Рекомендуемый профиль на основе скорости интернета.
    /// </summary>
    public static InternetProfile RecommendedProfile(double speedMbps) => speedMbps switch
    {
        < 1 => InternetProfile.Low,
        < 5 => InternetProfile.Medium,
        < 25 => InternetProfile.High,
        _ => InternetProfile.Ultra
    };

    #endregion
}