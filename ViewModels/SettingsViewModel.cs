// SettingsViewModel.cs
// ViewModel для страницы настроек приложения

using System.Reactive;
using System.Reactive.Linq;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly GoogleAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    // --- Настройки путей и загрузки ---
    [Reactive] public string DownloadPath { get; set; } = string.Empty;
    [Reactive] public int LoadBatchSize { get; set; }
    [Reactive] public int SearchBatchSize { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }

    // --- Настройки звука ---
    [Reactive] public int MaxVolumeLimit { get; set; }
    [Reactive] public float TargetGainDb { get; set; }

    // --- Настройки качества ---
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

    [Reactive] public List<AudioQualityPreference> QualityOptions { get; private set; } = [];

    // --- Интеграции ---
    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }
    [Reactive] public int SearchCacheTtlMinutes { get; set; }

    // --- Языки ---
    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    // --- Авторизация ---
    [Reactive] public bool IsAuthenticated { get; private set; }

    // --- Fake Account ---
    [Reactive] public string FakeChannelInput { get; set; } = string.Empty;
    [Reactive] public bool IsLoadingFakeAccount { get; set; }

    // ========== ЕДИНЫЙ API ДЛЯ АККАУНТА ==========

    /// <summary>
    /// Есть ли какой-либо аккаунт (Google или Fake)
    /// </summary>
    public bool HasAccount => IsAuthenticated || _library.HasFakeAccount;

    /// <summary>
    /// Это ограниченный (Fake) аккаунт?
    /// </summary>
    public bool IsFakeAccount => !IsAuthenticated && _library.HasFakeAccount;

    /// <summary>
    /// Имя аккаунта (приоритет: Google YouTube Channel > Fake Account > Google Name > Guest)
    /// </summary>
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

    /// <summary>
    /// URL аватара аккаунта (приоритет: Google YouTube Avatar > Fake Account Avatar)
    /// </summary>
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

    /// <summary>
    /// Подзаголовок аккаунта (email для Google, статус для Fake)
    /// </summary>
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

    /// <summary>
    /// Показывать предупреждение об ограничениях
    /// </summary>
    public bool ShowLimitedAccessWarning => IsFakeAccount;

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

    // --- Команды ---
    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SetFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFakeAccountCommand { get; }

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

        // --- Подписки ---

        // Обновление при смене языка
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            RaiseAccountPropertiesChanged();
            QualityOptions = [.. Enum.GetValues<AudioQualityPreference>()];
        };

        // Обновление при изменении авторизации
        _auth.OnAuthStateChanged += () =>
        {
            UpdateAuthState();
            RaiseAccountPropertiesChanged();
        };

        // Обновление при изменении Fake Account
        _library.OnFakeAccountChanged += RaiseAccountPropertiesChanged;

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
                Log.Info($"New volume limit: {v}");
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
                Log.Info($"New target gain: {v}");
                _library.Data.TargetGainDb = v;
                _library.Save();
                _audio.UpdateAudioSettings();
            });

        // Плавная загрузка
        this.WhenAnyValue(x => x.EnableSmoothLoading)
            .Skip(1)
            .Subscribe(v =>
            {
                Log.Info($"New smooth loading setting: {v}");
                _library.Data.EnableSmoothLoading = v;
                _library.Save();
            });

        // Discord RPC
        this.WhenAnyValue(x => x.DiscordRpcEnabled)
            .Skip(1)
            .Subscribe(v =>
            {
                Log.Info($"New Discord RPC setting: {v}");
                _library.Data.DiscordRpcEnabled = v;
                _library.Save();
            });

        // Автовоспроизведение
        this.WhenAnyValue(x => x.AutoPlayOnPaste)
            .Skip(1)
            .Subscribe(v =>
            {
                Log.Info($"New autoplay setting: {v}");
                _library.Data.AutoPlayOnUrlPaste = v;
                _library.Save();
            });

        this.WhenAnyValue(x => x.SearchBatchSize)
            .Skip(1)
            .Where(v => v >= 10 && v <= 100)
            .Subscribe(v =>
            {
                Log.Info($"New search batch size: {v}");
                _library.Data.SearchBatchSize = v;
                _library.Save();
            });

        this.WhenAnyValue(x => x.SearchCacheTtlMinutes)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(val =>
            {
                _library.Data.SearchCacheTtlMinutes = val;
                _library.Save();

                // Очищаем просроченные записи при изменении TTL
                _ = _searchCache.CleanupExpiredAsync();
            });

        this.WhenAnyValue(x => x.LoadBatchSize)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(val =>
            {
                _library.Data.LoadBatchSize = val;
                _library.Save();
            });

        // --- Команды ---

        SetFakeAccountCommand = ReactiveCommand.CreateFromTask(SetFakeAccountAsync);
        ClearFakeAccountCommand = ReactiveCommand.Create(ClearFakeAccount);
        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(ResetLibraryAsync);
        LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync);
        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);
    }

    // --- Команды: Fake Account ---

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

    // --- Команды: Auth ---

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

    // --- Команды: Storage ---

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

    // --- Вспомогательные методы ---

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
}