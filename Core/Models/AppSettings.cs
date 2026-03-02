namespace LMP.Core.Models;

public enum YoutubeClientProfile
{
    WebRemix,    // n + sig (Работают все ролики)
    AndroidVR,   // большинство роликов работают
    Web, // n + sig
    TV, // не работает
    Ios, // HLS в основном
    AndroidMusic // требует доп. действий
}

public enum InternetProfile
{
    Low,      // Экономия трафика / Медленный интернет
    Medium,   // Баланс (по умолчанию)
    High,     // Высокое качество / Быстрый интернет
    Ultra     // Максимальное кэширование / Локальная сеть
}

/// <summary>
/// Тип кривой интерполяции громкости.
/// </summary>
public enum VolumeCurveType
{
    /// <summary>Линейная: volume = t</summary>
    Linear,

    /// <summary>Квадратичная: volume = t² (по умолчанию, перцептивно линейная)</summary>
    Quadratic,

    /// <summary>Логарифмическая: volume = log2(1 + t) / log2(2)</summary>
    Logarithmic,

    /// <summary>Кубическая: volume = t³</summary>
    Cubic,

    /// <summary>
    /// "Скорость света": экспоненциальный рост в конце.
    /// Формула: volume = (e^(t*2) - 1) / (e² - 1)
    /// </summary>
    SpeedOfLight
}

/// <summary>
/// Поведение при критических ошибках воспроизведения.
/// </summary>
/// <remarks>
/// <para>Определяет реакцию системы на ошибки типа:</para>
/// <list type="bullet">
///   <item><see cref="Youtube.Exceptions.StreamUnavailableException"/> — стрим недоступен (403, geo-block)</item>
///   <item><see cref="Exceptions.ChunkDownloadFatalException"/> — фатальная ошибка загрузки чанков</item>
/// </list>
/// <para>Используется в <see cref="Services.PlaybackErrorOrchestrator"/>.</para>
/// </remarks>
public enum PlaybackErrorBehavior
{
    /// <summary>
    /// Показать модальный диалог и остановить воспроизведение.
    /// Требует явного действия пользователя.
    /// </summary>
    Dialog,

    /// <summary>
    /// Показать toast-уведомление и автоматически перейти к следующему треку.
    /// Также показывает OS-уведомление если приложение свёрнуто.
    /// </summary>
    ToastAndSkip,

    /// <summary>
    /// Игнорировать ошибку и автоматически перейти к следующему треку.
    /// Ошибка логируется, но пользователь не уведомляется.
    /// </summary>
    Ignore
}

public sealed class ProxySettings
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public bool UseAuth { get; set; } = false;
    public string Username { get; set; } = "";
    public string Password { get; set; } = ""; // В реальном приложении стоит шифровать
}

/// <summary>
/// Настройки хранения данных.
/// </summary>
public sealed class StorageSettings
{
    /// <summary>
    /// Лимит кэша изображений в МБ.
    /// </summary>
    public int ImageCacheLimitMb { get; set; } = 500;

    /// <summary>
    /// Лимит кэша аудио (StreamCache) в МБ.
    /// Старые файлы удаляются автоматически при превышении.
    /// </summary>
    public int AudioCacheLimitMb { get; set; } = 2048;

    public int DownloadedTracksLimitMb { get; set; } = 5000;

    /// <summary>
    /// Максимальное количество изображений в RAM-кэше.
    /// </summary>
    public int MaxBitmapCacheItems { get; set; } = 25;

    /// <summary>
    /// Автоматически сохранять полностью закэшированные треки в папку Downloads.
    /// По умолчанию выключено для экономии места (файл дублируется).
    /// </summary>
    public bool AutoSaveToDownloads { get; set; } = false;
}

/// <summary>
/// Настройки аудио системы.
/// </summary>
public sealed class AudioSettings
{
    /// <summary>
    /// Включить boost громкости выше 100%.
    /// Если false — MaxVolume просто увеличивает точность (больше шагов).
    /// </summary>
    public bool VolumeBoostEnabled { get; set; } = true;

