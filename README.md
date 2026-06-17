<thinking>
Нам необходимо реализовать изоляцию сессий пользователей (мультиаккаунтинг) на уровне базы данных, реестров в памяти и UI-компонентов. 
Проанализируем узкие места и спроектируем решения:

1. **Идентификация аккаунтов (Identity Mapping)**:
   * **Проблема**: `AuthState.DisplayId` использует `UserEmail` в качестве ключа. При переключении на бренд-аккаунты под тем же Google-профилем email остается идентичным (или приходит пустым), из-за чего данные смешиваются под одним `OwnerId`.
   * **Решение**: Добавляем свойство `ActiveGaiaId` в `AuthState`. Переопределяем `DisplayId` так, чтобы приоритет отдавался `ActiveGaiaId` (уникальному идентификатору канала), затем `UserEmail`, и только в конце — `"guest"`.

2. **Состояние гонки (Race Condition) при смене аккаунта**:
   * **Проблема**: При смене аккаунта `LibraryService` сбрасывал реестр и запускал `HydrateAsync` в фоне без ожидания, мгновенно отправляя сигнал `OnDataChanged`. UI начинал перезагрузку до завершения гидрации базы.
   * **Решение**: Переводим `HandleAuthStateChanged` на асинхронное выполнение (`async void`), гарантируя полный `await _registry.HydrateAsync(...)`. Создаем новое событие `OnAccountHydrated` в `LibraryService`. `MainWindowViewModel` будет подписываться на него, выполняя очистку кэшей представлений и трансляцию `BroadcastAccountChanged` строго после готовности данных в SQLite.

3. **Изоляция истории поиска (Search History)**:
   * **Проблема**: Находится в глобальном `AppSettings` и шарится между всеми профилями.
   * **Решение**: Исключаем историю из глобального `AppSettings`. Сохраняем её в `ISettingsRepository` под ключом, динамически зависящим от `CurrentOwnerId` (`$"SearchHistory_{CurrentOwnerId}"`). Добавляем `List<string>` в контекст исходной генерации JSON (`AppJsonContext`) для сохранения AOT-совместимости и минимизации аллокаций.

4. **Активное обновление ViewModels при смене аккаунта**:
   * **Проблема**: Страницы поиска и главного экрана не реагировали на смену аккаунта в реальном времени, если они были активны.
   * **Решение**: В `HomeViewModel` переопределяем `OnAccountChanged` так, чтобы при активном экране происходил автоматический вызов `LoadTracksAsync(force: true)`. В `SearchViewModel` загрузка и сохранение истории переносятся на асинхронное взаимодействие с репозиторием, а при смене аккаунта происходит принудительный сброс и перезагрузка истории поиска.

5. **Блокировка навигации TitleBar во время сетевой авторизации**:
   * **Проблема**: Пользователь может нажать кнопки навигации в процессе подключения к YouTube.
   * **Решение**: В `AuthDialogViewModel.AuthenticateAsync` динамически извлекаем синглтон `MainWindowViewModel` и оборачиваем сетевые вызовы в `LockNavigation` / `UnlockNavigation`. Временно снимаем блокировку только при показе диалога выбора бренда, после чего блокируем навигацию снова до завершения валидации.

Приступим к реализации, строго следуя стандартам производительности, чистоты кода и сохранения всей XML-документации.
</thinking>

## Анализ
1. **Утечка плейлистов и треков (Identity Map Leak)**: Из-за того, что `AuthState.DisplayId` опирался исключительно на `UserEmail`, сессии основного аккаунта и связанных с ним каналов-брендов перекрывались. База данных SQLite считывала и записывала сущности под одним и тем же ключом владельца.
2. **Асинхронная гонка при гидрации**: Фоновый запуск `_registry.HydrateAsync` без ожидания приводил к тому, что UI запрашивал данные из SQLite до того, как они были вычитаны и закэшированы в ОЗУ.
3. **Глобальный стейт истории поиска**: Хранение истории поиска в общем конфиге `AppSettings` нарушало изоляцию персональных данных пользователей.
4. **Неактивное состояние экранов**: Отсутствие вызовов принудительной перезагрузки во `ViewModel` при открытом экране и смене профиля оставляло старый контент на экране.

## Варианты решения
1. **Создание отдельных SQLite-файлов для каждого аккаунта**:
   * *Плюсы*: Физическая изоляция файлов.
   * *Минусы*: Накладные расходы на создание пулов подключений, усложнение миграций, медленный старт при переключении.
2. **Использование уникального GaiaId + кэширование в единой SQLite (Выбранный вариант)**:
   * *Плюсы*: Абсолютная изоляция на уровне запросов `WHERE OwnerId = @ownerId`, сохранение единого пула подключений, мгновенный асинхронный переход, полная интеграция с существующей архитектурой репозиториев.

## План
1. **AppJsonContext**: Добавить поддержку сериализации `List<string>` без накладных расходов рефлексии.
2. **AuthState**: Добавить свойство `ActiveGaiaId` и переписать генерацию `DisplayId`.
3. **LibraryService**:
   * Реализовать событие `OnAccountHydrated` для устранения race condition.
   * Перевести обработчик изменения авторизации на последовательный `await`.
   * Вынести историю поиска в изолированное хранилище `$"SearchHistory_{CurrentOwnerId}"`.
4. **TrackViewModelFactory**: Реализовать очистку кэша представлений треков во избежание «утечки» старых состояний реактивных свойств.
5. **MainWindowViewModel**: Синхронизировать сброс фабрики представлений и запуск трансляции смены аккаунта по событию `OnAccountHydrated`.
6. **HomeViewModel & SearchViewModel**: Реализовать автоматическую перезагрузку истории, плиток и очистку стейта в реальном времени.
7. **AuthDialogViewModel**: Внедрить управление блокировкой навигации в процессе сетевого обмена с API YouTube.

## Код

### Модели и контекст сериализации

```csharp
// AppJsonContext.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Cache;
using LMP.Core.Models;

namespace LMP.Core.Models;

/// <summary>
/// JSON Source Generator для оптимизации памяти и производительности.
/// Убирает накладные расходы рефлексии при работе с моделями.
/// </summary>
[JsonSerializable(typeof(TrackInfo))]
[JsonSerializable(typeof(List<TrackInfo>))]
[JsonSerializable(typeof(Playlist))]
[JsonSerializable(typeof(List<Playlist>))]
[JsonSerializable(typeof(CacheEntry))]
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(BootstrapSettings))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(YoutubeAccountItem))]
[JsonSerializable(typeof(List<YoutubeAccountItem>))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    WriteIndented = false, 
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class AppJsonContext : JsonSerializerContext
{
    public static AppJsonContext DefaultCompact { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
}
```

```csharp
// AppSettings.cs
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Models;

public enum YoutubeClientProfile
{
    WebRemix,    // n + sig (Работают все ролики)
    AndroidVR,   // большинство роликов работают
    Web,         // n + sig
    TV,          // не работает
    Ios,         // HLS в основном
    AndroidMusic // требует доп. действий
}

public enum InternetProfile
{
    Low,    // Экономия трафика / Медленный интернет
    Medium, // Баланс (по умолчанию)
    High,   // Высокое качество / Быстрый интернет
    Ultra   // Максимальное кэширование / Локальная сеть
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

public sealed class ProxySettings
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public bool UseAuth { get; set; } = false;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
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
    /// Нормализация громкости (статическое выравнивание уровней, аналог Spotify/YouTube Music).
    /// </summary>
    public bool NormalizationEnabled { get; set; } = true;

    /// <summary>
    /// Целевой уровень нормализации в LUFS.
    /// Диапазон: -24 (тише) .. -6 (громче). По умолчанию -14 (стандарт Spotify/YouTube Music).
    /// </summary>
    public float NormalizationTargetLufs { get; set; } = -14f;

    /// <summary>
    /// Максимальный gain нормализации. Ограничивает усиление тихих треков.
    /// Диапазон: 1.0 (без усиления) .. 6.0. По умолчанию 3.0.
    /// </summary>
    public float NormalizationMaxGain { get; set; } = 3.0f;

    /// <summary>
    /// Режим нормализации громкости.
    /// <para><b>Bidirectional</b> (по умолчанию): усиливает тихие и понижает громкие треки.
    /// Обеспечивает одинаковую громкость при любом контенте.</para>
    /// <para><b>DownwardOnly</b>: только понижение громких треков, как на YouTube.
    /// Тихие треки воспроизводятся на исходном уровне.</para>
    /// </summary>
    public NormalizationMode NormalizationMode { get; set; } = NormalizationMode.Bidirectional;

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

    /// <summary>
    /// Пропускать треки, требующие сложной расшифровки n-токена.
    /// </summary>
    public bool SkipNTokenTracks { get; set; } = true;
}

/// <summary>
/// Настройки панели уведомлений: поведение, отображение и авто-очистка.
/// <para>
/// Единая точка конфигурации всего, что связано с уведомлениями.
/// <see cref="NotificationService"/> читает эти значения напрямую,
/// что устраняет жёсткие константы в коде сервиса.
/// </para>
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>
    /// Ширина панели уведомлений в пикселях.
    /// </summary>
    public int PanelWidth { get; set; } = 360;

    /// <summary>
    /// Максимум уведомлений в памяти и в панели.
    /// При превышении вытесняются самые старые.
    /// </summary>
    public int MaxInPanelCount { get; set; } = 50;

    /// <summary>
    /// Включить авто-очистку уведомлений по возрасту.
    /// </summary>
    public bool AutoCleanupEnabled { get; set; } = true;

    /// <summary>
    /// Возраст уведомления в часах, после которого оно удаляется автоматически.
    /// По умолчанию 48 ч (2 дня).
    /// </summary>
    public int AutoCleanupAfterHours { get; set; } = 48;

    /// <summary>
    /// Интервал фоновой проверки авто-очистки в минутах.
    /// </summary>
    public int CleanupCheckIntervalMinutes { get; set; } = 30;
}

/// <summary>
/// Настройки автоматической очистки памяти.
/// </summary>
public sealed class MemorySettings
{
    /// <summary>
    /// Включить автоматическую очистку памяти по таймеру.
    /// </summary>
    public bool AutoCleanupEnabled { get; set; } = true;

    /// <summary>
    /// Интервал автоочистки в минутах.
    /// </summary>
    public int AutoCleanupIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Порог памяти (MB) при превышении которого запускается агрессивная очистка.
    /// 0 = отключено.
    /// </summary>
    public int PressureThresholdMb { get; set; } = 400;
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
    public YoutubeClientProfile YoutubeClient { get; set; } = YoutubeClientProfile.AndroidVR;
    public ProxySettings Proxy { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    // === UI ===
    public double PlaylistHeaderHeight { get; set; } = 300;
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;
    public int LoadBatchSize { get; set; } = 20;
    public int SearchBatchSize { get; set; } = 30;
    public bool EnableSearchCache { get; set; } = true;
    public int SearchCacheTtlMinutes { get; set; } = 120;

    /// <summary>
    /// Оптимизировать UI при потере фокуса окна.
    /// 
    /// <para><b>true:</b> При сворачивании или потере фокуса приостанавливаются
    /// тяжёлые UI-обновления (позиция, буфер, анимации). Экономит CPU.</para>
    /// 
    /// <para><b>false:</b> UI работает штатно даже без фокуса.
    /// Полезно для пользователей со вторым монитором.</para>
    /// 
    /// <para>Настройка НЕ влияет на сворачивание в tray — там всегда оптимизация.</para>
    /// </summary>
    public bool OptimizeWhenInactive { get; set; } = true;

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
    /// Настройки панели уведомлений и авто-очистки.
    /// </summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>
    /// Режим синхронизации лайков с YouTube.
    /// </summary>
    public LikeSyncMode LikeSyncMode { get; set; } = LikeSyncMode.MusicOnly;

    /// <summary>
    /// Настройки управления памятью.
    /// </summary>
    public MemorySettings Memory { get; set; } = new();
}
```

