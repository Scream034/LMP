using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Фабрика профилей стриминга для разных условий сети и устройства.
/// Приоритеты: быстрый старт → минимальный трафик → стабильность.
/// </summary>
public static class StreamingProfiles
{
    #region Profile Constants

    /// <summary>
    /// Профиль ЭКОНОМИИ ТРАФИКА.
    /// Мгновенный старт, качает только ~10 секунд вперёд.
    /// Идеален для мобильного интернета и лимитированных тарифов.
    /// </summary>
    private static class LowProfile
    {
        // Маленькие чанки = быстрый старт + меньше потерь при перемотке
        public const int ChunkSizeBytes = 32 * 1024;           // 32 KB - очень маленькие
        public const int ReadAheadChunks = 1;                  // Минимум
        public const int MaxRamChunks = 200;                   // Много мелких чанков в RAM

        // Минимум параллельных загрузок - не забиваем канал
        public const int MaxConcurrentDownloads = 2;
        public const int DownloadTimeoutMs = 30_000;
        public const int MaxRetries = 2;
        public const int RetryDelayMs = 500;

        // Минимальный буфер VLC - быстрый старт важнее
        public const int VlcNetworkCachingMs = 500;

        // МГНОВЕННЫЙ старт - только 1 чанк для начала
        public const int InitialBufferSeconds = 1;
        public const int InitialReadAheadChunks = 1;
        public const int HeaderChunks = 2;                     // Минимум для парсинга
        public const int TailChunks = 2;                       // Минимум для cues

        // СТРОЖАЙШИЙ throttling - качаем только ~10 секунд вперёд
        // При 128kbps: 10 сек = ~160KB = ~5 чанков по 32KB
        public const int MaxReadAheadFromPlayback = 3;         // VLC читает на 3 чанка вперёд
        public const int MaxDownloadAheadChunks = 5;           // Качаем только 5 чанков (~10 сек)
        public const int ChunksToKeepBehind = 1;               // Минимум позади

        // Редко расширяем - экономим CPU и сеть
        public const int BufferExtendIntervalMs = 2000;
        public const int SaveThresholdBytes = 32 * 1024;
    }

    /// <summary>
    /// СБАЛАНСИРОВАННЫЙ профиль (по умолчанию).
    /// Быстрый старт, качает ~30 секунд вперёд.
    /// Оптимален для обычного домашнего интернета.
    /// </summary>
    private static class MediumProfile
    {
        public const int ChunkSizeBytes = 128 * 1024;          // 128 KB
        public const int ReadAheadChunks = 2;
        public const int MaxRamChunks = 100;

        public const int MaxConcurrentDownloads = 3;
        public const int DownloadTimeoutMs = 30_000;
        public const int MaxRetries = 2;
        public const int RetryDelayMs = 400;

        // Небольшой буфер VLC
        public const int VlcNetworkCachingMs = 1000;

        // Быстрый старт - 2 секунды prebuffer
        public const int InitialBufferSeconds = 2;
        public const int InitialReadAheadChunks = 2;
        public const int HeaderChunks = 3;
        public const int TailChunks = 2;

        // Умеренный буфер - ~30 секунд вперёд
        // При 128kbps: 30 сек = ~480KB = ~4 чанка по 128KB
        public const int MaxReadAheadFromPlayback = 4;
        public const int MaxDownloadAheadChunks = 6;           // ~45 сек запас
        public const int ChunksToKeepBehind = 2;

        public const int BufferExtendIntervalMs = 1000;
        public const int SaveThresholdBytes = 64 * 1024;
    }

    /// <summary>
    /// Профиль ВЫСОКОГО КАЧЕСТВА.
    /// Быстрый старт, качает ~1.5 минуты вперёд.
    /// Для стабильного быстрого интернета.
    /// </summary>
    private static class HighProfile
    {
        public const int ChunkSizeBytes = 256 * 1024;          // 256 KB
        public const int ReadAheadChunks = 3;
        public const int MaxRamChunks = 80;

        public const int MaxConcurrentDownloads = 4;
        public const int DownloadTimeoutMs = 25_000;
        public const int MaxRetries = 2;
        public const int RetryDelayMs = 300;

        public const int VlcNetworkCachingMs = 800;

        // Быстрый старт - 3 секунды
        public const int InitialBufferSeconds = 3;
        public const int InitialReadAheadChunks = 2;
        public const int HeaderChunks = 3;
        public const int TailChunks = 2;

        // Комфортный буфер - ~1.5 минуты вперёд
        // При 128kbps: 90 сек = ~1440KB = ~6 чанков по 256KB
        public const int MaxReadAheadFromPlayback = 6;
        public const int MaxDownloadAheadChunks = 8;           // ~2 мин запас
        public const int ChunksToKeepBehind = 2;

        public const int BufferExtendIntervalMs = 500;
        public const int SaveThresholdBytes = 128 * 1024;
    }

