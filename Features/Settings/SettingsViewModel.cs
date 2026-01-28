using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Settings;

// Вспомогательный класс для локализации Enum в ComboBox
public class LocalizedItem<T>(T value, string name)
{
    public T Value { get; } = value;
    public string Name { get; } = name;
}

public enum ImageCachePreset
{
    Custom,
    Low,    // 20 items (Economy)
    Medium, // 50 items (Balanced)
    High    // 100 items (Smooth)
}

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly StreamCacheManager _streamCache;
    private readonly ThemeManagerService _themeManager;
    private readonly CookieAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    private bool _isDisposed;
    private bool _isLoadingTheme;
    private bool _isUpdatingPreset; // Флаг для предотвращения циклов

    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string FakeChannelInput { get; set; } = string.Empty;
    [Reactive] public bool IsLoadingFakeAccount { get; set; }

    public bool HasAccount => IsAuthenticated || _library.HasFakeAccount;
    public bool IsFakeAccount => !IsAuthenticated && _library.HasFakeAccount;

    public string AccountName => IsAuthenticated
        ? _auth.State.UserName // Берем сохраненное имя
        : _library.FakeAccountName ?? SL["Auth_NotSignedIn"];

    public string? AccountAvatarUrl => IsAuthenticated
        ? _auth.State.AvatarUrl // Берем сохраненный аватар
        : _library.FakeAccountAvatarUrl;

    public string AccountSubtitle => IsAuthenticated
        ? _auth.State.UserEmail // Показываем email если есть
        : IsFakeAccount ? SL["Account_LimitedAccess"] : SL["Auth_Guest"];

    public ObservableCollection<LocalizedItem<InternetProfile>> InternetProfileOptions { get; } = [];
    [Reactive] public LocalizedItem<InternetProfile>? SelectedInternetProfile { get; set; }
    [Reactive] public bool ProxyEnabled { get; set; }
    [Reactive] public string ProxyHost { get; set; } = "";
    [Reactive] public int ProxyPort { get; set; } = 8080;
    [Reactive] public bool ProxyAuth { get; set; }
    [Reactive] public string ProxyUser { get; set; } = "";
    [Reactive] public string ProxyPass { get; set; } = "";
    [Reactive] public bool NetworkRestartRequired { get; set; }

    [Reactive] public string DownloadPath { get; set; } = string.Empty;

    // Image Cache Settings
    public List<LocalizedItem<ImageCachePreset>> ImageCachePresets { get; } = [];
    [Reactive] public LocalizedItem<ImageCachePreset>? SelectedImageCachePreset { get; set; }
    [Reactive] public int MaxBitmapCacheItems { get; set; } // Slider Value

    [Reactive] public int ImageCacheLimitMb { get; set; }
    [Reactive] public int AudioCacheLimitMb { get; set; }

    [Reactive] public string ImageCacheStats { get; private set; } = "...";
    [Reactive] public string AudioCacheStats { get; private set; } = "...";
    [Reactive] public double ImageCacheUsagePercent { get; private set; }
    [Reactive] public double AudioCacheUsagePercent { get; private set; }

    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];
    [Reactive] public ThemeSettings? SelectedPreset { get; set; }
    [Reactive] public Color AccentColor { get; set; }
    [Reactive] public Color BgPrimaryColor { get; set; }
    [Reactive] public Color BgSecondaryColor { get; set; }
    [Reactive] public Color BgElevatedColor { get; set; }
    [Reactive] public Color TextPrimaryColor { get; set; }
    [Reactive] public Color TextSecondaryColor { get; set; }
    [Reactive] public bool HasUnsavedThemeChanges { get; set; }

    public List<AudioQualityPreference> QualityOptions { get; } = [.. Enum.GetValues<AudioQualityPreference>()];
    [Reactive] public int MaxVolumeLimit { get; set; }
    [Reactive] public float TargetGainDb { get; set; }
    [Reactive] public AudioQualityPreference QualityPreference { get; set; }
    [Reactive] public bool RememberTrackFormat { get; set; }

    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }
    [Reactive] public int SearchBatchSize { get; set; }
    [Reactive] public bool EnableSearchCache { get; set; }
    [Reactive] public int SearchCacheTtlMinutes { get; set; }

    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

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

    public SettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        StreamCacheManager streamCache,
        ThemeManagerService themeManager,
        CookieAuthService auth,
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

        InitializeLists(); // Заполняем списки (Network profiles, Image presets, Themes)

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

        // Обновляем локализацию списков при смене языка
        LocalizationService.Instance.LanguageChanged += (_, _) => RefreshLocalizedLists();
    }

    private void InitializeLists()
    {
        // Themes
        ThemePresets.Clear();
        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        RefreshLocalizedLists();
    }

    private void RefreshLocalizedLists()
    {
        // 1. Internet Profiles
        var currentProfile = SelectedInternetProfile?.Value ?? _library.Data.InternetProfile;
        InternetProfileOptions.Clear();
        foreach (var p in Enum.GetValues<InternetProfile>())
        {
            InternetProfileOptions.Add(new LocalizedItem<InternetProfile>(p, SL[$"NetProfile_{p}"]));
        }
        SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == currentProfile) ?? InternetProfileOptions[1];

        // 2. Image Cache Presets
        var currentImgPreset = SelectedImageCachePreset?.Value ?? ImageCachePreset.Custom;
        ImageCachePresets.Clear();
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Low, $"{SL["Cache_Low"]} (20)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Medium, $"{SL["Cache_Medium"]} (50)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.High, $"{SL["Cache_High"]} (100)"));
        // Custom не добавляем в список для выбора пользователем, он ставится автоматически

        // Восстанавливаем выбор (если был Custom, сбрасываем выделение, слайдер покажет значение)
        if (currentImgPreset != ImageCachePreset.Custom)
            SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == currentImgPreset);
    }

    private void SetupSubscriptions()
    {
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1).WhereNotNull()
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.Data.LanguageCode = lang.Code;
                _library.Save();
            });

        // Network Profile
        this.WhenAnyValue(x => x.SelectedInternetProfile).Skip(1).WhereNotNull().Subscribe(p =>
        {
            _library.Data.InternetProfile = p.Value;
            NetworkRestartRequired = true;
            _library.Save();
        });

        // Proxy settings
        this.WhenAnyValue(x => x.ProxyEnabled, x => x.ProxyHost, x => x.ProxyPort, x => x.ProxyAuth, x => x.ProxyUser, x => x.ProxyPass)
            .Skip(1).Subscribe(x => { NetworkRestartRequired = true; SaveNetworkSettings(); });

        // Storage Limits
        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1).Throttle(TimeSpan.FromMilliseconds(500)).Subscribe(_ => SaveStorageSettings());

        // Image Cache Preset Logic
        this.WhenAnyValue(x => x.SelectedImageCachePreset)
            .Skip(1)
            .Where(p => !_isUpdatingPreset && p != null)
            .Subscribe(p =>
            {
                _isUpdatingPreset = true;
                MaxBitmapCacheItems = p!.Value switch
                {
                    ImageCachePreset.Low => 20,
                    ImageCachePreset.Medium => 50,
                    ImageCachePreset.High => 100,
                    _ => MaxBitmapCacheItems
                };
                _isUpdatingPreset = false;
            });

        // Slider Logic -> Custom Preset
        this.WhenAnyValue(x => x.MaxBitmapCacheItems)
            .Skip(1)
            .Subscribe(val =>
            {
                _library.Data.Storage.MaxBitmapCacheItems = val;
                _library.Save();
                _imageCache.EnforceLimits(); // Применяем немедленно!

                if (!_isUpdatingPreset)
                {
                    _isUpdatingPreset = true;
                    // Если значение совпадает с пресетом, выбираем его, иначе null (Custom)
                    if (val == 20) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low);
                    else if (val == 50) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium);
                    else if (val == 100) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High);
                    else SelectedImageCachePreset = null; // Custom visual state
                    _isUpdatingPreset = false;
                }
            });

        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(_ => SaveStorageSettings());

        this.WhenAnyValue(
                x => x.AccentColor, x => x.BgPrimaryColor, x => x.BgSecondaryColor,
                x => x.BgElevatedColor, x => x.TextPrimaryColor, x => x.TextSecondaryColor)
            .Skip(1)
            .Subscribe(_ =>
            {
                if (!_isLoadingTheme)
                    HasUnsavedThemeChanges = true;
            });

        this.WhenAnyValue(x => x.SelectedPreset)
            .Skip(1).WhereNotNull()
            .Subscribe(ApplyPresetToColorPickers);

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

        FakeChannelInput = _library.FakeAccountUrl ?? "";
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();

        // Network (через wrapper)
        var savedProfile = _library.Data.InternetProfile;
        SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == savedProfile) ?? InternetProfileOptions[1];

        ProxyEnabled = _library.Data.Proxy.Enabled;
        ProxyHost = _library.Data.Proxy.Host;
        ProxyPort = _library.Data.Proxy.Port;
        ProxyAuth = _library.Data.Proxy.UseAuth;
        ProxyUser = _library.Data.Proxy.Username;
        ProxyPass = _library.Data.Proxy.Password;

        ImageCacheLimitMb = _library.Data.Storage.ImageCacheLimitMb;
        AudioCacheLimitMb = _library.Data.Storage.AudioCacheLimitMb;

        // Image Cache Items
        MaxBitmapCacheItems = _library.Data.Storage.MaxBitmapCacheItems > 0 ? _library.Data.Storage.MaxBitmapCacheItems : 40;
        // Preset auto-select logic runs via subscription on MaxBitmapCacheItems change or manually:
        if (MaxBitmapCacheItems == 20) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low);
        else if (MaxBitmapCacheItems == 50) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium);
        else if (MaxBitmapCacheItems == 100) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High);

        LoadThemeColors();
    }

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
        static string GetRgbHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        var theme = new ThemeSettings
        {
            Name = SelectedPreset?.Name ?? SL["Theme_Custom"],
            AccentColor = AccentColor.ToString(),
            AccentHover = LightenColor(AccentColor, 0.15).ToString(),
            BgPrimary = BgPrimaryColor.ToString(),
            BgSecondary = BgSecondaryColor.ToString(),
            BgElevated = BgElevatedColor.ToString(),
            BgHighlight = LightenColor(BgSecondaryColor, 0.1).ToString(),
            BgHover = LightenColor(BgSecondaryColor, 0.2).ToString(),
            BgSkeleton = LightenColor(BgSecondaryColor, 0.05).ToString(),
            BgSkeletonDeep = DarkenColor(BgSecondaryColor, 0.2).ToString(),
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

    private void SaveNetworkSettings()
    {
        _library.Data.InternetProfile = SelectedInternetProfile?.Value ?? InternetProfile.Medium;
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
        var audioStats = StreamCacheManager.GetStats();

        ImageCacheStats = $"{imgSize} MB / {ImageCacheLimitMb} MB ({imgCount} {SL["Common_Files"]})";
        AudioCacheStats = $"{audioStats.SizeMb} MB / {AudioCacheLimitMb} MB ({audioStats.FileCount} {SL["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0
            ? Math.Clamp((double)imgSize / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0
            ? Math.Clamp((double)audioStats.SizeMb / AudioCacheLimitMb, 0, 1) : 0;
    }

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
                await _dialog.ShowInfoAsync(SL["Dialog_Success"],
                    string.Format(SL["Dialog_Merge_Success"], info.Value.Name));
            }
            else
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Dialog_Merge_Error"]);
            }
        }
        catch (Exception ex)
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], ex.Message);
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
        var cookies = await _dialog.ShowInputAsync(SL["Dialog_Auth_Title"],
            SL["Dialog_AuthMessage"]);

        if (!string.IsNullOrWhiteSpace(cookies))
        {
            // 1. Сохраняем куки
            _auth.SaveCookies(cookies.Trim());

            // 2. Получаем инфо об аккаунте
            var (Name, Email, AvatarUrl) = await Program.Services
                .GetRequiredService<YoutubeUserDataService>()
                .GetAccountInfoAsync();

            // 3. Сохраняем инфо в AuthState
            _auth.UpdateUserProfile(Name, Email, AvatarUrl);

            // 4. Обновляем UI
            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();

            await _dialog.ShowInfoAsync(SL["Dialog_Success"], string.Format(SL["Auth_LoggedInAs"], Name));
        }
    }

    private async Task LogoutAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Auth_Logout"], SL["Dialog_LogoutMessage"]))
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
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Dialog_ClearHistoryMessage"]))
            return;
        _library.ClearHistory();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_HistoryCleared"]);
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"]))
            return;
        _library.Reset();
        LoadAllSettings();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}