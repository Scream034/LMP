namespace LMP.Core.Models;

/// <summary>
/// Конфигурация потоковой передачи и буферизации аудио.
/// </summary>
public sealed record StreamingConfig
{
    #region Chunk Settings

    /// <summary>Размер одного чанка в байтах.</summary>
    public int ChunkSizeBytes { get; init; } = Defaults.ChunkSizeBytes;

    /// <summary>Количество чанков, читаемых вперёд.</summary>
    public int ReadAheadChunks { get; init; } = Defaults.ReadAheadChunks;

    /// <summary>Максимальное количество чанков в RAM.</summary>
    public int MaxRamChunks { get; init; } = Defaults.MaxRamChunks;

    #endregion

    #region Download Settings

    /// <summary>Максимальное количество параллельных загрузок.</summary>
    public int MaxConcurrentDownloads { get; init; } = Defaults.MaxConcurrentDownloads;

    /// <summary>Таймаут загрузки чанка в мс.</summary>
    public int DownloadTimeoutMs { get; init; } = Defaults.DownloadTimeoutMs;

    /// <summary>Количество повторных попыток.</summary>
    public int MaxRetries { get; init; } = Defaults.MaxRetries;

    /// <summary>Задержка между повторами в мс.</summary>
    public int RetryDelayMs { get; init; } = Defaults.RetryDelayMs;

    /// <summary>Загружать весь трек сразу.</summary>
    public bool DownloadFullTrack { get; init; } = false;

    #endregion

    #region VLC Settings

    /// <summary>Размер сетевого кэша VLC в мс.</summary>
    public int VlcNetworkCachingMs { get; init; } = Defaults.VlcNetworkCachingMs;

    #endregion

    #region Pre-Buffer Settings

    /// <summary>Секунды буферизации до старта.</summary>
    public int InitialBufferSeconds { get; init; } = Defaults.InitialBufferSeconds;

    /// <summary>Максимум чанков до старта воспроизведения.</summary>
    public int InitialReadAheadChunks { get; init; } = Defaults.InitialReadAheadChunks;

    /// <summary>Количество header-чанков для парсинга.</summary>
    public int HeaderChunks { get; init; } = Defaults.HeaderChunks;

    /// <summary>Количество tail-чанков для cues.</summary>
    public int TailChunks { get; init; } = Defaults.TailChunks;

    #endregion

    #region Throttling

    /// <summary>Чанков вперёд от позиции воспроизведения.</summary>
    public int MaxReadAheadFromPlayback { get; init; } = Defaults.MaxReadAheadFromPlayback;

    /// <summary>Чанков качаем заранее.</summary>
    public int MaxDownloadAheadChunks { get; init; } = Defaults.MaxDownloadAheadChunks;

    /// <summary>Чанков позади для хранения в RAM.</summary>
    public int ChunksToKeepBehind { get; init; } = Defaults.ChunksToKeepBehind;

    #endregion

    #region Timing

    /// <summary>Интервал расширения буфера в мс.</summary>
    public int BufferExtendIntervalMs { get; init; } = Defaults.BufferExtendIntervalMs;

    /// <summary>Максимальное время блокировки Read() в мс.</summary>
    public int MaxReadBlockMs { get; init; } = Defaults.MaxReadBlockMs;

    /// <summary>Интервал опроса при ожидании данных.</summary>
    public int ReadPollIntervalMs { get; init; } = Defaults.ReadPollIntervalMs;

    /// <summary>Порог сохранения ranges на диск.</summary>
    public int SaveThresholdBytes { get; init; } = Defaults.SaveThresholdBytes;

    #endregion

    #region Concurrency

    /// <summary>Множитель для urgent-загрузок.</summary>
    public int UrgentBoostMultiplier { get; init; } = Defaults.UrgentBoostMultiplier;

    /// <summary>Ёмкость канала записи на диск.</summary>
    public int DiskChannelCapacity { get; init; } = Defaults.DiskChannelCapacity;

    #endregion

    /// <summary>
    /// Значения по умолчанию (Medium профиль).
    /// </summary>
    public static class Defaults
    {
        public const int ChunkSizeBytes = 128 * 1024;          // 128 KB
        public const int ReadAheadChunks = 2;
        public const int MaxRamChunks = 100;

        public const int MaxConcurrentDownloads = 3;
        public const int DownloadTimeoutMs = 30_000;
        public const int MaxRetries = 2;
        public const int RetryDelayMs = 400;

        public const int VlcNetworkCachingMs = 1000;

        public const int InitialBufferSeconds = 2;
        public const int InitialReadAheadChunks = 2;
        public const int HeaderChunks = 3;
        public const int TailChunks = 2;

        public const int MaxReadAheadFromPlayback = 4;
        public const int MaxDownloadAheadChunks = 6;
        public const int ChunksToKeepBehind = 2;

        public const int BufferExtendIntervalMs = 1000;
        public const int MaxReadBlockMs = 30_000;
        public const int ReadPollIntervalMs = 300;
        public const int SaveThresholdBytes = 64 * 1024;

        public const int UrgentBoostMultiplier = 2;
        public const int DiskChannelCapacity = 32;
    }
}