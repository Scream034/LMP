namespace LMP.Core.Services;

/// <summary>
/// Фабрика профилей стриминга для разных условий сети.
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
    /// Экономия трафика и высокая отзывчивость.
    /// </summary>
    public static StreamingConfig Low { get; } = new()
    {
        RequestAlignmentBytes = 8 * 1024,
        MinRequestSizeBytes = 8 * 1024,
        MaxRequestSizeBytes = 96 * 1024,

        MaxRamBytes = 4 * 1024 * 1024,
        RamEvictionWindowBytes = 768 * 1024,

        MaxConcurrentDownloads = 2,
        DownloadSlotTimeoutMs = 800,

        MaxNetworkRetries = 4,
        NetworkRetryBaseDelayMs = 800,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 5000,
        PostRefreshDelayMs = 800,

        TargetBufferMs = 10_000,
        InitialPrebufferBytes = 64 * 1024,
        SeekPreloadBytes = 96 * 1024,
        MinBufferAheadForBackgroundFillMs = 3000,

        BackgroundFillIdleCycles = 8,
        BackgroundFillIntervalMs = 5000,
        MaxBackgroundRequestsPerSession = 60,

        PreloadIntervalMs = 800,
        ThrottleMultiplier = 2.0
    };

    /// <summary>
    /// Сбалансированный профиль.
    /// </summary>
    public static StreamingConfig Medium { get; } = new()
    {
        RequestAlignmentBytes = 16 * 1024,
        MinRequestSizeBytes = 16 * 1024,
        MaxRequestSizeBytes = 384 * 1024,

        MaxRamBytes = 6 * 1024 * 1024,
        RamEvictionWindowBytes = 1536 * 1024,

        MaxConcurrentDownloads = 3,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 3,
        NetworkRetryBaseDelayMs = 500,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 3000,
        PostRefreshDelayMs = 500,

        TargetBufferMs = 12_000,
        InitialPrebufferBytes = 128 * 1024,
        SeekPreloadBytes = 256 * 1024,
        MinBufferAheadForBackgroundFillMs = 4000,

        BackgroundFillIdleCycles = 5,
        BackgroundFillIntervalMs = 3000,
        MaxBackgroundRequestsPerSession = 0,

        PreloadIntervalMs = 500,
        ThrottleMultiplier = 3.0
    };

    /// <summary>
    /// Профиль для быстрых и стабильных сетей.
    /// </summary>
    public static StreamingConfig High { get; } = new()
    {
        RequestAlignmentBytes = 32 * 1024,
        MinRequestSizeBytes = 32 * 1024,
        MaxRequestSizeBytes = 512 * 1024,

        MaxRamBytes = 8 * 1024 * 1024,
        RamEvictionWindowBytes = 2 * 1024 * 1024,

        MaxConcurrentDownloads = 4,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 400,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 2000,
        PostRefreshDelayMs = 300,

        TargetBufferMs = 15_000,
        InitialPrebufferBytes = 256 * 1024,
        SeekPreloadBytes = 384 * 1024,
        MinBufferAheadForBackgroundFillMs = 5000,

        BackgroundFillIdleCycles = 3,
        BackgroundFillIntervalMs = 1500,
        MaxBackgroundRequestsPerSession = 0,

        PreloadIntervalMs = 350,
        ThrottleMultiplier = 0
    };

    /// <summary>
    /// Профиль для максимально быстрого канала.
    /// </summary>
    public static StreamingConfig Ultra { get; } = new()
    {
        RequestAlignmentBytes = 64 * 1024,
        MinRequestSizeBytes = 64 * 1024,
        MaxRequestSizeBytes = 1024 * 1024,

        MaxRamBytes = 12 * 1024 * 1024,
        RamEvictionWindowBytes = 3 * 1024 * 1024,

        MaxConcurrentDownloads = 4,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 400,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 2000,
        PostRefreshDelayMs = 300,

        TargetBufferMs = 18_000,
        InitialPrebufferBytes = 512 * 1024,
        SeekPreloadBytes = 768 * 1024,
        MinBufferAheadForBackgroundFillMs = 6000,

        BackgroundFillIdleCycles = 3,
        BackgroundFillIntervalMs = 1500,
        MaxBackgroundRequestsPerSession = 0,

        PreloadIntervalMs = 300,
        ThrottleMultiplier = 0
    };
}