```csharp
// AuthState.cs
namespace LMP.Core.Models;

/// <summary>
/// Представляет состояние авторизации пользователя Google/YouTube.
/// </summary>
public sealed class AuthState
{
    public const string DefaultAuthUser = "0";

    private string _userName = "Guest";

    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Отображаемое имя пользователя. Автоматически возвращает локализованное имя гостя из ресурсов при отсутствии сессии.
    /// </summary>
    public string UserName
    {
        get => IsAuthenticated ? _userName : LocalizationService.Instance["Auth_Guest"];
        set => _userName = value;
    }

    public string UserEmail { get; set; } = "";
    public string AvatarUrl { get; set; } = "";

    /// <summary>
    /// Идентификатор активного аккаунта YouTube (Gaia ID) для точечной изоляции.
    /// </summary>
    public string ActiveGaiaId { get; set; } = string.Empty;

    /// <summary>
    /// Индекс сессии мульти-авторизации Google активного аккаунта.
    /// По умолчанию пустая строка — это позволяет доверять флагу IsSelected от YouTube 
    /// при первичном входе через расширение.
    /// </summary>
    public string AuthUser { get; set; } = DefaultAuthUser;

    /// <summary>
    /// Кэшированный список каналов пользователя для мгновенного оффлайн-переключения.
    /// </summary>
    public List<YoutubeAccountItem> CachedAccounts { get; set; } = [];

    /// <summary>
    /// Уникальный идентификатор текущего профиля для изоляции данных в БД.
    /// Использует Gaia ID при наличии, иначе email, с безусловным fallback на гостя.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayId => !string.IsNullOrEmpty(ActiveGaiaId) 
        ? ActiveGaiaId 
        : (!string.IsNullOrEmpty(UserEmail) ? UserEmail : "guest");

    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}
```

### Сервисы и управление транзакциями данных

