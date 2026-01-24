using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Features.Settings;

/// <summary>
/// ViewModel страницы настроек.
/// Управляет всеми настройками приложения: аккаунт, сеть, кэш, тема, аудио.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    // DEPENDENCIES

    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly StreamCacheManager _streamCache;
    private readonly ThemeManagerService _themeManager;
    private readonly GoogleAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    private bool _isDisposed;
    private bool _isLoadingTheme;

    // ACCOUNT PROPERTIES

    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string FakeChannelInput { get; set; } = string.Empty;
    [Reactive] public bool IsLoadingFakeAccount { get; set; }

    public bool HasAccount => IsAuthenticated || _library.HasFakeAccount;
    public bool IsFakeAccount => !IsAuthenticated && _library.HasFakeAccount;

    public string AccountName => IsAuthenticated
        ? _auth.State.YouTubeChannelName ?? _auth.State.UserName ?? L["Auth_NotSignedIn"]
        : _library.FakeAccountName ?? L["Auth_NotSignedIn"];

    public string? AccountAvatarUrl => IsAuthenticated
        ? _auth.State.YouTubeAvatarUrl
        : _library.FakeAccountAvatarUrl;

    public string AccountSubtitle => IsAuthenticated
        ? _auth.State.UserEmail ?? L["Auth_LoggedIn"]
        : IsFakeAccount ? L["Account_LimitedAccess"] : L["Auth_Guest"];

    // NETWORK PROPERTIES

    public List<InternetProfile> InternetProfileOptions { get; } = [.. Enum.GetValues<InternetProfile>()];
    [Reactive] public InternetProfile SelectedInternetProfile { get; set; }
    [Reactive] public bool ProxyEnabled { get; set; }
    [Reactive] public string ProxyHost { get; set; } = "";
    [Reactive] public int ProxyPort { get; set; } = 8080;
    [Reactive] public bool ProxyAuth { get; set; }
    [Reactive] public string ProxyUser { get; set; } = "";
    [Reactive] public string ProxyPass { get; set; } = "";
    [Reactive] public bool NetworkRestartRequired { get; set; }

    // STORAGE & CACHE PROPERTIES

    [Reactive] public string DownloadPath { get; set; } = string.Empty;
    [Reactive] public int ImageCacheLimitMb { get; set; }
    [Reactive] public int AudioCacheLimitMb { get; set; }
    [Reactive] public string ImageCacheStats { get; private set; } = "...";
    [Reactive] public string AudioCacheStats { get; private set; } = "...";
    [Reactive] public double ImageCacheUsagePercent { get; private set; }
    [Reactive] public double AudioCacheUsagePercent { get; private set; }

    // THEME / APPEARANCE PROPERTIES

    /// <summary>Список встроенных пресетов тем</summary>
    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];

    /// <summary>Выбранный пресет (null если кастомная тема)</summary>
    [Reactive] public ThemeSettings? SelectedPreset { get; set; }

    // Основные цвета для редактирования
    [Reactive] public Color AccentColor { get; set; }
    [Reactive] public Color BgPrimaryColor { get; set; }
    [Reactive] public Color BgSecondaryColor { get; set; }
    [Reactive] public Color BgElevatedColor { get; set; }
    [Reactive] public Color TextPrimaryColor { get; set; }
    [Reactive] public Color TextSecondaryColor { get; set; }

    /// <summary>Есть ли несохраненные изменения темы</summary>
    [Reactive] public bool HasUnsavedThemeChanges { get; set; }

    // AUDIO PROPERTIES

    public List<AudioQualityPreference> QualityOptions { get; } = [.. Enum.GetValues<AudioQualityPreference>()];
    [Reactive] public int MaxVolumeLimit { get; set; }
    [Reactive] public float TargetGainDb { get; set; }
    [Reactive] public AudioQualityPreference QualityPreference { get; set; }
    [Reactive] public bool RememberTrackFormat { get; set; }

    // GENERAL PROPERTIES

    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }
    [Reactive] public int SearchBatchSize { get; set; }
    [Reactive] public bool EnableSearchCache { get; set; }
    [Reactive] public int SearchCacheTtlMinutes { get; set; }

    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    // COMMANDS

    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SetFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearImageCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAudioCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetThemeCommand { get; }

    // CONSTRUCTOR

    public SettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        StreamCacheManager streamCache,
        ThemeManagerService themeManager,
        GoogleAuthService auth,
        IDialogService dialog,
        AudioEngine audio,
        YoutubeProvider youtube)
    {
        _library = library;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _streamCache = streamCache;
        _themeManager = themeManager;
        _auth = auth;
        _dialog = dialog;
        _audio = audio;
        _youtube = youtube;

        // Загружаем пресеты
        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        // Инициализируем команды
        LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync);
        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);
        SetFakeAccountCommand = ReactiveCommand.CreateFromTask(SetFakeAccountAsync);
        ClearFakeAccountCommand = ReactiveCommand.Create(ClearFakeAccount);
        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(ResetLibraryAsync);
        ClearImageCacheCommand = ReactiveCommand.CreateFromTask(ClearImageCacheAsync);
        ClearAudioCacheCommand = ReactiveCommand.CreateFromTask(ClearAudioCacheAsync);
        ApplyThemeCommand = ReactiveCommand.Create(ApplyTheme);
        ResetThemeCommand = ReactiveCommand.Create(ResetTheme);

        LoadAllSettings();
        UpdateCacheStats();
        SetupSubscriptions();
    }

    // INITIALIZATION

    private void SetupSubscriptions()
    {
        // Language change
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1).WhereNotNull()
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.Data.LanguageCode = lang.Code;
                _library.Save();
            });

        // Network settings -> Mark restart required
        this.WhenAnyValue(
                x => x.SelectedInternetProfile, x => x.ProxyEnabled,
                x => x.ProxyHost, x => x.ProxyPort,
                x => x.ProxyAuth, x => x.ProxyUser, x => x.ProxyPass)
            .Skip(1)
            .Subscribe(_ =>
            {
                NetworkRestartRequired = true;
                SaveNetworkSettings();
            });

        // Storage limits -> Save with throttle
        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(_ => SaveStorageSettings());

        // Theme color changes -> Mark unsaved
        this.WhenAnyValue(
                x => x.AccentColor, x => x.BgPrimaryColor, x => x.BgSecondaryColor,
                x => x.BgElevatedColor, x => x.TextPrimaryColor, x => x.TextSecondaryColor)
            .Skip(1)
            .Subscribe(_ =>
            {
                if (!_isLoadingTheme)
                    HasUnsavedThemeChanges = true;
            });

        // Preset selection -> Apply to color pickers
        this.WhenAnyValue(x => x.SelectedPreset)
            .Skip(1).WhereNotNull()
            .Subscribe(ApplyPresetToColorPickers);

        // Audio settings -> Immediate save
        this.WhenAnyValue(x => x.MaxVolumeLimit).Skip(1).Subscribe(v =>
        {
            _library.Data.MaxVolumeLimit = v;
            _library.Save();
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.TargetGainDb).Skip(1).Subscribe(v =>
        {
            _library.Data.TargetGainDb = v;
            _library.Save();
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.QualityPreference).Skip(1).Subscribe(v =>
        {
            _library.Data.QualityPreference = v;
            _library.Save();
            _youtube.ClearCache();
        });

        // General settings -> Immediate save
        this.WhenAnyValue(x => x.DiscordRpcEnabled).Skip(1).Subscribe(v =>
        {
            _library.Data.DiscordRpcEnabled = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.AutoPlayOnPaste).Skip(1).Subscribe(v =>
        {
            _library.Data.AutoPlayOnUrlPaste = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.EnableSmoothLoading).Skip(1).Subscribe(v =>
        {
            _library.Data.EnableSmoothLoading = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.RememberTrackFormat).Skip(1).Subscribe(v =>
        {
            _library.Data.RememberTrackFormat = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.SearchBatchSize).Skip(1).Subscribe(v =>
        {
            _library.Data.SearchBatchSize = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.EnableSearchCache).Skip(1).Subscribe(v =>
        {
            _library.Data.EnableSearchCache = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.SearchCacheTtlMinutes).Skip(1).Subscribe(v =>
        {
            _library.Data.SearchCacheTtlMinutes = v;
            _library.Save();
            _ = _searchCache.CleanupExpiredAsync();
        });
    }

    private void LoadAllSettings()
    {
        // General & Audio
        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = _library.Data.DiscordRpcEnabled;
        AutoPlayOnPaste = _library.Data.AutoPlayOnUrlPaste;
        SearchBatchSize = _library.Data.SearchBatchSize;
        EnableSmoothLoading = _library.Data.EnableSmoothLoading;
        MaxVolumeLimit = _library.Data.MaxVolumeLimit;
        TargetGainDb = _library.Data.TargetGainDb;
        QualityPreference = _library.Data.QualityPreference;
        RememberTrackFormat = _library.Data.RememberTrackFormat;
        EnableSearchCache = _library.Data.EnableSearchCache;
        SearchCacheTtlMinutes = _library.Data.SearchCacheTtlMinutes;
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == _library.Data.LanguageCode) ?? Languages[0];

        // Account
        FakeChannelInput = _library.FakeAccountUrl ?? "";
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();

        // Network
        SelectedInternetProfile = _library.Data.InternetProfile;
        ProxyEnabled = _library.Data.Proxy.Enabled;
        ProxyHost = _library.Data.Proxy.Host;
        ProxyPort = _library.Data.Proxy.Port;
        ProxyAuth = _library.Data.Proxy.UseAuth;
        ProxyUser = _library.Data.Proxy.Username;
        ProxyPass = _library.Data.Proxy.Password;

        // Storage
        ImageCacheLimitMb = _library.Data.Storage.ImageCacheLimitMb;
        AudioCacheLimitMb = _library.Data.Storage.AudioCacheLimitMb;

        // Theme
        LoadThemeColors();
    }

    // THEME LOGIC

    private void LoadThemeColors()
    {
        var theme = _themeManager.GetCurrentTheme();
        ApplyThemeToColorPickers(theme);
        HasUnsavedThemeChanges = false;
    }

    private void ApplyPresetToColorPickers(ThemeSettings preset)
    {
        ApplyThemeToColorPickers(preset);
        HasUnsavedThemeChanges = true;
    }

    private void ApplyThemeToColorPickers(ThemeSettings theme)
    {
        _isLoadingTheme = true;
        try
        {
            AccentColor = ParseColorSafe(theme.AccentColor);
            BgPrimaryColor = ParseColorSafe(theme.BgPrimary);
            BgSecondaryColor = ParseColorSafe(theme.BgSecondary);
            BgElevatedColor = ParseColorSafe(theme.BgElevated);
            TextPrimaryColor = ParseColorSafe(theme.TextPrimary);
            TextSecondaryColor = ParseColorSafe(theme.TextSecondary);
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void ApplyTheme()
    {
        // Правильное извлечение RGB из Color (без альфа-канала)
        static string GetRgbHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        var theme = new ThemeSettings
        {
            Name = SelectedPreset?.Name ?? L["Theme_Custom"],
            AccentColor = AccentColor.ToString(),
            AccentHover = LightenColor(AccentColor, 0.15).ToString(),
            BgPrimary = BgPrimaryColor.ToString(),
            BgSecondary = BgSecondaryColor.ToString(),
            BgElevated = BgElevatedColor.ToString(),
            BgHighlight = LightenColor(BgSecondaryColor, 0.1).ToString(),
            BgHover = LightenColor(BgSecondaryColor, 0.2).ToString(),
            BgSkeleton = LightenColor(BgSecondaryColor, 0.05).ToString(),
            BgSkeletonDeep = DarkenColor(BgSecondaryColor, 0.2).ToString(),
            // ИСПРАВЛЕНО: правильная генерация overlay с альфа-каналом
            BgOverlay = $"#CC{GetRgbHex(BgPrimaryColor)}",
            TextPrimary = TextPrimaryColor.ToString(),
            TextSecondary = TextSecondaryColor.ToString(),
            TextMuted = DarkenColor(TextSecondaryColor, 0.3).ToString(),
            TextDark = BgPrimaryColor.ToString()
        };

        _themeManager.SaveTheme(theme);
        _themeManager.ApplyTheme(theme);
        HasUnsavedThemeChanges = false;
    }

    private void ResetTheme()
    {
        _themeManager.ResetToDefault();
        LoadThemeColors();
        SelectedPreset = ThemePresets.FirstOrDefault();
    }

    // COLOR HELPERS

    private static Color ParseColorSafe(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Magenta; }
    }

    private static Color LightenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * factor),
            (byte)Math.Min(255, c.G + (255 - c.G) * factor),
            (byte)Math.Min(255, c.B + (255 - c.B) * factor));
    }

    private static Color DarkenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)(c.R * (1 - factor)),
            (byte)(c.G * (1 - factor)),
            (byte)(c.B * (1 - factor)));
    }

    // SAVE HELPERS

    private void SaveNetworkSettings()
    {
        _library.Data.InternetProfile = SelectedInternetProfile;
        _library.Data.Proxy.Enabled = ProxyEnabled;
        _library.Data.Proxy.Host = ProxyHost;
        _library.Data.Proxy.Port = ProxyPort;
        _library.Data.Proxy.UseAuth = ProxyAuth;
        _library.Data.Proxy.Username = ProxyUser;
        _library.Data.Proxy.Password = ProxyPass;
        _library.Save();
    }

    private void SaveStorageSettings()
    {
        _library.Data.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
        _library.Data.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
        _library.Save();
        UpdateCacheStats();
    }

    // CACHE LOGIC

    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearAllAsync();
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        await _streamCache.ClearAllAsync();
        UpdateCacheStats();
    }

    private void UpdateCacheStats()
    {
        var (imgCount, imgSize) = _imageCache.GetStats();
        var audioStats = _streamCache.GetStats();

        ImageCacheStats = $"{imgSize} MB / {ImageCacheLimitMb} MB ({imgCount} {L["Common_Files"]})";
        AudioCacheStats = $"{audioStats.SizeMb} MB / {AudioCacheLimitMb} MB ({audioStats.FileCount} {L["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0
            ? Math.Clamp((double)imgSize / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0
            ? Math.Clamp((double)audioStats.SizeMb / AudioCacheLimitMb, 0, 1) : 0;
    }

    // ACCOUNT LOGIC

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
                RaiseAccountProperties();
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
        RaiseAccountProperties();
    }

    private async Task LoginAsync()
    {
        await _auth.StartLoginAsync();
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();
    }

    private async Task LogoutAsync()
    {
        if (!await _dialog.ConfirmAsync(L["Auth_Logout"], L["Dialog_LogoutMessage"]))
            return;
        _auth.Logout();
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();
    }

    private void RaiseAccountProperties()
    {
        this.RaisePropertyChanged(nameof(AccountName));
        this.RaisePropertyChanged(nameof(AccountAvatarUrl));
        this.RaisePropertyChanged(nameof(AccountSubtitle));
        this.RaisePropertyChanged(nameof(HasAccount));
        this.RaisePropertyChanged(nameof(IsFakeAccount));
    }

    // GENERAL ACTIONS

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await _dialog.SelectFolderAsync(DownloadPath);
        if (string.IsNullOrEmpty(newPath)) return;
        DownloadPath = newPath;
        _library.DownloadPath = newPath;
        _library.Save();
    }

    private async Task ClearHistoryAsync()
    {
        if (!await _dialog.ConfirmAsync(L["Dialog_Confirm_Title"], L["Dialog_ClearHistoryMessage"]))
            return;
        _library.ClearHistory();
        await _dialog.ShowInfoAsync(L["Dialog_Done_Title"], L["Dialog_HistoryCleared"]);
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(L["Dialog_Warning_Title"], L["Dialog_ResetMessage"]))
            return;
        _library.Reset();
        LoadAllSettings();
        await _dialog.ShowInfoAsync(L["Dialog_Done_Title"], L["Dialog_ResetComplete"]);
    }

    // IDISPOSABLE

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}