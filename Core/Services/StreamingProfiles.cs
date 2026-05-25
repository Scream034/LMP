using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Фабрика профилей стриминга для разных условий сети.
/// Каждый профиль — набор предварительно настроенных параметров <see cref="StreamingConfig"/>.
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
    /// Экономия трафика. Chunk 16KB — минимальный seek latency при медленном соединении.
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
    /// Сбалансированный (по умолчанию). Chunk 64KB — оптимум между seek latency и HTTP overhead.
    ///
    /// <para><b>Расчёт буфера:</b> 6 ReadAhead × 64KB = 384KB ≈ 20s @ 155kbps.
    /// Достаточно для бесперебойного воспроизведения на обычном домашнем интернете.
    /// Seek загружает 64KB вместо 128KB — perceived latency снижена в 2×.</para>
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
    /// Высокое качество. Chunk 128KB — баланс для быстрого интернета.
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
    /// Максимальное кэширование. Chunk 256KB — агрессивный prefetch.
    /// </summary>
    public static StreamingConfig Ultra { get; } = new()
    {
        ChunkSizeBytes = 256 * 1024,
        ReadAheadChunks = 12,
        MaxRamChunks = 48,
        RamEvictionDistance = 18,

        MaxConcurrentDownloads = 6,
        DownloadTimeoutMs = 15_000,
        DownloadSlotTimeoutMs = 200,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 300,
        UseExponentialBackoff = false,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 1500,
        PostRefreshDelayMs = 200,

        InitialChunksToLoad = 5,
        SeekPreloadChunks = 8,

        BackgroundFillIdleCycles = 2,
        BackgroundFillIntervalMs = 500,
        MaxBackgroundChunksPerSession = 0,
        MinBufferAheadForBackgroundFill = 8,

        PreloadIntervalMs = 200
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