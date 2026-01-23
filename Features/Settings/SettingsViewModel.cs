using System.Reactive;
using System.Reactive.Linq;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Features.Settings;

/// <summary>
/// ViewModel для управления настройками приложения.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    #region Fields

    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly GoogleAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    // [FIX] Поля для хранения делегатов событий (для корректной отписки)
    private readonly EventHandler<string> _languageChangedHandler;
    private readonly Action _authStateChangedHandler;
    private readonly Action _fakeAccountChangedHandler;
    
    // [FIX] Флаг освобождения ресурсов
    private bool _isDisposed;

    #endregion

    #region Properties

    // --- Настройки путей и загрузки ---
    
    /// <summary>Путь для скачивания треков.</summary>
    [Reactive] public string DownloadPath { get; set; } = string.Empty;
    
    /// <summary>Размер пакета загрузки при пагинации.</summary>
    [Reactive] public int LoadBatchSize { get; set; }
    
    /// <summary>Размер пакета при поиске.</summary>
    [Reactive] public int SearchBatchSize { get; set; }
    
    /// <summary>Включить скелетоны и плавную загрузку.</summary>
    [Reactive] public bool EnableSmoothLoading { get; set; }

    // --- Настройки звука ---
    
    /// <summary>Лимит максимальной громкости (%).</summary>
    [Reactive] public int MaxVolumeLimit { get; set; }
    
    /// <summary>Целевое усиление (Gain) в дБ.</summary>
    [Reactive] public float TargetGainDb { get; set; }

    // --- Настройки качества ---
    
    /// <summary>Предпочтительное качество аудио.</summary>
    public AudioQualityPreference QualityPreference
    {
        get => _library.Data.QualityPreference;
        set
        {
            if (_library.Data.QualityPreference == value) return;
            _library.Data.QualityPreference = value;
            _library.Save();
            _youtube.ClearCache();
            this.RaisePropertyChanged();
            ApplyQualityToCurrentTrack(value);
        }
    }

    /// <summary>Запоминать формат последнего воспроизведенного трека.</summary>
    public bool RememberTrackFormat
    {
        get => _library.Data.RememberTrackFormat;
        set
        {
            if (_library.Data.RememberTrackFormat == value) return;
            _library.Data.RememberTrackFormat = value;
            _library.Save();
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Список доступных опций качества.</summary>
    [Reactive] public List<AudioQualityPreference> QualityOptions { get; private set; } = [];

    // --- Интеграции ---
    
    /// <summary>Включить Discord Rich Presence.</summary>
    [Reactive] public bool DiscordRpcEnabled { get; set; }
    
    /// <summary>Автоматически воспроизводить при вставке URL.</summary>
    [Reactive] public bool AutoPlayOnPaste { get; set; }
    
    /// <summary>Время жизни кэша поиска (минуты).</summary>
    [Reactive] public int SearchCacheTtlMinutes { get; set; }

    // --- Языки ---
    
    /// <summary>Список доступных языков.</summary>
    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    
    /// <summary>Выбранный язык интерфейса.</summary>
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    // --- Авторизация ---
    
    /// <summary>Пользователь авторизован в Google.</summary>
    [Reactive] public bool IsAuthenticated { get; private set; }

    // --- Fake Account ---
    
    /// <summary>Ввод URL канала для фейкового аккаунта.</summary>
    [Reactive] public string FakeChannelInput { get; set; } = string.Empty;
    
    /// <summary>Идет ли процесс загрузки информации о канале.</summary>
    [Reactive] public bool IsLoadingFakeAccount { get; set; }

    // --- ЕДИНЫЙ API ДЛЯ АККАУНТА ---

    /// <summary>Есть ли какой-либо аккаунт (Google или Fake).</summary>
    public bool HasAccount => IsAuthenticated || _library.HasFakeAccount;

    /// <summary>Это ограниченный (Fake) аккаунт?</summary>
    public bool IsFakeAccount => !IsAuthenticated && _library.HasFakeAccount;

    /// <summary>Имя аккаунта.</summary>
    public string AccountName
    {
        get
        {
            if (IsAuthenticated)
                return _auth.State.YouTubeChannelName
                    ?? _auth.State.UserName
                    ?? L["Auth_NotSignedIn"];

            if (_library.HasFakeAccount)
                return _library.FakeAccountName ?? L["Auth_NotSignedIn"];

            return L["Auth_NotSignedIn"];
        }
    }

    /// <summary>URL аватара аккаунта.</summary>
    public string? AccountAvatarUrl
    {
        get
        {
            if (IsAuthenticated)
                return _auth.State.YouTubeAvatarUrl;

            if (_library.HasFakeAccount)
                return _library.FakeAccountAvatarUrl;

            return null;
        }
    }

    /// <summary>Подзаголовок аккаунта.</summary>
    public string AccountSubtitle
    {
        get
        {
            if (IsAuthenticated)
                return _auth.State.UserEmail ?? L["Auth_LoggedIn"];

            if (_library.HasFakeAccount)
                return L["Account_LimitedAccess"];

            return L["Auth_Guest"];
        }
    }

    /// <summary>Показывать предупреждение об ограничениях.</summary>
    public bool ShowLimitedAccessWarning => IsFakeAccount;

    /// <summary>Включить кэширование поиска.</summary>
    public bool EnableSearchCache
    {
        get => _library.Data.EnableSearchCache;
        set
        {
            if (_library.Data.EnableSearchCache == value) return;
            _library.Data.EnableSearchCache = value;
            _library.Save();
            this.RaisePropertyChanged();
        }
    }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SetFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFakeAccountCommand { get; }

    #endregion

    #region Constructors

    public SettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        GoogleAuthService auth,
        IDialogService dialog,
        AudioEngine audio,
        YoutubeProvider youtube)
    {
        _library = library;
        _searchCache = searchCache;
        _auth = auth;
        _dialog = dialog;
        _audio = audio;
        _youtube = youtube;

        // Инициализация коллекций и данных
        QualityOptions = [.. Enum.GetValues<AudioQualityPreference>()];
        FakeChannelInput = _library.FakeAccountUrl ?? "";

        SearchCacheTtlMinutes = _library.Data.SearchCacheTtlMinutes > 0
            ? _library.Data.SearchCacheTtlMinutes
            : 60;

        LoadBatchSize = _library.Data.LoadBatchSize > 0
            ? _library.Data.LoadBatchSize
            : 20;

        LoadSettings();
        UpdateAuthState();

        // [FIX] Инициализация обработчиков событий
        _languageChangedHandler = (_, _) =>
        {
            RaiseAccountPropertiesChanged();
            QualityOptions = [.. Enum.GetValues<AudioQualityPreference>()];
        };

        _authStateChangedHandler = () =>
        {
            UpdateAuthState();
            RaiseAccountPropertiesChanged();
        };

        _fakeAccountChangedHandler = RaiseAccountPropertiesChanged;

        // [FIX] Подписка на события
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
        _auth.OnAuthStateChanged += _authStateChangedHandler;
        _library.OnFakeAccountChanged += _fakeAccountChangedHandler;

        InitializeReactiveSubscriptions();

        // [FIX] Прямая инициализация команд в конструкторе (исправляет CS0206 и CS8618)
        SetFakeAccountCommand = ReactiveCommand.CreateFromTask(SetFakeAccountAsync);
        ClearFakeAccountCommand = ReactiveCommand.Create(ClearFakeAccount);
        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(ResetLibraryAsync);
        LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync);
        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);
    }

    #endregion

    #region Initialization Methods

    private void InitializeReactiveSubscriptions()
    {
        // Автосохранение языка
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1)
            .WhereNotNull()
            .Throttle(TimeSpan.FromMilliseconds(100))
            .Subscribe(lang =>
            {
                Log.Info($"New language: {lang.Name}");
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.Data.LanguageCode = lang.Code;
                _library.Save();
            });

        // Громкость
        this.WhenAnyValue(x => x.MaxVolumeLimit)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.MaxVolumeLimit = v;
                _library.Save();
                _audio.UpdateAudioSettings();
            });

        // Усиление
        this.WhenAnyValue(x => x.TargetGainDb)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(v =>
            {
                _library.Data.TargetGainDb = v;
                _library.Save();
                _audio.UpdateAudioSettings();
            });

        // Плавная загрузка
        this.WhenAnyValue(x => x.EnableSmoothLoading)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.EnableSmoothLoading = v;
                _library.Save();
            });

        // Discord RPC
        this.WhenAnyValue(x => x.DiscordRpcEnabled)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.DiscordRpcEnabled = v;
                _library.Save();
            });

        // Автовоспроизведение
        this.WhenAnyValue(x => x.AutoPlayOnPaste)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.AutoPlayOnUrlPaste = v;
                _library.Save();
            });

        // Размер пакета поиска
        this.WhenAnyValue(x => x.SearchBatchSize)
            .Skip(1)
            .Where(v => v >= 10 && v <= 100)
            .Subscribe(v =>
            {
                _library.Data.SearchBatchSize = v;
                _library.Save();
            });

        // TTL кэша поиска
        this.WhenAnyValue(x => x.SearchCacheTtlMinutes)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(val =>
            {
                _library.Data.SearchCacheTtlMinutes = val;
                _library.Save();
                _ = _searchCache.CleanupExpiredAsync();
            });

        // Размер пакета загрузки
        this.WhenAnyValue(x => x.LoadBatchSize)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(val =>
            {
                _library.Data.LoadBatchSize = val;
                _library.Save();
            });
    }

    #endregion

    #region Action Methods

    private async Task SetFakeAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(FakeChannelInput)) return;

        IsLoadingFakeAccount = true;

        try
        {
            var info = await _youtube.GetChannelInfoAsync(FakeChannelInput);
            if (info != null)
            {
                _library.SetFakeAccount(FakeChannelInput, info.Value.Name, info.Value.AvatarUrl);
                await _dialog.ShowInfoAsync(L["Dialog_Success"],
                    string.Format(L["Dialog_Merge_Success"], info.Value.Name));
            }
            else
            {
                await _dialog.ShowInfoAsync(L["Dialog_Error_Title"], L["Dialog_Merge_Error"]);
            }
        }
        catch (Exception ex)
        {
            await _dialog.ShowInfoAsync(L["Dialog_Error_Title"], ex.Message);
        }
        finally
        {
            IsLoadingFakeAccount = false;
        }
    }

    private void ClearFakeAccount()
    {
        _library.ClearFakeAccount();
        FakeChannelInput = "";
    }

    private async Task LoginAsync()
    {
        await _auth.StartLoginAsync();
        UpdateAuthState();
    }

    private async Task LogoutAsync()
    {
        bool confirmed = await _dialog.ConfirmAsync(
            L["Auth_Logout"],
            L["Dialog_LogoutMessage"]);

        if (confirmed)
        {
            _auth.Logout();
            UpdateAuthState();
        }
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await _dialog.SelectFolderAsync(DownloadPath);
        if (!string.IsNullOrEmpty(newPath))
        {
            DownloadPath = newPath;
            _library.DownloadPath = newPath;
            _library.Save();
        }
    }

    private async Task ClearHistoryAsync()
    {
        bool confirmed = await _dialog.ConfirmAsync(
            L["Dialog_Confirm_Title"],
            L["Dialog_ClearHistoryMessage"]);

        if (confirmed)
        {
            _library.ClearHistory();
            await _dialog.ShowInfoAsync(L["Dialog_Done_Title"], L["Dialog_HistoryCleared"]);
        }
    }

    private async Task ResetLibraryAsync()
    {
        bool confirmed = await _dialog.ConfirmAsync(
            L["Dialog_Warning_Title"],
            L["Dialog_ResetMessage"]);

        if (confirmed)
        {
            _library.Reset();
            LoadSettings();
            await _dialog.ShowInfoAsync(L["Dialog_Done_Title"], L["Dialog_ResetComplete"]);
        }
    }

    #endregion

    #region Helper Methods

    private void RaiseAccountPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(HasAccount));
        this.RaisePropertyChanged(nameof(IsFakeAccount));
        this.RaisePropertyChanged(nameof(AccountName));
        this.RaisePropertyChanged(nameof(AccountAvatarUrl));
        this.RaisePropertyChanged(nameof(AccountSubtitle));
        this.RaisePropertyChanged(nameof(ShowLimitedAccessWarning));
    }

    private void LoadSettings()
    {
        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = _library.Data.DiscordRpcEnabled;
        AutoPlayOnPaste = _library.Data.AutoPlayOnUrlPaste;
        LoadBatchSize = _library.Data.LoadBatchSize;
        SearchBatchSize = _library.Data.SearchBatchSize > 0 ? _library.Data.SearchBatchSize : 30;
        EnableSmoothLoading = _library.Data.EnableSmoothLoading;
        MaxVolumeLimit = _library.Data.MaxVolumeLimit;
        TargetGainDb = _library.Data.TargetGainDb;

        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == _library.Data.LanguageCode)
            ?? Languages[0];
    }

    private void UpdateAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
    }

    private void ApplyQualityToCurrentTrack(AudioQualityPreference preference)
    {
        var currentTrack = _audio.CurrentTrack;
        if (currentTrack == null || currentTrack.IsDownloaded) return;

        string targetContainer = preference switch
        {
            AudioQualityPreference.BestAvailable => "webm",
            AudioQualityPreference.Standard => "mp4",
            _ => "webm"
        };

        currentTrack.PreferredContainer = targetContainer;
        currentTrack.PreferredBitrate = 0;

        _ = _audio.SwitchQualityAsync(targetContainer, 0);
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Освобождает ресурсы и отписывается от событий.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // [FIX] Отписка от событий
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        _auth.OnAuthStateChanged -= _authStateChangedHandler;
        _library.OnFakeAccountChanged -= _fakeAccountChangedHandler;

        GC.SuppressFinalize(this);
    }

    #endregion
}