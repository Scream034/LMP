namespace LMP.Core.Models;

/// <summary>
/// Конфигурация потоковой передачи и буферизации аудио для range-based транспорта.
/// </summary>
public sealed record StreamingConfig
{
    #region Range Request Settings

    /// <summary>
    /// Выравнивание сетевых запросов и локального кэша в байтах.
    /// </summary>
    public int RequestAlignmentBytes { get; init; } = Defaults.RequestAlignmentBytes;

    /// <summary>
    /// Минимальный размер одного сетевого range-запроса в байтах.
    /// </summary>
    public int MinRequestSizeBytes { get; init; } = Defaults.MinRequestSizeBytes;

    /// <summary>
    /// Максимальный размер одного сетевого range-запроса в байтах.
    /// </summary>
    public int MaxRequestSizeBytes { get; init; } = Defaults.MaxRequestSizeBytes;

    #endregion

    #region RAM Cache

    /// <summary>
    /// Максимальный объём RAM-кэша в байтах.
    /// </summary>
    public int MaxRamBytes { get; init; } = Defaults.MaxRamBytes;

    /// <summary>
    /// Окно удержания RAM-кэша вокруг текущей позиции в байтах.
    /// </summary>
    public int RamEvictionWindowBytes { get; init; } = Defaults.RamEvictionWindowBytes;

    #endregion

    #region Download Settings

    /// <summary>Максимальное количество параллельных range-загрузок.</summary>
    public int MaxConcurrentDownloads { get; init; } = Defaults.MaxConcurrentDownloads;

    /// <summary>Таймаут ожидания слота загрузки (мс). 0 = без ожидания.</summary>
    public int DownloadSlotTimeoutMs { get; init; } = Defaults.DownloadSlotTimeoutMs;

    #endregion

    #region Retry / Resilience

    /// <summary>Максимум повторных попыток при сетевых ошибках.</summary>
    public int MaxNetworkRetries { get; init; } = Defaults.MaxNetworkRetries;

    /// <summary>Базовая задержка между retry (мс).</summary>
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

    #region Buffer Targets

    /// <summary>
    /// Целевой объём буфера вперёд от текущей позиции в миллисекундах.
    /// Используется MAPO-планировщиком.
    /// </summary>
    public int TargetBufferMs { get; init; } = Defaults.TargetBufferMs;

    /// <summary>
    /// Начальный объём данных в байтах, который желательно иметь перед стартом парсинга/декодирования.
    /// </summary>
    public int InitialPrebufferBytes { get; init; } = Defaults.InitialPrebufferBytes;

    /// <summary>
    /// Объём данных в байтах для best-effort предзагрузки вокруг seek-позиции.
    /// </summary>
    public int SeekPreloadBytes { get; init; } = Defaults.SeekPreloadBytes;

    /// <summary>
    /// Минимальный объём буфера вперёд в миллисекундах перед запуском фоновой докачки.
    /// </summary>
    public int MinBufferAheadForBackgroundFillMs { get; init; } = Defaults.MinBufferAheadForBackgroundFillMs;

    #endregion

    #region Background Fill

    /// <summary>Циклов простоя перед фоновой докачкой.</summary>
    public int BackgroundFillIdleCycles { get; init; } = Defaults.BackgroundFillIdleCycles;

    /// <summary>Пауза между фоновыми range-загрузками (мс).</summary>
    public int BackgroundFillIntervalMs { get; init; } = Defaults.BackgroundFillIntervalMs;

    /// <summary>Максимум фоновых range-запросов за сессию (0 = unlimited).</summary>
    public int MaxBackgroundRequestsPerSession { get; init; } = Defaults.MaxBackgroundRequestsPerSession;

    #endregion

    #region Preload Loop

    /// <summary>Интервал preload loop (мс).</summary>
    public int PreloadIntervalMs { get; init; } = Defaults.PreloadIntervalMs;

    #endregion

    #region Throttling

    /// <summary>
    /// Множитель скорости скачивания относительно битрейта аудио.
    /// 0 = без ограничений. 3.0 = скачиваем не быстрее 3× битрейта.
    /// Предотвращает burst-download, из-за которого YouTube дросселирует соединение.
    /// </summary>
    public double ThrottleMultiplier { get; init; } = Defaults.ThrottleMultiplier;

    #endregion

    /// <summary>
    /// Значения по умолчанию.
    /// </summary>
    public static class Defaults
    {
        public const int RequestAlignmentBytes = 16 * 1024;
        public const int MinRequestSizeBytes = 16 * 1024;
        public const int MaxRequestSizeBytes = 256 * 1024;

        public const int MaxRamBytes = 6 * 1024 * 1024;
        public const int RamEvictionWindowBytes = 1536 * 1024;

        public const int MaxConcurrentDownloads = 3;
        public const int DownloadSlotTimeoutMs = 300;

        public const int MaxNetworkRetries = 3;
        public const int NetworkRetryBaseDelayMs = 500;
        public const bool UseExponentialBackoff = true;
        public const int Max403BeforeCircuitBreak = 3;
        public const int RefreshCooldownMs = 3000;
        public const int PostRefreshDelayMs = 500;

        public const int TargetBufferMs = 12_000;
        public const int InitialPrebufferBytes = 96 * 1024;
        public const int SeekPreloadBytes = 192 * 1024;
        public const int MinBufferAheadForBackgroundFillMs = 4000;

        public const int BackgroundFillIdleCycles = 5;
        public const int BackgroundFillIntervalMs = 3000;
        public const int MaxBackgroundRequestsPerSession = 0;

        public const int PreloadIntervalMs = 500;

        public const double ThrottleMultiplier = 3.0;
    }
}