```csharp
// LibraryService.cs
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using LMP.Core.Models;
using LMP.Core.Youtube.Utils;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace LMP.Core.Services;

/// <summary>
/// Главный сервис библиотеки с SQLite-персистентностью и поддержкой мультиаккаунтов.
/// </summary>
public sealed class LibraryService : IAsyncDisposable
{
    public const string LikedPlaylistId = "liked";

    private readonly TrackRegistry _registry;
    private readonly ITrackRepository _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly ISettingsRepository _settings;
    private readonly IDbContextFactory<LibraryDbContext> _dbFactory;
    private readonly CookieAuthService _auth;

    private readonly Subject<Unit> _saveSettingsSignal = new();
    private readonly IDisposable _saveSubscription;

    public AppSettings Settings { get; private set; } = new();

    public event Action? OnDataChanged;
    public event Action<TrackInfo>? OnTrackUpdated;
    public event Action<Playlist>? OnPlaylistChanged;
    public event Action<string>? OnPlaylistRemoved;

    /// <summary>
    /// Событие, сигнализирующее о завершении полной асинхронной гидрации кэшей после смены аккаунта.
    /// </summary>
    public event Action? OnAccountHydrated;

    private string CurrentOwnerId => _auth.State.DisplayId;

    public LibraryService(
        TrackRegistry registry,
        ITrackRepository tracks,
        IPlaylistRepository playlists,
        ISettingsRepository settings,
        IDbContextFactory<LibraryDbContext> dbFactory,
        CookieAuthService auth)
    {
        _registry = registry;
        _tracks = tracks;
        _playlists = playlists;
        _settings = settings;
        _dbFactory = dbFactory;
        _auth = auth;

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        _auth.OnAuthStateChanged += HandleAuthStateChanged;

        _saveSubscription = _saveSettingsSignal
            .Throttle(TimeSpan.FromSeconds(2))
            .ObserveOn(RxSchedulers.TaskpoolScheduler)
            .Subscribe(async _ =>
            {
                try { await _settings.SetAsync("AppSettings", Settings); }
                catch (Exception ex) { Log.Error($"[LibraryService] Settings save failed: {ex.Message}"); }
            });
    }

    private async void HandleAuthStateChanged()
    {
        try
        {
            // Сбрасываем L1 кэш в оперативной памяти во избежание смешивания лайков
            _registry.Clear();
            await _registry.HydrateAsync(CancellationToken.None).ConfigureAwait(false);
            OnAccountHydrated?.Invoke();
            OnDataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"[LibraryService] Hydration failed during auth state shift: {ex.Message}");
        }
    }

    #region Инициализация

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Settings = await _settings.GetOrDefaultAsync("AppSettings", new AppSettings(), ct);
        YoutubeClientUtils.CurrentProfile = Settings.YoutubeClient;

        var jsonPath = G.FilePath.Library;
        if (File.Exists(jsonPath))
        {
            await MigrateFromJsonAsync(jsonPath, ct);
        }

        await _registry.HydrateAsync(ct);
        _registry.SubscribeToCacheEvents();

        sw.Stop();
        Log.Info($"[LibraryService] Initialized in {sw.ElapsedMilliseconds}ms");
    }

    private async Task MigrateFromJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            Log.Info("[Migration] Starting JSON -> SQLite migration...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var json = await File.ReadAllTextAsync(path, ct);
            var legacy = JsonSerializer.Deserialize<LegacyLibraryData>(json);
            if (legacy == null)
            {
                Log.Warn("[Migration] Could not deserialize legacy data");
                return;
            }

            var migratedTrackIds = new HashSet<string>();

            if (legacy.Tracks?.Count > 0)
            {
                int migrated = 0;
                int failed = 0;

                foreach (var track in legacy.Tracks.Values)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(track.Id)) continue;
                        await _tracks.UpsertAsync(track, ct);
                        migratedTrackIds.Add(track.Id);
                        migrated++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Warn($"[Migration] Failed to migrate track {track.Id}: {ex.Message}");
                    }
                }
                Log.Info($"[Migration] Migrated {migrated} tracks ({failed} failed)");
            }

            if (legacy.Playlists?.Count > 0)
            {
                int playlistsMigrated = 0;
                int totalTracksAdded = 0;
                int totalTracksMissing = 0;

                foreach (var legacyPl in legacy.Playlists.Values)
                {
                    try
                    {
                        var playlist = legacyPl.ToPlaylist();
                        playlist.OwnerId = CurrentOwnerId;
                        await _playlists.UpsertAsync(playlist, ct);
                        playlistsMigrated++;

                        var validTrackIds = legacyPl.TrackIds
                            .Where(migratedTrackIds.Contains)
                            .ToList();

                        var missingCount = legacyPl.TrackIds.Count - validTrackIds.Count;
                        if (missingCount > 0)
                        {
                            totalTracksMissing += missingCount;
                        }

                        var added = await _playlists.AddTracksAsync(playlist.Id, validTrackIds, CurrentOwnerId, ct);
                        totalTracksAdded += added;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Migration] Failed to migrate playlist {legacyPl.Name}: {ex.Message}");
                    }
                }

                Log.Info($"[Migration] Migrated {playlistsMigrated} playlists, added {totalTracksAdded} track links");
            }

            if (legacy.RecentlyPlayedIds?.Count > 0)
            {
                int historyAdded = 0;
                foreach (var id in legacy.RecentlyPlayedIds.AsEnumerable().Reverse().Take(100))
                {
                    try
                    {
                        if (migratedTrackIds.Contains(id))
                        {
                            await _tracks.AddToHistoryAsync(id, CurrentOwnerId, ct);
                            historyAdded++;
                        }
                    }
                    catch { }
                }
                Log.Info($"[Migration] Added {historyAdded} history entries");
            }

            Settings = MapLegacySettings(legacy);
            await _settings.SetAsync("AppSettings", Settings, ct);

            var backup = path + $".migrated.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup);

            sw.Stop();
            Log.Info($"[Migration] Complete in {sw.ElapsedMilliseconds}ms. Backup: {backup}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Migration] Failed: {ex.Message}");
        }
    }

    private static AppSettings MapLegacySettings(LegacyLibraryData d) => new()
    {
        Volume = d.Volume,
        LastVolume = d.LastVolume,
        ShuffleEnabled = d.ShuffleEnabled,
        RepeatMode = d.RepeatMode,
        MaxVolumeLimit = d.MaxVolumeLimit,
        TargetGainDb = d.TargetGainDb,
        QualityPreference = d.QualityPreference,
        RememberTrackFormat = d.RememberTrackFormat,
        InternetProfile = d.InternetProfile,
        LanguageCode = d.LanguageCode,
        DownloadPath = d.DownloadPath,
        DiscordRpcEnabled = d.DiscordRpcEnabled,
        AutoPlayOnUrlPaste = d.AutoPlayOnUrlPaste,
        LoadBatchSize = d.LoadBatchSize,
        SearchBatchSize = d.SearchBatchSize,
        EnableSearchCache = d.EnableSearchCache,
        SearchCacheTtlMinutes = d.SearchCacheTtlMinutes,
        PlaylistHeaderHeight = d.PlaylistHeaderHeight
    };

    #endregion

    #region Треки

    public async Task AddOrUpdateTrackAsync(TrackInfo track, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, CurrentOwnerId, ct);
        var canonical = _registry.RegisterOrUpdate(track);
        await _tracks.UpsertAsync(canonical, ct);
        OnTrackUpdated?.Invoke(canonical);
    }

    public TrackInfo? GetTrack(string id) => _registry.TryGet(id);

    public async Task<TrackInfo?> GetTrackAsync(string id, CancellationToken ct = default)
    {
        return await _registry.GetOrLoadAsync(id, ct);
    }

    public bool HasTrack(string id) => _registry.TryGet(id) != null;

    public async Task<List<TrackInfo>> SearchTracksAsync(
     string query, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.SearchAsync(query, CurrentOwnerId, limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, CurrentOwnerId, ct);

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<TimeSpan> GetPlaylistTotalDurationAsync(string playlistId, CancellationToken ct = default)
    {
        var totalTicks = await _playlists.GetTotalDurationTicksAsync(playlistId, CurrentOwnerId, ct);
        return TimeSpan.FromTicks(totalTicks);
    }

    public async Task<long> GetTotalLibraryDurationAsync(CancellationToken ct = default)
    {
        return await _playlists.GetTotalLibraryDurationAsync(CurrentOwnerId, ct);
    }

    public async Task<List<TrackInfo>> GetAllTracksAsync(
        int limit = 10000,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tracks = await _tracks.GetAllAsync(CurrentOwnerId, limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, CurrentOwnerId, ct);

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<List<TrackInfo>> GetLocalTracksAsync(
        int limit = 1000,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLocalTracksAsync(CurrentOwnerId, limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, CurrentOwnerId, ct);

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<int> GetTrackCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountAsync(ct);
    }

    public async Task<int> GetLocalTrackCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountLocalAsync(ct);
    }

    public async Task<List<TrackInfo>> SearchLocalTracksAsync(
     string query,
     int limit = 100,
     CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetLocalTracksAsync(limit, 0, ct);

        var allLocal = await _tracks.GetLocalTracksAsync(CurrentOwnerId, limit * 2, 0, ct);

        var filtered = allLocal
            .Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        if (filtered.Count == 0) return filtered;

        var trackIds = filtered.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, CurrentOwnerId, ct);

        for (int i = 0; i < filtered.Count; i++)
        {
            var t = filtered[i];
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return filtered;
    }

    /// <summary>Сохраняет вычисленный gain нормализации трека в БД.</summary>
    public Task SaveTrackNormalizationGainAsync(string trackId, float gain, CancellationToken ct = default) =>
        _tracks.SaveNormalizationGainAsync(trackId, gain, ct);

    #endregion

    #region История

    public async Task AddToRecentlyPlayedAsync(TrackInfo track, CancellationToken ct = default)
    {
        await AddOrUpdateTrackAsync(track, ct);
        await _tracks.AddToHistoryAsync(track.Id, CurrentOwnerId, ct);
    }

    public async Task<List<TrackInfo>> GetRecentlyPlayedAsync(int count = 20, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetRecentlyPlayedAsync(CurrentOwnerId, count, ct);
        for (int i = 0; i < tracks.Count; i++) 
            _registry.RegisterOrUpdate(tracks[i]);
        return tracks;
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await _tracks.ClearHistoryAsync(CurrentOwnerId, ct);
        OnDataChanged?.Invoke();
    }

    #endregion

    #region Лайки

    public async Task SetLikeStateAsync(TrackInfo track, bool isLiked, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, CurrentOwnerId, ct);
        var canonical = _registry.RegisterOrUpdate(track);

        if (canonical.IsLiked == isLiked)
        {
            Log.Debug($"[LibraryService] Like state already {isLiked} for {track.Id}");
            return;
        }

        canonical.IsLiked = isLiked;
        if (isLiked) canonical.IsDisliked = false;

        await _tracks.UpsertAsync(canonical, ct);

        if (isLiked)
        {
            await _playlists.AddTrackAsync(LikedPlaylistId, canonical.Id, CurrentOwnerId, 0, ct);
            canonical.InPlaylists.Add(LikedPlaylistId);
        }
        else
        {
            await _playlists.RemoveTrackAsync(LikedPlaylistId, canonical.Id, CurrentOwnerId, ct);
            canonical.InPlaylists.Remove(LikedPlaylistId);
        }

        _registry.UpdatePinStatus(canonical);

        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(canonical);
    }

    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, CurrentOwnerId, ct);
        var canonical = _registry.RegisterOrUpdate(track);
        await SetLikeStateAsync(track, !canonical.IsLiked, ct);
    }

    public async Task ToggleDislikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        var canonical = _registry.RegisterOrUpdate(track);
        canonical.IsDisliked = !canonical.IsDisliked;

        if (canonical.IsDisliked)
        {
            canonical.IsLiked = false;
            await _playlists.RemoveTrackAsync(LikedPlaylistId, canonical.Id, CurrentOwnerId, ct);
            canonical.InPlaylists.Remove(LikedPlaylistId);
        }

        await _tracks.UpsertAsync(canonical, ct);
        _registry.UpdatePinStatus(canonical);

        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(canonical);
    }

    public async Task<List<TrackInfo>> GetLikedTracksAsync(
     int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLikedAsync(CurrentOwnerId, limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, CurrentOwnerId, ct);

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<int> GetLikedCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountLikedAsync(CurrentOwnerId, ct);
    }

    #endregion

    #region Плейлисты

    public async Task<string?> GetSetVideoIdAsync(
        string playlistId, string trackId, CancellationToken ct = default)
    {
        return await _playlists.GetSetVideoIdAsync(playlistId, trackId, ct);
    }

    public async Task UpdateSetVideoIdAsync(
        string playlistId, string trackId, string setVideoId, CancellationToken ct = default)
    {
        await _playlists.UpdateSetVideoIdAsync(playlistId, trackId, setVideoId, ct);
    }

    public async Task UpdateSetVideoIdsAsync(
        string playlistId,
        IReadOnlyList<(string TrackId, string SetVideoId)> mappings,
        CancellationToken ct = default)
    {
        await _playlists.UpdateSetVideoIdsAsync(playlistId, mappings, ct);
    }

    public async Task<List<string>> GetPlaylistTrackIdsAsync(string playlistId, CancellationToken ct = default)
    {
        return await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct);
    }

    public async Task<(Playlist Playlist, int TrackCount)?> GetPlaylistWithCountAsync(
        string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return null;

        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct);
        return (playlist, trackIds.Count);
    }

    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllPlaylistsWithCountsAsync(CancellationToken ct = default)
    {
        var results = await _playlists.GetAllWithCountsAsync(CurrentOwnerId, ct);

        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Playlist.Id == LikedPlaylistId)
            {
                var pl = results[i].Playlist;
                pl.Name = LocalizationService.Instance["Playlist_Liked"];
                results[i] = (pl, results[i].TrackCount);
            }
        }

        return results;
    }

    public async Task<Playlist?> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        var pl = await _playlists.GetByIdAsync(id, CurrentOwnerId, ct);
        if (pl != null && id == LikedPlaylistId)
        {
            pl.Name = LocalizationService.Instance["Playlist_Liked"];
        }
        return pl;
    }

    public async Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
    {
        return (await _playlists.GetByIdAsync(LikedPlaylistId, CurrentOwnerId, ct))!;
    }

    public async Task<List<Playlist>> GetAllPlaylistsAsync(CancellationToken ct = default)
    {
        var all = await _playlists.GetAllAsync(CurrentOwnerId, ct);
        var liked = all.FirstOrDefault(p => p.Id == LikedPlaylistId);
        if (liked != null)
        {
            liked.Name = LocalizationService.Instance["Playlist_Liked"];
        }
        return all;
    }

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(
        string playlistId, CancellationToken ct = default)
    {
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct);
        if (trackIds.Count == 0) return [];

        await _registry.PreloadAsync(trackIds, ct);

        var tracks = new List<TrackInfo>(trackIds.Count);
        for (int i = 0; i < trackIds.Count; i++)
        {
            var track = _registry.TryGet(trackIds[i]);
            if (track != null) tracks.Add(track);
        }

        return tracks;
    }

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(
        string playlistId, int limit, int offset = 0, CancellationToken ct = default)
    {
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, limit, offset, ct);
        if (trackIds.Count == 0) return [];

        await _registry.PreloadAsync(trackIds, ct);

        var tracks = new List<TrackInfo>(trackIds.Count);
        for (int i = 0; i < trackIds.Count; i++)
        {
            var track = _registry.TryGet(trackIds[i]);
            if (track != null) tracks.Add(track);
        }

        return tracks;
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        var playlist = new Playlist { Name = name, SyncMode = PlaylistSyncMode.LocalOnly, OwnerId = CurrentOwnerId };
        await _playlists.UpsertAsync(playlist, ct);
        OnDataChanged?.Invoke();
        return playlist;
    }

    public async Task AddOrUpdatePlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        playlist.OwnerId = CurrentOwnerId;
        await _playlists.UpsertAsync(playlist, ct);

        if (playlist.TrackIds.Count > 0)
        {
            var existingTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct);
            var existingSet = new HashSet<string>(existingTrackIds, StringComparer.Ordinal);

            var newTrackIds = playlist.TrackIds.Where(id => !existingSet.Contains(id)).ToList();
            if (newTrackIds.Count > 0)
            {
                await _playlists.AddTracksAsync(playlist.Id, newTrackIds, CurrentOwnerId, ct);
                Log.Debug($"[LibraryService] Add {newTrackIds.Count} tracks into playlist '{playlist.Name}'");
            }
        }

        OnPlaylistChanged?.Invoke(playlist);
        OnDataChanged?.Invoke();
    }

    public async Task AddTrackToPlaylistAsync(TrackInfo track, string playlistId, CancellationToken ct = default)
    {
        await AddOrUpdateTrackAsync(track, ct);
        await _playlists.AddTrackAsync(playlistId, track.Id, CurrentOwnerId, null, ct);
        track.InPlaylists.Add(playlistId);
        _registry.UpdatePinStatus(track);
        OnDataChanged?.Invoke();
    }

    public async Task RemoveTrackFromPlaylistAsync(string trackId, string playlistId, CancellationToken ct = default)
    {
        await _playlists.RemoveTrackAsync(playlistId, trackId, CurrentOwnerId, ct);
        var track = _registry.TryGet(trackId);
        if (track != null)
        {
            track.InPlaylists.Remove(playlistId);
            _registry.UpdatePinStatus(track);
        }
        OnDataChanged?.Invoke();
    }

    public async Task MoveTrackInPlaylistAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        await _playlists.MoveTrackAsync(playlistId, oldIndex, newIndex, ct);
        OnDataChanged?.Invoke();
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default)
    {
        if (IsSystemPlaylist(playlistId)) return;
        await _playlists.RenameAsync(playlistId, newName, ct);
        OnDataChanged?.Invoke();
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        if (IsSystemPlaylist(playlistId)) return;

        foreach (var track in _registry.GetPinnedTracks())
            track.InPlaylists.Remove(playlistId);

        await _playlists.DeleteAsync(playlistId, ct);

        OnPlaylistRemoved?.Invoke(playlistId);
        OnDataChanged?.Invoke();
    }

    public async Task<bool> IsTrackInPlaylistAsync(string trackId, string playlistId, CancellationToken ct = default)
    {
        return await _playlists.ContainsTrackAsync(playlistId, trackId, CurrentOwnerId, ct);
    }

    public static bool IsSystemPlaylist(string id) => id == LikedPlaylistId;

    #endregion

    #region Настройки

    public string DownloadPath
    {
        get => string.IsNullOrEmpty(Settings.DownloadPath) ? G.Folder.Downloads : Settings.DownloadPath;
        set { Settings.DownloadPath = value; SaveSettings(); }
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        update(Settings);
        SaveSettings();
    }

    private void SaveSettings() => _saveSettingsSignal.OnNext(Unit.Default);

    /// <summary>
    /// Извлекает историю поиска текущего пользователя из изолированной БД-таблицы параметров.
    /// </summary>
    public async Task<List<string>> GetSearchHistoryAsync(CancellationToken ct = default)
    {
        var key = $"SearchHistory_{CurrentOwnerId}";
        return await _settings.GetOrDefaultAsync(key, new List<string>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Сохраняет историю поиска текущего пользователя в изолированную БД-таблицу параметров.
    /// </summary>
    public async Task SaveSearchHistoryAsync(List<string> history, CancellationToken ct = default)
    {
        var key = $"SearchHistory_{CurrentOwnerId}";
        await _settings.SetAsync(key, history, ct).ConfigureAwait(false);
    }

    #endregion

    #region События

    private void OnLanguageChanged(object? _, string __)
    {
        OnDataChanged?.Invoke();
    }

    #endregion

    #region Очистка и завершение

    public async Task ResetAsync(CancellationToken ct = default)
    {
        _registry.Clear();

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        await ctx.Database.EnsureDeletedAsync(ct);
        await ctx.Database.EnsureCreatedAsync(ct);
        await ctx.OptimizeAsync(ct);
        await ctx.EnsureFtsTablesAsync(ct);

        Settings = new AppSettings();
        OnDataChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        _auth.OnAuthStateChanged -= HandleAuthStateChanged;
        _saveSubscription.Dispose();
        _saveSettingsSignal.Dispose();

        await _registry.FlushAsync();
        await _settings.SetAsync("AppSettings", Settings);

        GC.SuppressFinalize(this);

        Log.Info("Disposed");
    }

    #endregion
}
```

