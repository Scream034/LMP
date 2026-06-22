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

    #region Network Assessment

    /// <summary>
    /// Safety factor для throughput-based планирования размера запроса (0.0–1.0).
    /// <para>
    /// По модели THROUGHPUT (dash.js): запрашиваем не более этой доли оценённой
    /// пропускной способности. Значение 0.9 означает: использовать 90% от EMA-оценки,
    /// оставляя 10% запаса, чтобы не упираться в потолок канала и не конкурировать
    /// за bandwidth с параллельными запросами.
    /// </para>
    /// </summary>
    public double ThroughputSafetyFactor { get; init; } = Defaults.ThroughputSafetyFactor;

    /// <summary>
    /// Абсолютный нижний порог bandwidth (байт/сек), ниже которого сеть считается
    /// критически деградированной вне зависимости от relative ratio к битрейту.
    /// <para>
    /// Решает проблему: на узком канале (10 Мбит/с) с низкобитрейтным аудио (128 kbps)
    /// relative ratio ≈ 78 — система считает сеть «нормальной», хотя канал абсолютно узкий.
    /// </para>
    /// </summary>
    public int AbsoluteCriticalBandwidthBytesPerSec { get; init; } = Defaults.AbsoluteCriticalBandwidthBytesPerSec;

    /// <summary>
    /// Абсолютный нижний порог bandwidth (байт/сек), ниже которого сеть считается
    /// деградированной вне зависимости от relative ratio к битрейту.
    /// </summary>
    public int AbsoluteDegradedBandwidthBytesPerSec { get; init; } = Defaults.AbsoluteDegradedBandwidthBytesPerSec;

    /// <summary>
    /// Минимально жизнеспособный множитель bandwidth к битрейту аудио.
    /// Если <c>bandwidth &lt; bitrate × multiplier</c> — канал считается критическим
    /// (не обеспечивает даже минимального запаса над стримингом).
    /// </summary>
    public double MinViableBandwidthMultiplier { get; init; } = Defaults.MinViableBandwidthMultiplier;

    /// <summary>
    /// Максимальный уровень буфера вперёд (мс), при котором активируется BDP floor.
    /// <para>
    /// По BBA-принципу (Netflix/dash.js): BDP floor нужен только при пустом буфере
    /// (startup/seek фаза). В steady state (буфер выше этого порога) оценка
    /// пропускной способности не нужна — достаточно demand-based sizing.
    /// </para>
    /// </summary>
    public int BdpFloorMaxBufferMs { get; init; } = Defaults.BdpFloorMaxBufferMs;

    /// <summary>
    /// Количество первых замеров bandwidth, к которым применяется повышенный bootstrap weight.
    /// <para>
    /// Устраняет проблему медленной сходимости EMA на startup: первые N замеров
    /// используют <see cref="BandwidthBootstrapWeight"/> вместо size-based веса,
    /// что позволяет системе быстро выйти на реальную оценку канала.
    /// </para>
    /// </summary>
    public int BandwidthBootstrapSampleCount { get; init; } = Defaults.BandwidthBootstrapSampleCount;

    /// <summary>
    /// Вес EMA для bootstrap-замеров (первые <see cref="BandwidthBootstrapSampleCount"/> измерений).
    /// Должен быть выше steady-state веса для быстрой начальной сходимости.
    /// </summary>
    public double BandwidthBootstrapWeight { get; init; } = Defaults.BandwidthBootstrapWeight;

    #endregion

    #region Startup Pipeline

    /// <summary>
    /// Объём данных (байт) для немедленного фонового prefetch после инициализации parser.
    /// <para>
    /// Запускается параллельно с созданием decoder и warmup-проверкой,
    /// чтобы к моменту принятия решения о старте воспроизведения буфер уже был
    /// частично заполнен. Перекрывает задержку <see cref="PreloadIntervalMs"/>
    /// первой итерации preload loop.
    /// </para>
    /// <para>
    /// Рекомендуемое значение: достаточно для покрытия warmup-порога
    /// <c>(TargetBufferMs / 2)</c> секунд аудио при целевом битрейте.
    /// </para>
    /// </summary>
    public int StartupPrefetchBytes { get; init; } = Defaults.StartupPrefetchBytes;

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

        // Network Assessment

        /// <summary>
        /// 90% от оценённой пропускной способности (модель THROUGHPUT, dash.js).
        /// Оставляем 10% запаса, чтобы не упираться в потолок канала.
        /// </summary>
        public const double ThroughputSafetyFactor = 0.90;

        /// <summary>
        /// 128 KB/s ≈ 1 Мбит/с — абсолютный минимум для стабильного аудиостриминга.
        /// Ниже этого порога любой relative ratio будет вводить в заблуждение.
        /// </summary>
        public const int AbsoluteCriticalBandwidthBytesPerSec = 128 * 1024;

        /// <summary>
        /// 512 KB/s ≈ 4 Мбит/с — нижняя граница комфортного стриминга с буферизацией.
        /// </summary>
        public const int AbsoluteDegradedBandwidthBytesPerSec = 512 * 1024;

        /// <summary>
        /// Канал должен быть как минимум в 1.5× быстрее битрейта аудио, иначе
        /// воспроизведение без буферизации невозможно.
        /// </summary>
        public const double MinViableBandwidthMultiplier = 1.5;

        /// <summary>
        /// BDP floor активен только пока буфер меньше 4 секунд (startup/seek фаза).
        /// В steady state достаточно demand-based sizing без BDP-коррекции.
        /// </summary>
        public const int BdpFloorMaxBufferMs = 4_000;

        /// <summary>
        /// Первые 3 замера используют повышенный bootstrap weight для быстрой
        /// начальной сходимости EMA.
        /// </summary>
        public const int BandwidthBootstrapSampleCount = 3;

        /// <summary>
        /// 30% вес для bootstrap-замеров — в 6× выше минимального steady-state веса (5%),
        /// что обеспечивает быстрое приближение к реальной скорости канала.
        /// </summary>
        public const double BandwidthBootstrapWeight = 0.30;

        /// <summary>
        /// 128 KB — перекрывает ~5–7 секунд аудио на 128–192 kbps,
        /// что достаточно для warmup-порога на Medium профиле.
        /// </summary>
        public const int StartupPrefetchBytes = 128 * 1024;
    }
}