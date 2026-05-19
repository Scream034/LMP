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
    /// Экономия трафика. Маленькие чанки, минимальный буфер (~10 сек).
    /// Для мобильного интернета и лимитированных тарифов.
    /// </summary>
    public static StreamingConfig Low { get; } = new()
    {
        ChunkSizeBytes = 32 * 1024,          // 32 KB
        ReadAheadChunks = 2,
        MaxRamChunks = 128,
        RamEvictionDistance = 8,

        MaxConcurrentDownloads = 2,
        DownloadTimeoutMs = 30_000,
        DownloadSlotTimeoutMs = 800,

        MaxNetworkRetries = 4,               // Больше retry для нестабильной сети
        NetworkRetryBaseDelayMs = 800,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 5000,
        PostRefreshDelayMs = 800,

        InitialChunksToLoad = 2,
        SeekPreloadChunks = 2,

        BackgroundFillIdleCycles = 8,
        BackgroundFillIntervalMs = 5000,
        MaxBackgroundChunksPerSession = 30,
        MinBufferAheadForBackgroundFill = 2,

        PreloadIntervalMs = 1200
    };

    /// <summary>
    /// Сбалансированный (по умолчанию). ~30 сек буфер.
    /// Для обычного домашнего интернета.
    /// </summary>
    public static StreamingConfig Medium { get; } = new()
    {
        ChunkSizeBytes = 128 * 1024,         // 128 KB
        ReadAheadChunks = 4,
        MaxRamChunks = 64,
        RamEvictionDistance = 10,

        MaxConcurrentDownloads = 3,
        DownloadTimeoutMs = 15_000,
        DownloadSlotTimeoutMs = 500,

        MaxNetworkRetries = 3,
        NetworkRetryBaseDelayMs = 500,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 3000,
        PostRefreshDelayMs = 500,

        InitialChunksToLoad = 3,
        SeekPreloadChunks = 2,

        BackgroundFillIdleCycles = 5,
        BackgroundFillIntervalMs = 3000,
        MaxBackgroundChunksPerSession = 0,
        MinBufferAheadForBackgroundFill = 3,

        PreloadIntervalMs = 800
    };

    /// <summary>
    /// Высокое качество. ~1.5 мин буфер.
    /// Для стабильного быстрого интернета.
    /// </summary>
    public static StreamingConfig High { get; } = new()
    {
        ChunkSizeBytes = 256 * 1024,         // 256 KB
        ReadAheadChunks = 6,
        MaxRamChunks = 48,
        RamEvictionDistance = 12,

        MaxConcurrentDownloads = 4,
        DownloadTimeoutMs = 20_000,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 400,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 2000,
        PostRefreshDelayMs = 300,

        InitialChunksToLoad = 3,
        SeekPreloadChunks = 3,

        BackgroundFillIdleCycles = 3,
        BackgroundFillIntervalMs = 1500,
        MaxBackgroundChunksPerSession = 0,
        MinBufferAheadForBackgroundFill = 4,

        PreloadIntervalMs = 500
    };

    /// <summary>
    /// Максимальное кэширование. ~5 мин буфер.
    /// Для локальной сети или гигабитного интернета.
    /// </summary>
    public static StreamingConfig Ultra { get; } = new()
    {
        ChunkSizeBytes = 512 * 1024,         // 512 KB
        ReadAheadChunks = 10,
        MaxRamChunks = 40,
        RamEvictionDistance = 15,

        MaxConcurrentDownloads = 6,
        DownloadTimeoutMs = 15_000,
        DownloadSlotTimeoutMs = 200,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 300,
        UseExponentialBackoff = false,       // Быстрый retry, сеть надёжная
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 1500,
        PostRefreshDelayMs = 200,

        InitialChunksToLoad = 4,
        SeekPreloadChunks = 4,

        BackgroundFillIdleCycles = 2,
        BackgroundFillIntervalMs = 500,
        MaxBackgroundChunksPerSession = 0,
        MinBufferAheadForBackgroundFill = 6,

        PreloadIntervalMs = 300
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