```csharp
// CookieAuthService.cs (Частичное обновление)
// Добавляем сохранение Gaia ID при смене профиля. В методе, обновляющем профиль:
public void UpdateUserProfile(string name, string email, string avatarUrl, string? gaiaId = null)
{
    State.UserName = name;
    State.UserEmail = email;
    State.AvatarUrl = avatarUrl;
    State.ActiveGaiaId = gaiaId ?? string.Empty;
    State.LastUpdated = DateTime.UtcNow;

    SaveStateToDisk();
    
    // Вызывает OnAuthStateChanged
    TriggerAuthStateChanged(); 
}
```

```csharp
// TrackRegistry.cs (Внутренние изменения)
// Убедимся, что Clear() полностью очищает коллекции ОЗУ при смене профилей.
public void Clear()
{
    _cache.Clear();
    _pinned.Clear();
    Log.Debug("[TrackRegistry] Memory caches successfully cleared on profile transition.");
}
```

### Реализация пользовательского интерфейса (ViewModels)

```csharp
// HomeViewModel.cs
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;

namespace LMP.UI.Features.Home;

/// <summary>
/// ViewModel главного экрана. Категории + поиск через YouTube с кэшированием.
/// </summary>
public sealed class HomeViewModel : TrackListReorderableViewModel
{
    #region Constants

    private const int DefaultFetchSize = 100;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentQuery = "";
    private CancellationTokenSource? _categoryCts;
    private bool _isDisposed;
    private bool _isDataLoaded; // Признак того, что данные уже лежат в кэше ОЗУ

    #endregion

    #region Properties

    [Reactive] public string Greeting { get; private set; } = string.Empty;
    [Reactive] public CategoryItem? SelectedCategory { get; set; }

    public ObservableCollection<CategoryItem> Categories { get; } = [];

    public bool CanReorderItems => CanReorder;

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<(int oldIndex, int newIndex), Unit> MoveItemCommand { get; }

    #endregion

    #region Constructor

    public HomeViewModel(
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory)
        : base(audio, downloads, vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        UpdateGreeting();

        _languageChangedHandler = (_, _) =>
        {
            UpdateGreeting();
            InitializeCategories();
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        InitializeCategories();

        RefreshCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(async () => await LoadTracksAsync(force: true)));

        MoveItemCommand = CreateCommand(
            ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(
                async tuple => await MoveItemAsync(tuple.oldIndex, tuple.newIndex)));

        this.WhenAnyValue(x => x.FilterQuery)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(CanReorderItems)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedCategory)
            .WhereNotNull()
            .Skip(1)
            .Where(_ => !IsLoading) // Использовать IsLoading базового класса
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await LoadTracksAsync())
            .DisposeWith(Disposables);
    }

    #endregion

    #region Navigation

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        // Если данные уже в памяти, загрузку не запускаем — базовый класс 
        // автоматически переключит IsLoading в false по окончании перехода.
        if (!_isDataLoaded)
        {
            await LoadTracksAsync();
            _isDataLoaded = true;
        }

        await base.OnNavigatedToAsync(); // Запуск базового перехватчика
    }

    #endregion

    #region TrackListReorderableViewModel Implementation

    protected override void OnPlay(TrackInfo track)
    {
        Task.Run(async () => await Audio.PlayTrackAsync(track));
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    protected override async Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentQuery)) return [];

        var cached = await _searchCache.GetAsync(_currentQuery, SearchSource.YouTube, 30);
        if (cached is { Count: > 0 })
        {
            var idSet = ids.ToHashSet();
            return [.. cached.Where(t => idSet.Contains(t.Id))];
        }

        return [];
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();
        _isDataLoaded = false;

        // Если страница главного экрана активна прямо сейчас, принудительно запускаем обновление
        if (ViewModelBase.CurrentSuspendLevel == SuspendLevel.None)
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () => await LoadTracksAsync(force: true), DispatcherPriority.Background);
        }
    }

    #endregion

    #region Private Methods

    private async Task LoadTracksAsync(bool force = false)
    {
        var category = SelectedCategory;
        if (category is null) return;

        _categoryCts?.Cancel();
        _categoryCts?.Dispose();
        _categoryCts = new CancellationTokenSource();
        var ct = _categoryCts.Token;

        IsLoading = true;

        try
        {
            await Task.Delay(50, ct);

            if (category.IsSpecial)
            {
                var recent = await LibService.GetRecentlyPlayedAsync(DefaultFetchSize);
                if (ct.IsCancellationRequested) return;
                InitializeWithData(recent);
            }
            else
            {
                _currentQuery = category.Query;

                var cached = !force
                    ? await _searchCache.GetAsync(_currentQuery, SearchSource.YouTube, 30)
                    : null;

                List<TrackInfo> tracks;

                if (cached is { Count: > 0 })
                {
                    tracks = cached;
                    _ = RefreshCacheInBackgroundAsync(ct);
                }
                else
                {
                    tracks = await _youtube.SearchAsync(_currentQuery, DefaultFetchSize);
                    if (ct.IsCancellationRequested) return;

                    if (tracks.Count > 0)
                        _ = _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, tracks);
                }

                var imageUrls = tracks.Take(20)
                    .Select(static t => t.ThumbnailUrl)
                    .Where(static u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                if (ct.IsCancellationRequested) return;
                InitializeWithData(tracks);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[HomeVM] Load error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshCacheInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(3000, ct);
            var fresh = await _youtube.SearchAsync(_currentQuery, DefaultFetchSize);
            if (ct.IsCancellationRequested || fresh.Count == 0) return;
            await _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, fresh);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[HomeVM] Background cache refresh failed: {ex.Message}");
        }
    }

    private void UpdateGreeting()
    {
        var key = DateTime.Now.Hour switch
        {
            >= 0 and < 5 => "Home_Greeting_Night",
            >= 5 and < 12 => "Home_Greeting_Morning",
            >= 12 and < 18 => "Home_Greeting_Afternoon",
            _ => "Home_Greeting_Evening"
        };
        Greeting = SL[key];
    }

    private void InitializeCategories()
    {
        ReadOnlySpan<(string key, string fallback, string query, bool special)> defs =
        [
            ("Category_RecentlyPlayed", "Recently Played", "",                         true),
            ("Category_Trending",       "Trending",        "trending music 2025",      false),
            ("Category_Pop",            "Pop",             "pop hits 2025",            false),
            ("Category_HipHop",         "Hip-Hop",         "hip hop 2025",             false),
            ("Category_Electronic",     "Electronic",      "electronic music",         false),
            ("Category_LoFi",           "Lo-Fi",           "lofi hip hop chill beats", false),
            ("Category_Rock",           "Rock",            "rock music",               false),
        ];

        for (int i = Categories.Count; i < defs.Length; i++)
            Categories.Add(new CategoryItem());

        for (int i = 0; i < defs.Length; i++)
        {
            var (key, fallback, query, special) = defs[i];
            var name = SL[key];
            if (string.IsNullOrEmpty(name) || name == key) name = fallback;

            var cat = Categories[i];
            cat.Name = name;
            cat.Query = query;
            cat.IsSpecial = special;
            cat.LocKey = key;
        }

        while (Categories.Count > defs.Length)
            Categories.RemoveAt(Categories.Count - 1);

        SelectedCategory ??= Categories.FirstOrDefault();
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[HomeVM] Disposing");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _categoryCts?.Cancel();
            _categoryCts?.Dispose();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}
```