    /// <summary>
    /// Профиль МАКСИМАЛЬНОГО кэширования.
    /// Быстрый старт, качает ~5 минут вперёд.
    /// Для локальной сети или гигабитного интернета.
    /// </summary>
    private static class UltraProfile
    {
        public const int ChunkSizeBytes = 512 * 1024;          // 512 KB
        public const int ReadAheadChunks = 5;
        public const int MaxRamChunks = 60;

        public const int MaxConcurrentDownloads = 6;
        public const int DownloadTimeoutMs = 20_000;
        public const int MaxRetries = 1;
        public const int RetryDelayMs = 200;

        public const int VlcNetworkCachingMs = 500;

        // Быстрый старт - 4 секунды
        public const int InitialBufferSeconds = 4;
        public const int InitialReadAheadChunks = 3;
        public const int HeaderChunks = 3;
        public const int TailChunks = 3;

        // Большой буфер - ~5 минут вперёд
        // При 128kbps: 300 сек = ~4800KB = ~10 чанков по 512KB
        public const int MaxReadAheadFromPlayback = 10;
        public const int MaxDownloadAheadChunks = 15;          // ~7.5 мин запас
        public const int ChunksToKeepBehind = 3;

        public const int BufferExtendIntervalMs = 300;
        public const int SaveThresholdBytes = 256 * 1024;
    }

    #endregion

    #region Factory

    /// <summary>
    /// Получает конфигурацию стриминга для указанного профиля.
    /// </summary>
    public static StreamingConfig GetConfig(InternetProfile profile) => profile switch
    {
        InternetProfile.Low => CreateLowConfig(),
        InternetProfile.Medium => CreateMediumConfig(),
        InternetProfile.High => CreateHighConfig(),
        InternetProfile.Ultra => CreateUltraConfig(),
        _ => CreateMediumConfig()
    };

    private static StreamingConfig CreateLowConfig() => new()
    {
        ChunkSizeBytes = LowProfile.ChunkSizeBytes,
        ReadAheadChunks = LowProfile.ReadAheadChunks,
        MaxRamChunks = LowProfile.MaxRamChunks,
        MaxConcurrentDownloads = LowProfile.MaxConcurrentDownloads,
        DownloadTimeoutMs = LowProfile.DownloadTimeoutMs,
        MaxRetries = LowProfile.MaxRetries,
        RetryDelayMs = LowProfile.RetryDelayMs,
        DownloadFullTrack = false,
        VlcNetworkCachingMs = LowProfile.VlcNetworkCachingMs,
        InitialBufferSeconds = LowProfile.InitialBufferSeconds,
        InitialReadAheadChunks = LowProfile.InitialReadAheadChunks,
        HeaderChunks = LowProfile.HeaderChunks,
        TailChunks = LowProfile.TailChunks,
        MaxReadAheadFromPlayback = LowProfile.MaxReadAheadFromPlayback,
        MaxDownloadAheadChunks = LowProfile.MaxDownloadAheadChunks,
        ChunksToKeepBehind = LowProfile.ChunksToKeepBehind,
        BufferExtendIntervalMs = LowProfile.BufferExtendIntervalMs,
        SaveThresholdBytes = LowProfile.SaveThresholdBytes
    };

    private static StreamingConfig CreateMediumConfig() => new()
    {
        ChunkSizeBytes = MediumProfile.ChunkSizeBytes,
        ReadAheadChunks = MediumProfile.ReadAheadChunks,
        MaxRamChunks = MediumProfile.MaxRamChunks,
        MaxConcurrentDownloads = MediumProfile.MaxConcurrentDownloads,
        DownloadTimeoutMs = MediumProfile.DownloadTimeoutMs,
        MaxRetries = MediumProfile.MaxRetries,
        RetryDelayMs = MediumProfile.RetryDelayMs,
        DownloadFullTrack = false,
        VlcNetworkCachingMs = MediumProfile.VlcNetworkCachingMs,
        InitialBufferSeconds = MediumProfile.InitialBufferSeconds,
        InitialReadAheadChunks = MediumProfile.InitialReadAheadChunks,
        HeaderChunks = MediumProfile.HeaderChunks,
        TailChunks = MediumProfile.TailChunks,
        MaxReadAheadFromPlayback = MediumProfile.MaxReadAheadFromPlayback,
        MaxDownloadAheadChunks = MediumProfile.MaxDownloadAheadChunks,
        ChunksToKeepBehind = MediumProfile.ChunksToKeepBehind,
        BufferExtendIntervalMs = MediumProfile.BufferExtendIntervalMs,
        SaveThresholdBytes = MediumProfile.SaveThresholdBytes
    };