    /// <summary>
    /// Кривая интерполяции громкости.
    /// </summary>
    public VolumeCurveType VolumeCurve { get; set; } = VolumeCurveType.Quadratic;

    /// <summary>
    /// Плавное изменение громкости (fade при изменении).
    /// </summary>
    public bool SmoothVolumeEnabled { get; set; } = false;

    /// <summary>
    /// Скорость плавного изменения громкости (мс на полный переход).
    /// </summary>
    public int SmoothVolumeDurationMs { get; set; } = 150;

    /// <summary>
    /// Нормализация громкости (выравнивание уровней между треками).
    /// </summary>
    public bool NormalizationEnabled { get; set; } = false;

    /// <summary>
    /// Целевой уровень нормализации в LUFS.
    /// </summary>
    public float NormalizationTargetLufs { get; set; } = -14f;

    /// <summary>
    /// Поведение при критических ошибках воспроизведения.
    /// </summary>
    /// <remarks>
    /// <para>Применяется к ошибкам:</para>
    /// <list type="bullet">
    ///   <item>StreamUnavailableException (403, geo-block, все клиенты failed)</item>
    ///   <item>ChunkDownloadFatalException (превышен лимит 403 при загрузке чанков)</item>
    /// </list>
    /// <para>НЕ применяется к:</para>
    /// <list type="bullet">
    ///   <item>BotDetectionException — всегда показывает диалог с таймером</item>
    ///   <item>LoginRequiredException — всегда показывает диалог (требует действия)</item>
    /// </list>
    /// </remarks>
    public PlaybackErrorBehavior CriticalErrorBehavior { get; set; } = PlaybackErrorBehavior.ToastAndSkip;

    /// <summary>
    /// Воспроизводить звук при ошибке воспроизведения.
    /// </summary>
    public bool PlayErrorSound { get; set; } = true;
}

public enum RepeatMode
{
    None,
    One,
    All
}

public enum AudioQualityPreference
{
    BestAvailable,
    Standard
}

/// <summary>
/// Application settings. Stored as JSON in Settings table.
/// </summary>
public sealed class AppSettings
{
    // === Audio ===
    public float Volume { get; set; } = 0.5f;
    public int LastVolume { get; set; } = 50;
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;
    public int MaxVolumeLimit { get; set; } = 100;
    public float TargetGainDb { get; set; } = 0f;
    public AudioQualityPreference QualityPreference { get; set; } = AudioQualityPreference.BestAvailable;
    public bool RememberTrackFormat { get; set; } = true;

    /// <summary>
    /// Расширенные настройки аудио.
    /// </summary>
    public AudioSettings Audio { get; set; } = new();

    // === Network ===
    public InternetProfile InternetProfile { get; set; } = InternetProfile.Medium;
    // Добавляем выбор клиента (по умолчанию VR, так как он сейчас работает)
    public YoutubeClientProfile YoutubeClient { get; set; } = YoutubeClientProfile.AndroidVR;
    public ProxySettings Proxy { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    // === UI ===
    public double PlaylistHeaderHeight { get; set; } = 320;
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;
    public int LoadBatchSize { get; set; } = 20;
    public int SearchBatchSize { get; set; } = 30;
    public bool EnableSearchCache { get; set; } = true;
    public int SearchCacheTtlMinutes { get; set; } = 120;
    public bool EnableSmoothLoading { get; set; } = true;

    /// <summary>
    /// Действие при нажатии кнопки закрытия окна.
    /// По умолчанию — спрашивать.
    /// </summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>
    /// Сворачивать в системный трей при нажатии кнопки минимизации.
    /// По умолчанию false — обычное сворачивание в панель задач.
    /// </summary>
    public bool MinimizeToTray { get; set; } = false;

    /// <summary>
    /// Ширина панели уведомлений.
    /// </summary>
    public int NotificationPanelWidth { get; set; } = 360;

    // === Search ===
    public string LastSearchQuery { get; set; } = "";
    public List<string> SearchHistory { get; set; } = [];

    /// <summary>
    /// Режим синхронизации лайков с YouTube.
    /// </summary>
    public LikeSyncMode LikeSyncMode { get; set; } = LikeSyncMode.MusicOnly;
}