```csharp
// SearchViewModel.cs
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Diagnostics;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Avalonia.Threading;

namespace LMP.UI.Features.Search;

/// <summary>
/// Обертка истории поиска для O(1) доступа к командам без поиска по визуальному дереву.
/// Предотвращает ошибки "Value is null" при анимации затухания.
/// </summary>
public sealed record SearchHistoryItem(string Query, SearchViewModel Owner);

/// <summary>
/// ViewModel экрана поиска треков.
///
/// <para><b>Источники:</b> YouTube Music, YouTube, Local (библиотека).</para>
///
/// <para><b>Стратегия загрузки:</b>
/// <list type="bullet">
///   <item>Local — <see cref="LibraryService.SearchLocalTracksAsync"/>, без сети.</item>
///   <item>DirectUrl — одиночный трек через <see cref="YoutubeProvider.GetTrackByUrlAsync"/>.</item>
///   <item>Playlist URL — все треки через <see cref="YoutubeProvider.GetPlaylistAsync"/>.</item>
///   <item>Текстовый запрос — кэш → сеть, InfiniteScroll через <see cref="YoutubeProvider.SearchSession"/>.</item>
/// </list></para>
///
/// <para><b>Smart Parent</b> (активный трек, прогресс загрузки) унаследован от
/// <see cref="TrackListPaginatedViewModel"/>: нет N подписок на каждую TrackItemViewModel.</para>
/// </summary>
public sealed class SearchViewModel : TrackListPaginatedViewModel
{
    #region Constants

    private const int DebounceMs = 300;
    private const int MaxResults = 300;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;

    private string _currentQuery = "";
    private CancellationTokenSource? _searchCts;
    private YoutubeProvider.SearchSession? _searchSession;
    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool _isDisposed;

    #endregion

    #region Properties

    private int InitialBatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize * 2
        : 50;

    private int ScrollBatchSize => LibService.Settings.SearchBatchSize > 0
        ? LibService.Settings.SearchBatchSize
        : 30;

    [Reactive] public string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// Источник поиска: YouTube Music (музыка), YouTube (всё), Local (локальные).
    /// </summary>
    [Reactive] public ContentSource Source { get; set; } = ContentSource.YouTubeMusic;

    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }
    [Reactive] public bool IsFromCache { get; private set; }
    [Reactive] public bool IsOfflineMode { get; private set; }

    /// <summary>
    /// Кнопка принудительного обновления: видна только при наличии кэшированных результатов.
    /// </summary>
    public bool ShowForceSearchButton =>
        LibService.Settings.EnableSearchCache && IsFromCache && !IsLoading;

    /// <summary>
    /// История поиска. Обернута в SearchHistoryItem для предотвращения 
    /// binding exceptions при teardown'е страницы.
    /// </summary>
    public ObservableCollection<SearchHistoryItem> RecentSearches { get; } = [];

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceSearchCommand { get; }
    public ReactiveCommand<string, Unit> HistoryClickCommand { get; }
    public ReactiveCommand<string, Unit> RemoveHistoryCommand { get; }
    public ReactiveCommand<string, Unit> SetSourceCommand { get; }

    #endregion

    #region Constructor

    public SearchViewModel(
      AudioEngine audio,
      DownloadService downloads,
      TrackViewModelFactory vmFactory,
      YoutubeProvider youtube,
      SearchCacheService searchCache,
      ImageCacheService imageCache)
      : base(audio, downloads, vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        var canSearch = this.WhenAnyValue(
            x => x.SearchQuery, x => x.IsLoading,
            static (q, loading) => !string.IsNullOrWhiteSpace(q) && !loading);

        SearchCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: false),
            canSearch));

        var canForceSearch = this.WhenAnyValue(
            x => x.IsFromCache, x => x.IsLoading,
            static (cache, loading) => cache && !loading);

        ForceSearchCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: true),
            canForceSearch));

        HistoryClickCommand = CreateCommand(ReactiveCommand.CreateFromTask<string>(async q =>
        {
            if (_isDisposed || string.IsNullOrEmpty(q)) return;
            SearchQuery = q;
            await ExecuteSearchAsync(false);
        }));

        RemoveHistoryCommand = CreateCommand(ReactiveCommand.Create<string>(q =>
        {
            if (_isDisposed) return;
            var itemToRemove = RecentSearches.FirstOrDefault(x => x.Query == q);
            if (itemToRemove != null)
                RecentSearches.Remove(itemToRemove);

            UpdateHistoryStorage();
        }));

        SetSourceCommand = CreateCommand(ReactiveCommand.Create<string>(sourceStr =>
        {
            if (_isDisposed) return;
            if (Enum.TryParse<ContentSource>(sourceStr, true, out var result))
                Source = result;
        }));

        this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowForceSearchButton)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.Source)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async source =>
            {
                if (_isDisposed) return;
                IsOfflineMode = source == ContentSource.Local;

                if (!string.IsNullOrWhiteSpace(SearchQuery))
                    await ExecuteSearchAsync(forceNetwork: false);
            })
            .DisposeWith(Disposables);

        // Отключаем стартовый скелетон для пустой страницы поиска,
        // так как базовая VM инициализирует списки со статусом IsLoading = true
        IsLoading = false;
    }

    #endregion

    #region Navigation

    /// <summary>
    /// Вызывается после завершения анимации перехода.
    /// Запускает перехватчик из базового класса для плавного отображения контента.
    /// </summary>
    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        await LoadHistoryAsync();
        await base.OnNavigatedToAsync(); // Запуск базового перехватчика
    }

    #endregion

    #region TrackListPaginatedViewModel Implementation

    /// <summary>
    /// Запускает воспроизведение из контекста поиска.
    /// StartQueueAsync формирует очередь из одного трека с автоматическим докачиванием.
    /// </summary>
    protected override void OnPlay(TrackInfo track)
    {
        if (_isDisposed) return;
        _ = Task.Run(async () => await Audio.StartQueueAsync([track], track));
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    /// <summary>
    /// InfiniteScroll: загружает следующую порцию через активную <see cref="YoutubeProvider.SearchSession"/>.
    /// Для Local-источника InfiniteScroll отключён (<see cref="InitializeItemsAsync"/> canFetchMore=false).
    /// </summary>
    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (_isDisposed || Source == ContentSource.Local || TotalCount >= MaxResults)
            return [];

        var currentFilter = GetSearchFilter();

        if (_searchSession != null && _searchSession.Filter != currentFilter)
        {
            _searchSession.Dispose();
            _searchSession = null;
        }

        if (_searchSession == null && !string.IsNullOrEmpty(_currentQuery))
        {
            var existingIds = GetLoadedItemsIds();
            _searchSession = _youtube.CreateSearchSession(
                _currentQuery, MaxResults, currentFilter, existingIds);
        }

        if (_searchSession == null || !_searchSession.HasMore) return [];

        try
        {
            var newTracks = await _searchSession.FetchNextBatchAsync(ScrollBatchSize, ct);
            if (ct.IsCancellationRequested || _isDisposed) return [];

            if (Source == ContentSource.YouTubeMusic)
                foreach (var t in newTracks) t.IsMusic = true;

            if (newTracks.Count > 0)
            {
                AudioSourceFactory.GlobalCache?.HydrateCacheStatus(newTracks);

                if (LibService.Settings.EnableSearchCache)
                {
                    var snapshot = GetItemsSnapshot();
                    var all = new List<TrackInfo>(snapshot.Count + newTracks.Count);
                    all.AddRange(snapshot);
                    all.AddRange(newTracks);
                    _ = _searchCache.SetAsync(_currentQuery, SourceToSearchSource(), all);

                    var imageUrls = newTracks.Take(10)
                        .Select(static t => t.ThumbnailUrl)
                        .Where(static u => !string.IsNullOrEmpty(u));
                    _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                }
            }

            // Если постраничный запрос вернул 0 результатов, принудительно останавливаем дальнейшую пагинацию.
            if (newTracks.Count == 0 || !_searchSession.HasMore)
                SetCanFetchMore(false);

            return newTracks;
        }
        catch (OperationCanceledException) { return []; }
        catch (HttpRequestException) { ErrorMessage = SL["Search_NetworkError"]; return []; }
        catch (Exception ex)
        {
            Log.Error($"[Search] FetchMore error: {ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        // Полностью очищаем стейт поиска старого аккаунта во избежание утечки приватности
        SearchQuery = string.Empty;
        _currentQuery = string.Empty;
        ErrorMessage = null;
        IsFromCache = false;
        HasResults = false;

        try
        {
            _searchSession?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Search] Error disposing search session on account change: {ex.Message}");
        }
        _searchSession = null;

        ClearItems();

        _ = Dispatcher.UIThread.InvokeAsync(LoadHistoryAsync, DispatcherPriority.Background);

        Log.Info("[Search] Search state and account history successfully synchronized.");
    }

    #endregion

    #region Search Logic

    private SearchFilter GetSearchFilter() => Source switch
    {
        ContentSource.YouTubeMusic => SearchFilter.MusicSong,
        ContentSource.YouTube => SearchFilter.Video,
        ContentSource.Local => SearchFilter.None,
        _ => SearchFilter.MusicSong
    };

    private SearchSource SourceToSearchSource() => Source switch
    {
        ContentSource.YouTubeMusic => SearchSource.YouTubeMusic,
        ContentSource.YouTube => SearchSource.YouTube,
        _ => SearchSource.YouTube
    };

    private bool CanExecuteSearch()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSearchTime).TotalMilliseconds < DebounceMs) return false;
        _lastSearchTime = now;
        return true;
    }

    private async Task ExecuteSearchAsync(bool forceNetwork)
    {
        if (_isDisposed) return;
        if (!forceNetwork && !CanExecuteSearch()) return;

        CancellationTokenSource? currentCts = null;

        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            currentCts = _searchCts;
            var ct = currentCts.Token;

            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;

            CancelLoading();
            IsLoading = true;
            ErrorMessage = null;
            IsFromCache = false;
            HasResults = false;

            ClearItems();

            _currentQuery = SearchQuery.Trim();

            try
            {
                AddToHistory(_currentQuery);
            }
            catch (Exception ex)
            {
                Log.Error($"[Search] History save error: {ex.Message}");
            }

            await Task.Delay(50, ct);

            if (Source == ContentSource.Local)
            {
                await HandleLocalSearchAsync(ct);
                return;
            }

            var queryType = YoutubeProvider.DetectQueryType(_currentQuery);

            if (queryType == QueryType.DirectUrl)
                await HandleDirectUrlAsync(ct);
            else if (queryType == QueryType.Playlist)
                await HandlePlaylistAsync(ct);
            else
                await HandleSearchAsync(ct, forceNetwork);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                ErrorMessage = ex.Message;
                Log.Error($"[Search] Error executing search: {ex}");
            }
        }
        finally
        {
            if (!_isDisposed
                && currentCts == _searchCts
                && currentCts != null
                && !currentCts.IsCancellationRequested)
            {
                IsLoading = false;
                IsFetchingFromNetwork = false;
            }
        }
    }

    private async Task HandleLocalSearchAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        Log.Debug($"[Search] Local search: '{_currentQuery}'");

        var filtered = string.IsNullOrWhiteSpace(_currentQuery)
            ? await LibService.GetLocalTracksAsync(MaxResults, 0, ct)
            : await LibService.SearchLocalTracksAsync(_currentQuery, MaxResults, ct);

        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        await InitializeItemsAsync(filtered, canFetchMore: false);
        HasResults = filtered.Count > 0;

        if (!HasResults)
        {
            var localCount = await LibService.GetLocalTrackCountAsync(ct);
            ErrorMessage = localCount == 0
                ? SL["Search_NoLocalFiles"]
                : SL["Search_NoResults"];
        }

        Log.Info($"[Search] Local: {filtered.Count} results");
    }

    private async Task HandleDirectUrlAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        var tracks = track != null ? [track] : new List<TrackInfo>();
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (track != null && LibService.Settings.AutoPlayOnUrlPaste)
            _ = Task.Run(async () => await Audio.PlayTrackAsync(track), ct);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandlePlaylistAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        IsFetchingFromNetwork = true;
        var playlist = await _youtube.GetPlaylistAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        var tracks = playlist?.Tracks ?? [];
        IsFetchingFromNetwork = false;
        await InitializeItemsAsync(tracks, canFetchMore: false);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandleSearchAsync(CancellationToken ct, bool forceNetwork)
    {
        if (_isDisposed) return;

        var sw = Stopwatch.StartNew();
        var cacheSource = SourceToSearchSource();
        bool useCache = !forceNetwork && LibService.Settings.EnableSearchCache;

        if (useCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, cacheSource, minCount: 20);
            ct.ThrowIfCancellationRequested();
            if (_isDisposed) return;

            if (cached is { Count: >= 20 })
            {
                IsFromCache = true;
                await InitializeItemsAsync(cached, canFetchMore: cached.Count < MaxResults);
                HasResults = true;

                var urls = cached.Take(20).Select(static t => t.ThumbnailUrl);
                _ = _imageCache.PrefetchAsync(urls!, ct);

                Log.Debug($"[Search] Cache hit: {cached.Count} items in {sw.ElapsedMilliseconds}ms");
                return;
            }
        }

        IsFetchingFromNetwork = true;
        IsFromCache = false;

        if (forceNetwork)
            _searchCache.InvalidateQuery(_currentQuery, cacheSource);

        var (tracks, session) = await _youtube.SearchWithSessionAsync(
            _currentQuery, InitialBatchSize, MaxResults, GetSearchFilter(), ct);

        if (_isDisposed) return;

        _searchSession = session;

        if (Source == ContentSource.YouTubeMusic)
            foreach (var t in tracks) t.IsMusic = true;

        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        IsFetchingFromNetwork = false;

        if (tracks.Count > 0 && LibService.Settings.EnableSearchCache)
        {
            _ = _searchCache.SetAsync(_currentQuery, cacheSource, tracks);
            var urls = tracks.Take(20).Select(static t => t.ThumbnailUrl);
            _ = _imageCache.PrefetchAsync(urls!, ct);
        }

        // Если первичный поиск вернул 0 результатов, принудительно запрещаем пагинацию 
        // для предотвращения бесконечного скролл-шторма запросов.
        bool hasMore = (tracks.Count > 0) && (session?.HasMore ?? false);
        await InitializeItemsAsync(tracks, canFetchMore: hasMore);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];

        sw.Stop();
        Log.Info($"[Search] {tracks.Count} results in {sw.ElapsedMilliseconds}ms, hasMore={hasMore}");
    }
    #endregion

    #region History

    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await LibService.GetSearchHistoryAsync();
            RecentSearches.Clear();
            foreach (var query in history)
            {
                RecentSearches.Add(new SearchHistoryItem(query, this));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Search] Failed to load search history on navigate: {ex.Message}");
        }
    }

    private void AddToHistory(string query)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(query)) return;

        var existing = RecentSearches.FirstOrDefault(x => x.Query == query);
        if (existing != null) RecentSearches.Remove(existing);

        RecentSearches.Insert(0, new SearchHistoryItem(query, this));

        while (RecentSearches.Count > 10) RecentSearches.RemoveAt(RecentSearches.Count - 1);
        UpdateHistoryStorage();
    }

    private void UpdateHistoryStorage()
    {
        if (_isDisposed) return;
        var historyStrings = RecentSearches.Select(x => x.Query).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await LibService.SaveSearchHistoryAsync(historyStrings);
            }
            catch (Exception ex)
            {
                Log.Error($"[Search] Failed to commit search history storage: {ex.Message}");
            }
        });
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _isDisposed = true;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}
```