    private static StreamingConfig CreateHighConfig() => new()
    {
        ChunkSizeBytes = HighProfile.ChunkSizeBytes,
        ReadAheadChunks = HighProfile.ReadAheadChunks,
        MaxRamChunks = HighProfile.MaxRamChunks,
        MaxConcurrentDownloads = HighProfile.MaxConcurrentDownloads,
        DownloadTimeoutMs = HighProfile.DownloadTimeoutMs,
        MaxRetries = HighProfile.MaxRetries,
        RetryDelayMs = HighProfile.RetryDelayMs,
        DownloadFullTrack = false,
        VlcNetworkCachingMs = HighProfile.VlcNetworkCachingMs,
        InitialBufferSeconds = HighProfile.InitialBufferSeconds,
        InitialReadAheadChunks = HighProfile.InitialReadAheadChunks,
        HeaderChunks = HighProfile.HeaderChunks,
        TailChunks = HighProfile.TailChunks,
        MaxReadAheadFromPlayback = HighProfile.MaxReadAheadFromPlayback,
        MaxDownloadAheadChunks = HighProfile.MaxDownloadAheadChunks,
        ChunksToKeepBehind = HighProfile.ChunksToKeepBehind,
        BufferExtendIntervalMs = HighProfile.BufferExtendIntervalMs,
        SaveThresholdBytes = HighProfile.SaveThresholdBytes
    };

    private static StreamingConfig CreateUltraConfig() => new()
    {
        ChunkSizeBytes = UltraProfile.ChunkSizeBytes,
        ReadAheadChunks = UltraProfile.ReadAheadChunks,
        MaxRamChunks = UltraProfile.MaxRamChunks,
        MaxConcurrentDownloads = UltraProfile.MaxConcurrentDownloads,
        DownloadTimeoutMs = UltraProfile.DownloadTimeoutMs,
        MaxRetries = UltraProfile.MaxRetries,
        RetryDelayMs = UltraProfile.RetryDelayMs,
        DownloadFullTrack = false,
        VlcNetworkCachingMs = UltraProfile.VlcNetworkCachingMs,
        InitialBufferSeconds = UltraProfile.InitialBufferSeconds,
        InitialReadAheadChunks = UltraProfile.InitialReadAheadChunks,
        HeaderChunks = UltraProfile.HeaderChunks,
        TailChunks = UltraProfile.TailChunks,
        MaxReadAheadFromPlayback = UltraProfile.MaxReadAheadFromPlayback,
        MaxDownloadAheadChunks = UltraProfile.MaxDownloadAheadChunks,
        ChunksToKeepBehind = UltraProfile.ChunksToKeepBehind,
        BufferExtendIntervalMs = UltraProfile.BufferExtendIntervalMs,
        SaveThresholdBytes = UltraProfile.SaveThresholdBytes
    };

    #endregion

    #region Helpers

    /// <summary>
    /// Вычисляет примерное количество чанков для указанного времени.
    /// </summary>
    public static int SecondsToChunks(StreamingConfig config, int seconds, int bitrateKbps = 128)
    {
        if (bitrateKbps <= 0) bitrateKbps = 128;
        var bytesPerSecond = bitrateKbps * 1000 / 8;
        var totalBytes = bytesPerSecond * seconds;
        return Math.Max(1, (int)Math.Ceiling((double)totalBytes / config.ChunkSizeBytes));
    }

    /// <summary>
    /// Вычисляет примерное время буфера в секундах.
    /// </summary>
    public static int EstimatedBufferSeconds(StreamingConfig config, int bitrateKbps = 128)
    {
        if (bitrateKbps <= 0) bitrateKbps = 128;
        var bytesPerSecond = bitrateKbps * 1000 / 8;
        var bufferBytes = config.MaxDownloadAheadChunks * config.ChunkSizeBytes;
        return (int)(bufferBytes / bytesPerSecond);
    }

    /// <summary>
    /// Вычисляет объём RAM для буферов при текущих настройках.
    /// </summary>
    public static int EstimatedRamUsageMb(StreamingConfig config) =>
        config.MaxRamChunks * config.ChunkSizeBytes / (1024 * 1024);

    /// <summary>
    /// Возвращает читабельное описание профиля.
    /// </summary>
    public static string GetProfileDescription(InternetProfile profile) => profile switch
    {
        InternetProfile.Low => "Экономия трафика (~10 сек буфер)",
        InternetProfile.Medium => "Сбалансированный (~30 сек буфер)",
        InternetProfile.High => "Высокое качество (~1.5 мин буфер)",
        InternetProfile.Ultra => "Максимальный (~5 мин буфер)",
        _ => "Неизвестный профиль"
    };

    /// <summary>
    /// Возвращает рекомендуемый профиль на основе скорости интернета.
    /// </summary>
    public static InternetProfile GetRecommendedProfile(double speedMbps) => speedMbps switch
    {
        < 1 => InternetProfile.Low,
        < 5 => InternetProfile.Medium,
        < 25 => InternetProfile.High,
        _ => InternetProfile.Ultra
    };

    #endregion
}