```csharp
// TrackViewModelFactory.cs
using System.Buffers;
using System.Collections.Concurrent;
using LMP.UI.Features.Shared;

namespace LMP.UI.Services;

/// <summary>
/// Фабрика для создания TrackItemViewModel.
/// Использует TrackRegistry для Identity Map и кэширует ViewModel-и.
/// </summary>
public class TrackViewModelFactory
{
    private readonly LibraryService _library;
    private readonly DialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly TrackRegistry _registry;

    // Кэш для "общих" VM (используются в Home, Search, Library, Playlist)
    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    public TrackViewModelFactory(
        LibraryService library,
        DialogService dialog,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        TrackRegistry registry)
    {
        _library = library;
        _dialog = dialog;
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _registry = registry;
    }

    /// <summary>
    /// Полностью стирает кэш-карту представлений во время ротации аккаунтов.
    /// Предотвращает удержание старых реактивных свойств ViewModel-представлений.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
        Log.Info("[TrackViewModelFactory] Memory representation caches successfully flushed.");
    }

    /// <summary>
    /// Возвращает существующую (из кэша) или создаёт новую "общую" ViewModel для трека.
    /// Эти VM переиспользуются между страницами (Home, Search, Library).
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // Identity Map — получаем канонический экземпляр данных
        var canonical = _registry.RegisterOrUpdate(track);

        // Проверяем кэш
        if (_cache.TryGetValue(canonical.Id, out var weakRef) &&
            weakRef.TryGetTarget(out var existing) &&
            !existing.IsDisposed)
        {
            existing.UpdatePlayAction(playAction);

            // Сбрасываем контекст, так как эта VM используется в общих списках
            existing.IsQueueContext = false;
            // existing.IsPlaylistContext - это свойство управляется извне (PlaylistViewModel), не трогаем его тут жестко

            if (!ReferenceEquals(existing.Track, canonical))
            {
                Log.Warn($"[TrackFactory] VM track mismatch for {canonical.Id}, recreating");
                // Если данные рассинхронизировались (редкий кейс), создаем заново
                existing.Dispose();
            }
            else
            {
                return existing;
            }
        }

        // Создаём новую VM
        var vm = CreateVmInstance(canonical, playAction);

        _cache[canonical.Id] = new WeakReference<TrackItemViewModel>(vm);

        return vm;
    }

    /// <summary>
    /// Создаёт СПЕЦИАЛЬНУЮ ViewModel для списка очереди.
    /// ВАЖНО: Эта VM НЕ кэшируется в общем словаре, чтобы QueueViewModel могла 
    /// безопасно вызывать Dispose() при закрытии, не ломая остальные экраны.
    /// </summary>
    public TrackItemViewModel CreateForQueue(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // Используем те же канонические данные
        var canonical = _registry.RegisterOrUpdate(track);

        // Создаем ИЗОЛИРОВАННЫЙ экземпляр
        var vm = CreateVmInstance(canonical, playAction);

        vm.IsQueueContext = true;

        // НЕ добавляем в _cache!
        return vm;
    }

    private TrackItemViewModel CreateVmInstance(TrackInfo track, Action<TrackInfo>? playAction)
    {
        return new TrackItemViewModel(
            track,
            _audio,
            _downloads,
            _manager,
            _dialog,
            _library,
            playAction);
    }

    public TrackItemViewModel? TryGet(string trackId)
    {
        if (_cache.TryGetValue(trackId, out var weakRef) &&
            weakRef.TryGetTarget(out var vm) &&
            !vm.IsDisposed)
        {
            return vm;
        }
        return null;
    }

    public void TryRemove(string trackId)
    {
        _cache.TryRemove(trackId, out _);
    }

    public int CleanupCache()
    {
        // ArrayPool устраняет аллокацию List<string> на каждый вызов очистки.
        // _cache.Count может измениться — берём с запасом через Count + 16.
        var deadKeys = ArrayPool<string>.Shared.Rent(_cache.Count + 16);
        int deadCount = 0;

        try
        {
            foreach (var kvp in _cache)
            {
                if (!kvp.Value.TryGetTarget(out var target) || target.IsDisposed)
                {
                    if (deadCount < deadKeys.Length)
                        deadKeys[deadCount++] = kvp.Key;
                }
            }

            for (int i = 0; i < deadCount; i++)
                _cache.TryRemove(deadKeys[i], out _);

            if (deadCount > 0)
            {
                Log.Debug($"[TrackFactory] Cleaned {deadCount} dead references.");
            }

            return deadCount;
        }
        finally
        {
            ArrayPool<string>.Shared.Return(deadKeys, clearArray: true);
        }
    }

    /// <summary>
    /// Полная очистка с dispose всех VM.
    /// Вызывается только при закрытии приложения или полном сбросе.
    /// </summary>
    public void ClearWithDispose()
    {
        // Собираем все живые VM
        var toDispose = new List<TrackItemViewModel>();

        foreach (var kvp in _cache)
        {
            if (kvp.Value.TryGetTarget(out var vm) && !vm.IsDisposed)
            {
                toDispose.Add(vm);
            }
        }

        _cache.Clear();

        // Диспозим отложенно
        Task.Run(async () =>
        {
            await Task.Delay(100);
            foreach (var vm in toDispose)
            {
                try { vm.Dispose(); } catch { }
            }
            Log.Info($"[TrackFactory] Disposed {toDispose.Count} VMs on clear.");
        });
    }

    /// <summary>
    /// Мягкая очистка — только удаляем ссылки, не диспозим.
    /// Используется при навигации.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
    }
}
```

```csharp
// MainWindowViewModel.cs
using System.Reactive;
using System.Reactive.Linq;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Diagnostics;
using LMP.UI.Features.Player;
using LMP.UI.Features.Search;
using LMP.UI.Features.Home;
using LMP.UI.Features.Library;
using LMP.UI.Features.Settings;
using LMP.UI.Features.Playlist;
using LMP.UI.Features.Notifications;
using System.Runtime;
using LMP.UI.Features.Queue;
using Avalonia.Threading;

namespace LMP.UI.Features.Shell;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, ViewModelBase> _pageCache = new(StringComparer.Ordinal);

    // ═══ VERSION INFO ═══
    [Reactive] public bool IsVersionInfoVisible { get; set; } = true;
    public static string VersionDisplay => G.Build.DisplayVersion;
    public static string GitHashDisplay => G.Build.GitHash;

    private string _commitsDisplay = "";
    public string CommitsDisplay
    {
        get => _commitsDisplay;
        private set => this.RaiseAndSetIfChanged(ref _commitsDisplay, value);
    }

    public ICommand ToggleVersionInfoCommand { get; }
    public ICommand OpenGitHubCommand { get; }

    // ═══ NAVIGATION ═══
    [Reactive] public ViewModelBase? CurrentPage { get; private set; }
    [Reactive] public PlayerBarViewModel PlayerBar { get; private set; }
    [Reactive] public string CurrentPageName { get; private set; } = "";
    [Reactive] public bool IsNavigationLocked { get; private set; }
    [Reactive] public string NavigationLockReason { get; private set; } = "";

    // ═══ NOTIFICATIONS ═══
    [Reactive] public NotificationButtonViewModel NotificationButton { get; private set; }
    [Reactive] public NotificationPanelViewModel NotificationPanel { get; private set; }
    [Reactive] public ToastOverlayViewModel ToastOverlay { get; private set; }

    // ═══ DIALOG HOST ═══
    [Reactive] public DialogHostViewModel DialogHost { get; private set; }

    private const int DeferredLoadDelayMs = 180;

    public ReactiveCommand<string, Unit> NavigateCommand { get; }

    public MainWindowViewModel(
          IServiceProvider services,
          PlayerBarViewModel playerBar,
          NotificationButtonViewModel notificationButton,
          NotificationPanelViewModel notificationPanel,
          ToastOverlayViewModel toastOverlay,
          DialogHostViewModel dialogHost,
          LibraryService library)
    {
        Log.Info("MainWindowViewModel constructor started.");

        _services = services;
        PlayerBar = playerBar;
        NotificationButton = notificationButton;
        NotificationPanel = notificationPanel;
        ToastOverlay = toastOverlay;
        DialogHost = dialogHost;

        library.OnAccountHydrated += HandleGlobalAccountHydrated;

        UpdateCommitsDisplay();

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            UpdateCommitsDisplay();
            this.RaisePropertyChanged(nameof(L));
        };

        ToggleVersionInfoCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsVersionInfoVisible = !IsVersionInfoVisible;
        }));

        OpenGitHubCommand = CreateCommand(ReactiveCommand.Create(OpenGitHub));

        var canNavigate = this.WhenAnyValue(
            x => x.IsNavigationLocked,
            x => x.DialogHost.HasActiveDialog,
            (locked, hasDialog) => !locked && !hasDialog);

        NavigateCommand = CreateCommand(NavigateCommand = CreateCommand(ReactiveCommand.Create<string>(pageName =>
        {
            if (!IsNavigationLocked && !DialogHost.HasActiveDialog)
            {
                Navigate(pageName);
            }
        }, canNavigate)));

        Navigate("Home");

        _ = ValidateAuthOnStartupAsync();

        Log.Info("MainWindowViewModel initialized.");
    }

    /// <summary>
    /// Обрабатывает изменения авторизации на глобальном уровне после полной гидрации кэшей.
    /// Принудительно очищает кэш представлений фабрики перед рендером.
    /// </summary>
    private void HandleGlobalAccountHydrated()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _services.GetRequiredService<TrackViewModelFactory>().ClearCache();
            ViewModelBase.BroadcastAccountChanged();
        }, DispatcherPriority.Normal);
    }

    private void UpdateCommitsDisplay()
    {
        var L = LocalizationService.Instance;
        CommitsDisplay = $"({string.Format(L["Build_CommitsCount"], G.Build.CommitCount)})";
    }

    public void LockNavigation(string reason)
    {
        IsNavigationLocked = true;
        NavigationLockReason = reason;
        Log.Info($"[Navigation] Locked: {reason}");
    }

    public void UnlockNavigation()
    {
        IsNavigationLocked = false;
        NavigationLockReason = "";
        Log.Info("[Navigation] Unlocked");
    }

    public async Task WithNavigationLockAsync(string reason, Func<Task> operation)
    {
        LockNavigation(reason);
        try { await operation(); }
        finally { UnlockNavigation(); }
    }

    public async Task<T> WithNavigationLockAsync<T>(string reason, Func<Task<T>> operation)
    {
        LockNavigation(reason);
        try { return await operation(); }
        finally { UnlockNavigation(); }
    }

    private void Navigate(string pageName)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        if (CurrentPageName == pageName)
        {
            Log.Debug($"[Navigation] Already on '{pageName}', skipping");
            return;
        }

        var sw = Stopwatch.StartNew();
        Log.Info($"Switching to page: {pageName}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        if (oldPage is ISmoothTransitionViewModel smoothOldPage)
        {
            smoothOldPage.PrepareForTransition();
        }

        if (!_pageCache.TryGetValue(pageName, out var newPage))
        {
            newPage = pageName switch
            {
                "Home" => _services.GetRequiredService<HomeViewModel>(),
                "Search" => _services.GetRequiredService<SearchViewModel>(),
                "Library" => _services.GetRequiredService<LibraryViewModel>(),
                "Settings" => _services.GetRequiredService<SettingsViewModel>(),
                "Queue" => _services.GetRequiredService<QueueViewModel>(),
                _ => null
            };

            if (newPage == null)
            {
                Log.Warn($"[Navigation] Unknown page: {pageName}");
                return;
            }

            _pageCache[pageName] = newPage;
        }

        if (newPage is ISmoothTransitionViewModel smoothNewPage)
        {
            smoothNewPage.PrepareForTransition();
        }

        CurrentPage = newPage;
        CurrentPageName = pageName;

        sw.Stop();
        Log.Info($"Page '{pageName}' ready in {sw.ElapsedMilliseconds}ms, scheduling deferred init...");

        _ = DeferredInitAsync(newPage, pageName);

        if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
        {
            if (!_pageCache.ContainsKey(oldPageName))
            {
                _ = DisposePageDelayedAsync(disposable, oldPageName);
            }
        }
    }

    public void NavigateToPlaylist(string playlistId)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        Log.Info($"Navigating to Playlist: {playlistId}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        if (oldPage is ISmoothTransitionViewModel smoothOldPage)
        {
            smoothOldPage.PrepareForTransition();
        }

        // Кэшируем страницу плейлиста, чтобы избежать утечек, дублирования инстансов и багов с Dispose
        if (!_pageCache.TryGetValue("Playlist", out var playlistPage) || playlistPage is not PlaylistViewModel playlistVM)
        {
            playlistVM = _services.GetRequiredService<PlaylistViewModel>();
            _pageCache["Playlist"] = playlistVM;
        }

        if (playlistVM is ISmoothTransitionViewModel smoothPlaylistPage)
        {
            smoothPlaylistPage.PrepareForTransition();
        }

        // Изменение контента напрямую без присвоения null исключает сбой анимации
        CurrentPage = playlistVM;
        CurrentPageName = "Playlist";

        if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
        {
            if (!_pageCache.ContainsKey(oldPageName))
            {
                _ = DisposePageDelayedAsync(disposable, oldPageName);
            }
        }

        // Важно: Запускаем отложенную инициализацию, которая вызовет OnNavigatedToAsync и сбросит _isTransitioning в false
        _ = DeferredInitAsync(playlistVM, "Playlist");

        // Запуск асинхронного метода на UI-потоке. Метод сам уступит управление при await.
        _ = LoadPlaylistSafeAsync(playlistVM, playlistId);
    }

    private async Task LoadPlaylistSafeAsync(PlaylistViewModel vm, string playlistId)
    {
        try
        {
            await vm.LoadPlaylistAsync(playlistId);
        }
        catch (Exception ex)
        {
            Log.Error($"[Navigation] Playlist load failed: {ex.Message}");
        }
    }

    private async Task DeferredInitAsync(ViewModelBase page, string pageName)
    {
        try
        {
            await Task.Delay(DeferredLoadDelayMs);

            if (CurrentPage != page)
            {
                Log.Debug($"[Navigation] Page '{pageName}' is no longer current, skipping deferred init");
                return;
            }

            var sw = Stopwatch.StartNew();
            await page.OnNavigatedToAsync();
            sw.Stop();

            Log.Info($"[Navigation] Deferred init for '{pageName}' completed in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Error($"[Navigation] Deferred init failed for '{pageName}': {ex.Message}");
        }
    }

    private async Task DisposePageDelayedAsync(IDisposable page, string pageName)
    {
        await Task.Delay(256);

        try
        {
            page.Dispose();
            Log.Debug($"[Navigation] Disposed old page: {pageName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Navigation] Error disposing {pageName}: {ex.Message}");
        }

        _services.GetRequiredService<TrackViewModelFactory>().CleanupCache();
        _services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();
    }

    private static void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = G.GitHubUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open GitHub: {ex.Message}");
        }
    }

    private async Task ValidateAuthOnStartupAsync()
    {
        try
        {
            await Task.Delay(2000);

            var auth = _services.GetRequiredService<CookieAuthService>();

            if (auth.HasProfileLoadError)
            {
                Log.Warn("[Auth] Profile load error detected on startup");

                if (auth.IsAuthenticated)
                {
                    var notifications = _services.GetRequiredService<NotificationService>();
                    await notifications.ShowToastAsync(
                        "Auth_ProfileLoadError_Title",
                        "Auth_ProfileLoadError_Message",
                        NotificationSeverity.Warning,
                        durationMs: 6000);
                }
                return;
            }

            if (!auth.IsAuthenticated) return;

            var (isValid, error, _) = await auth.ValidateSessionAsync();

            if (!isValid)
            {
                Log.Warn($"[Auth] Session expired on startup: {error}");

                var notifications = _services.GetRequiredService<NotificationService>();
                await notifications.ShowToastAsync(
                    "Auth_SessionExpired_Title",
                    "Auth_SessionExpired_Message",
                    NotificationSeverity.Warning,
                    durationMs: 8000);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Startup validation error: {ex.Message}");
        }
    }
}
```

```csharp
// AuthDialogViewModel.cs
using System.IO;
using System.IO.Compression;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using Avalonia.Threading;
using LMP.Core.Audio.Http;
using LMP.Core.Services;
using LMP.UI.Helpers;
using LMP.UI.Services;
using LMP.UI.Features.Shell;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

/// <summary>
/// Модель представления для диалога авторизации через расширение браузера.
/// </summary>
public sealed class AuthDialogViewModel : ViewModelBase
{
    private readonly CookieAuthService _auth;
    private readonly YoutubeUserDataService _userData;
    private readonly LocalAuthServer _localServer;

    [Reactive] public string CookiesText { get; set; } = string.Empty;
    [Reactive] public bool IsAuthenticating { get; private set; }
    [Reactive] public string StatusText { get; private set; } = string.Empty;
    [Reactive] public bool IsError { get; private set; }
    [Reactive] public int AttemptCount { get; private set; }

    [Reactive] public bool IsExtensionDownloading { get; private set; }
    [Reactive] public bool IsExtensionReady { get; private set; }
    [Reactive] public bool IsGuideExpanded { get; set; } = true;
    [Reactive] public string ExtensionFolderPath { get; private set; } = string.Empty;

    [Reactive] public bool IsPathCopied { get; private set; }
    [Reactive] public int SelectedBrowserTabIndex { get; set; }
    [Reactive] public string InstalledExtensionVersion { get; private set; } = "—";

    /// <summary>
    /// Возвращает текстовое представление установленной версии расширения на основе текущей локализации.
    /// </summary>
    public string ExtensionVersionText
    {
        get
        {
            var format = SL["Auth_Extension_Version_Format"];
            if (string.IsNullOrEmpty(format) || !format.Contains("{0}"))
            {
                format = "Extension: v{0}";
            }
            return string.Format(format, InstalledExtensionVersion);
        }
    }

    public bool IsFirefoxWarningVisible => IsGuideExpanded && SelectedBrowserTabIndex == 2;
    public bool IsWarningVisible => !IsGuideExpanded || IsFirefoxWarningVisible;

    /// <summary>
    /// Возвращает значение, указывающее, выполняется ли в данный момент сетевая операция.
    /// </summary>
    public bool IsSpinnerActive => IsAuthenticating || IsExtensionDownloading;

    public Action<bool>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> AuthenticateCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadExtensionCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPathCommand { get; }
    public ReactiveCommand<string, Unit> CopyLinkCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleGuideCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="AuthDialogViewModel"/>.
    /// </summary>
    public AuthDialogViewModel(
        CookieAuthService auth,
        YoutubeUserDataService userData,
        LocalAuthServer localServer)
    {
        _auth = auth;
        _userData = userData;
        _localServer = localServer;

        AuthenticateCommand = CreateCommand(ReactiveCommand.CreateFromTask(AuthenticateAsync));
        DownloadExtensionCommand = CreateCommand(ReactiveCommand.CreateFromTask(DownloadExtensionAsync));
        CopyPathCommand = CreateCommand(ReactiveCommand.CreateFromTask(CopyPathToClipboardAsync));
        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask<string>(CopyLinkAsync));
        ToggleGuideCommand = CreateCommand(ReactiveCommand.Create(() => IsGuideExpanded = !IsGuideExpanded));
        CloseCommand = CreateCommand(ReactiveCommand.Create(() => OnResult?.Invoke(false)));

        StatusText = SL["Dialog_Login_WaitingStatus"] ?? "Ожидаем запрос от расширения или введите куки вручную...";

        this.WhenAnyValue(x => x.IsAuthenticating, x => x.IsExtensionDownloading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsSpinnerActive)));

        this.WhenAnyValue(x => x.SelectedBrowserTabIndex, x => x.IsGuideExpanded)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsFirefoxWarningVisible));
                this.RaisePropertyChanged(nameof(IsWarningVisible));
            });

        this.WhenAnyValue(x => x.InstalledExtensionVersion)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ExtensionVersionText)));

        _ = StartListeningAsync(_cts.Token);
        _ = CheckExtensionVersionAsync(_cts.Token);
    }

    /// <summary>
    /// Динамически вычисляет и извлекает текущий синглтон MainWindowViewModel для блокировок TitleBar.
    /// </summary>
    private static MainWindowViewModel? GetMainWindowViewModel()
    {
        try
        {
            return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<MainWindowViewModel>(AppEntry.Services);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Выполняет автоматическую проверку соответствия локальной и удаленной версий расширения.
    /// </summary>
    private async Task CheckExtensionVersionAsync(CancellationToken ct)
    {
        var localVersionStr = GetLocalExtensionVersion();
        if (string.IsNullOrEmpty(localVersionStr))
        {
            InstalledExtensionVersion = "—";
            IsExtensionReady = false;
            IsGuideExpanded = true;
            return;
        }

        var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");
        ExtensionFolderPath = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
        InstalledExtensionVersion = localVersionStr;
        IsExtensionReady = true;
        IsGuideExpanded = false;

        try
        {
            var remoteManifestUrl = GetRemoteManifestUrl();
            using var response = await SharedHttpClient.Instance.GetAsync(
                remoteManifestUrl,
                HttpCompletionOption.ResponseContentRead,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var remoteVerProp))
            {
                var remoteVersionStr = remoteVerProp.GetString();
                if (!string.IsNullOrEmpty(remoteVersionStr) &&
                    Version.TryParse(localVersionStr, out var localVer) &&
                    Version.TryParse(remoteVersionStr, out var remoteVer))
                {
                    if (localVer < remoteVer)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            IsExtensionReady = false;
                            IsGuideExpanded = true;
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Auth] Automatic extension version check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает версию расширения, обнаруженного в локальном каталоге приложения.
    /// </summary>
    private string? GetLocalExtensionVersion()
    {
        try
        {
            var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");
            var folder = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
            var manifestPath = Path.Combine(folder, "manifest.json");

            if (!File.Exists(manifestPath)) return null;

            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var versionProp))
            {
                return versionProp.GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Auth] Failed to read local extension version: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Формирует адрес для получения манифеста из удаленного репозитория.
    /// </summary>
    private string GetRemoteManifestUrl()
    {
        var url = G.AuthExtensionDownloadUrl;
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(url);
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    var owner = segments[0];
                    var repo = segments[1];
                    var branch = "main";

                    int headsIdx = url.IndexOf("heads/", StringComparison.OrdinalIgnoreCase);
                    if (headsIdx >= 0)
                    {
                        var remaining = url.Substring(headsIdx + 6);
                        int zipIdx = remaining.IndexOf(".zip", StringComparison.OrdinalIgnoreCase);
                        if (zipIdx >= 0)
                        {
                            branch = remaining.Substring(0, zipIdx);
                        }
                    }

                    return $"https://raw.githubusercontent.com/{owner}/{repo}/refs/heads/{branch}/manifest.json";
                }
            }
            catch { /* fallback */ }
        }

        return "https://raw.githubusercontent.com/Scream034/LMP-Auth/refs/heads/main/manifest.json";
    }

    private void ApplyLocalExtension()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");
            ExtensionFolderPath = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
            InstalledExtensionVersion = GetLocalExtensionVersion() ?? "—";
            IsExtensionReady = true;
            IsGuideExpanded = false;
        });
    }

    private async Task StartListeningAsync(CancellationToken ct)
    {
        try
        {
            var cookies = await _localServer.WaitForCookiesAsync(ct);
            if (!string.IsNullOrEmpty(cookies) && !ct.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CookiesText = cookies;
                    AuthenticateCommand.Execute(Unit.Default);
                });
            }
        }
        catch (OperationCanceledException) { /* ignore */ }
    }

    private async Task AuthenticateAsync()
    {
        if (string.IsNullOrWhiteSpace(CookiesText))
        {
            SetStatus(SL["Dialog_Login_WaitingStatus"], isError: true);
            return;
        }

        IsAuthenticating = true;
        AttemptCount++;
        SetStatus($"{SL["Splash_ConnectingYouTube"]} ({AttemptCount})", isError: false);

        var mainWindow = GetMainWindowViewModel();
        mainWindow?.LockNavigation(SL["Splash_ConnectingYouTube"] ?? "Подключение к YouTube...");

        try
        {
            _auth.SaveCookies(CookiesText);

            if (!_auth.IsAuthenticated)
            {
                SetStatus(SL["Auth_LoginError_SAPISID"], isError: true);
                return;
            }

            SetStatus(SL["Nav_PleaseWait"], isError: false);

            var (isValid, error, isNetworkError) = await _auth.ValidateSessionAsync();
            if (!isValid)
            {
                if (isNetworkError)
                {
                    SetStatus($"{SL["Search_NetworkError"]} ({error})", isError: true);
                }
                else
                {
                    SetStatus(SL["Auth_SessionExpired_Message"], isError: true);
                    _auth.Logout();
                }
                return;
            }

            var accounts = await _userData.GetAvailableAccountsAsync();
            string finalName, finalEmail, finalAvatar, finalGaiaId;

            if (accounts.Count > 1)
            {
                IsAuthenticating = false;
                mainWindow?.UnlockNavigation();

                var dialogService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                                     .GetRequiredService<DialogService>(AppEntry.Services);

                var selectedAccount = await dialogService.ShowAccountSelectionDialogAsync(accounts);

                if (selectedAccount == null)
                {
                    SetStatus(SL["Common_Cancel"], isError: true);
                    _auth.Logout();
                    return;
                }

                _auth.SetAuthUser(selectedAccount.AuthUser);
                IsAuthenticating = true;
                mainWindow?.LockNavigation(SL["Splash_ConnectingYouTube"] ?? "Подключение к YouTube...");

                // БЕРЕМ ДАННЫЕ ИЗ ВЫБРАННОГО АККАУНТА, а не запрашиваем заново
                finalName = selectedAccount.Name;
                finalEmail = selectedAccount.Email;
                finalAvatar = selectedAccount.AvatarUrl;
                finalGaiaId = selectedAccount.GaiaId;
            }
            else
            {
                var singleAccount = accounts.FirstOrDefault();
                _auth.SetAuthUser(singleAccount?.AuthUser ?? AuthState.DefaultAuthUser);

                // Если аккаунт один, пробуем взять данные сразу из него
                if (singleAccount != null && !string.IsNullOrEmpty(singleAccount.Name))
                {
                    finalName = singleAccount.Name;
                    finalEmail = singleAccount.Email;
                    finalAvatar = singleAccount.AvatarUrl;
                    finalGaiaId = singleAccount.GaiaId;
                }
                else
                {
                    // Иначе делаем запасной сетевой запрос
                    var (Name, Email, AvatarUrl) = await _userData.GetAccountInfoAsync();
                    finalName = Name;
                    finalEmail = Email;
                    finalAvatar = AvatarUrl;
                    finalGaiaId = string.Empty;
                }
            }

            if (string.IsNullOrEmpty(finalName) || (finalName.Equals("User", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(finalAvatar)))
            {
                SetStatus(SL["Auth_ProfileLoadError_Message"], isError: true);
                _auth.Logout();
                return;
            }

            _auth.UpdateUserProfile(finalName, finalEmail, finalAvatar, finalGaiaId);

            SetStatus($"{SL["Dialog_Success"]}! {finalName}", isError: false);
            await Task.Delay(1000);

            OnResult?.Invoke(true);
        }
        catch (Exception ex)
        {
            SetStatus(SL["Dialog_Error_Title"] + ": " + ex.Message, isError: true);
        }
        finally
        {
            IsAuthenticating = false;
            mainWindow?.UnlockNavigation();
        }
    }

    private async Task DownloadExtensionAsync()
    {
        try
        {
            IsExtensionDownloading = true;
            SetStatus(SL["Splash_Initializing"], isError: false);

            SafeDeleteDirectory(G.Folder.Extension);
            Directory.CreateDirectory(G.Folder.Extension);

            using var response = await SharedHttpClient.Instance.GetAsync(
                G.AuthExtensionDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                _cts.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;

            await using (var downloadStream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
            await using (var fs = new FileStream(G.FilePath.TempAuthExtensionZipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                long totalBytesRead = 0;
                int bytesRead;

                while ((bytesRead = await downloadStream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token).ConfigureAwait(false);
                    totalBytesRead += bytesRead;

                    if (contentLength.HasValue && contentLength.Value > 0)
                    {
                        var progressPercent = Math.Clamp((double)totalBytesRead / contentLength.Value * 100, 0, 100);
                        var totalMb = (double)totalBytesRead / (1024 * 1024);
                        var contentMb = (double)contentLength.Value / (1024 * 1024);
                        var progressText = string.Format(SL["Extension_Install_Downloading_Progress"], progressPercent, totalMb, contentMb);
                        Dispatcher.UIThread.Post(() => SetStatus(progressText, isError: false));
                    }
                    else
                    {
                        var totalMb = (double)totalBytesRead / (1024 * 1024);
                        var progressText = string.Format(SL["Extension_Install_Downloading_Indeterminate"], totalMb);
                        Dispatcher.UIThread.Post(() => SetStatus(progressText, isError: false));
                    }
                }
            }

            Dispatcher.UIThread.Post(() => SetStatus(SL["Splash_PreparingImages"], isError: false));

            ZipFile.ExtractToDirectory(G.FilePath.TempAuthExtensionZipFile, G.Folder.Extension);

            var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ExtensionFolderPath = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
                InstalledExtensionVersion = GetLocalExtensionVersion() ?? "—";
                IsExtensionReady = true;
                IsGuideExpanded = true;
                SetStatus(SL["Dialog_Login_WaitingStatus"], isError: false);
            });

            await CopyPathToClipboardAsync();
        }
        catch (Exception)
        {
            SetStatus(SL["Extension_Install_DownloadError"], isError: true);
        }
        finally
        {
            IsExtensionDownloading = false;
            try { if (File.Exists(G.FilePath.TempAuthExtensionZipFile)) File.Delete(G.FilePath.TempAuthExtensionZipFile); } catch { /* ignore */ }
        }
    }

    private async Task CopyPathToClipboardAsync()
    {
        if (string.IsNullOrEmpty(ExtensionFolderPath)) return;
        await Clipboard.SetTextAsync(ExtensionFolderPath);
        IsPathCopied = true;
        CopyHintService.Instance.Show(SL["Extension_Path_Copied_Toast"], CopyHintKind.Success);
    }

    private async Task CopyLinkAsync(string url)
    {
        await Clipboard.SetTextAsync(url);
        CopyHintService.Instance.Show(SL["Extension_Link_Copied_Toast"], CopyHintKind.Success);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsError = isError;
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException)
        {
            try
            {
                var tempPath = path + "_deleted_" + Path.GetRandomFileName();
                Directory.Move(path, tempPath);
                _ = Task.Run(() => { try { Directory.Delete(tempPath, true); } catch { } });
            }
            catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
```