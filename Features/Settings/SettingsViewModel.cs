// Features/Settings/SettingsViewModel.cs
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Core.Youtube.Utils;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Settings;

public class LocalizedItem<T>(T value, string name)
{
    public T Value { get; } = value;
    public string Name { get; } = name;
}

public enum ImageCachePreset
{
    Custom,
    Low,
    Medium,
    High
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
    private bool _isUpdatingPreset;

    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string FakeChannelInput { get; set; } = string.Empty;
    [Reactive] public bool IsLoadingFakeAccount { get; set; }

    public bool HasAccount => IsAuthenticated || _library.HasFakeAccount;
    public bool IsFakeAccount => !IsAuthenticated && _library.HasFakeAccount;

    public string AccountName => IsAuthenticated
        ? _auth.State.UserName
        : _library.FakeAccountName ?? SL["Auth_NotSignedIn"];

    public string? AccountAvatarUrl => IsAuthenticated
        ? _auth.State.AvatarUrl
        : _library.FakeAccountAvatarUrl;

    public string AccountSubtitle => IsAuthenticated
        ? _auth.State.UserEmail
        : IsFakeAccount ? SL["Account_LimitedAccess"] : SL["Auth_Guest"];

    public ObservableCollection<LocalizedItem<InternetProfile>> InternetProfileOptions { get; } = [];
    [Reactive] public LocalizedItem<InternetProfile>? SelectedInternetProfile { get; set; }

    public ObservableCollection<LocalizedItem<YoutubeClientProfile>> ClientOptions { get; } = [];
    [Reactive] public LocalizedItem<YoutubeClientProfile>? SelectedClient { get; set; }

    [Reactive] public bool ProxyEnabled { get; set; }
    [Reactive] public string ProxyHost { get; set; } = "";
    [Reactive] public int ProxyPort { get; set; } = 8080;
    [Reactive] public bool ProxyAuth { get; set; }
    [Reactive] public string ProxyUser { get; set; } = "";
    [Reactive] public string ProxyPass { get; set; } = "";
    [Reactive] public bool NetworkRestartRequired { get; set; }

    [Reactive] public string DownloadPath { get; set; } = string.Empty;

    public List<LocalizedItem<ImageCachePreset>> ImageCachePresets { get; } = [];
    [Reactive] public LocalizedItem<ImageCachePreset>? SelectedImageCachePreset { get; set; }
    [Reactive] public int MaxBitmapCacheItems { get; set; }

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

        InitializeLists();

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
        this.WhenAnyValue(x => x.SelectedClient)
    .Skip(1).WhereNotNull()
    .Subscribe(c =>
    {
        // 1. Сохраняем в настройки
        _library.UpdateSettings(s => s.YoutubeClient = c.Value);

        // 2. Обновляем статику (чтобы VideoController и HttpHandler увидели изменения)
        YoutubeClientUtils.CurrentProfile = c.Value;

        // 3. Перезагружаем AudioEngine (чтобы обновить HttpClient внутри него)
        _audio.ReinitializeWithProfile(_library.Settings.InternetProfile);

        // 4. Сбрасываем кэш стримов, так как старые ссылки могут быть невалидны для нового клиента
        _youtube.ClearCache();
    });

        LoadAllSettings();
        UpdateCacheStats();
        SetupSubscriptions();

        LocalizationService.Instance.LanguageChanged += (_, _) => RefreshLocalizedLists();
    }

    private void InitializeLists()
    {
        ThemePresets.Clear();
        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        RefreshLocalizedLists();
    }

    private void RefreshLocalizedLists()
    {
        var currentProfile = SelectedInternetProfile?.Value ?? _library.Settings.InternetProfile;
        InternetProfileOptions.Clear();
        foreach (var p in Enum.GetValues<InternetProfile>())
        {
            InternetProfileOptions.Add(new LocalizedItem<InternetProfile>(p, SL[$"NetProfile_{p}"]));
        }
        SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == currentProfile) ?? InternetProfileOptions[1];

        var currentImgPreset = SelectedImageCachePreset?.Value ?? ImageCachePreset.Custom;
        ImageCachePresets.Clear();
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Low, $"{SL["Cache_Low"]} (20)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Medium, $"{SL["Cache_Medium"]} (50)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.High, $"{SL["Cache_High"]} (100)"));

        if (currentImgPreset != ImageCachePreset.Custom)
            SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == currentImgPreset);

        // Clients
        var currentClient = SelectedClient?.Value ?? _library.Settings.YoutubeClient;
        ClientOptions.Clear();
        ClientOptions.Add(new(YoutubeClientProfile.AndroidVR, SL["Client_AndroidVR"]));
        ClientOptions.Add(new(YoutubeClientProfile.TV, SL["Client_TV"]));
        ClientOptions.Add(new(YoutubeClientProfile.Web, SL["Client_Web"]));

        SelectedClient = ClientOptions.FirstOrDefault(x => x.Value == currentClient)
                         ?? ClientOptions[0];
    }

    private void SetupSubscriptions()
    {
        // Changed: Use UpdateSettings instead of Data + Save
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1).WhereNotNull()
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.UpdateSettings(s => s.LanguageCode = lang.Code);
            });

        this.WhenAnyValue(x => x.SelectedInternetProfile).Skip(1).WhereNotNull().Subscribe(p =>
        {
            _library.UpdateSettings(s => s.InternetProfile = p.Value);
            NetworkRestartRequired = true;
        });

        this.WhenAnyValue(x => x.ProxyEnabled, x => x.ProxyHost, x => x.ProxyPort, x => x.ProxyAuth, x => x.ProxyUser, x => x.ProxyPass)
            .Skip(1).Subscribe(x => { NetworkRestartRequired = true; SaveNetworkSettings(); });

        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1).Throttle(TimeSpan.FromMilliseconds(500)).Subscribe(_ => SaveStorageSettings());

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

        this.WhenAnyValue(x => x.MaxBitmapCacheItems)
            .Skip(1)
            .Subscribe(val =>
            {
                _library.UpdateSettings(s => s.Storage.MaxBitmapCacheItems = val);
                _imageCache.EnforceLimits();

                if (!_isUpdatingPreset)
                {
                    _isUpdatingPreset = true;
                    if (val == 20) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low);
                    else if (val == 50) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium);
                    else if (val == 100) SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High);
                    else SelectedImageCachePreset = null;
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
            _library.UpdateSettings(s => s.MaxVolumeLimit = v);
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.TargetGainDb).Skip(1).Subscribe(v =>
        {
            _library.UpdateSettings(s => s.TargetGainDb = v);
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.QualityPreference).Skip(1).Subscribe(v =>
        {
            _library.UpdateSettings(s => s.QualityPreference = v);
            _youtube.ClearCache();
        });

        this.WhenAnyValue(x => x.DiscordRpcEnabled).Skip(1).Subscribe(v =>
            _library.UpdateSettings(s => s.DiscordRpcEnabled = v));

        this.WhenAnyValue(x => x.AutoPlayOnPaste).Skip(1).Subscribe(v =>
            _library.UpdateSettings(s => s.AutoPlayOnUrlPaste = v));

        this.WhenAnyValue(x => x.EnableSmoothLoading).Skip(1).Subscribe(v =>
            _library.UpdateSettings(s => s.EnableSmoothLoading = v));

        this.WhenAnyValue(x => x.RememberTrackFormat).Skip(1).Subscribe(v =>
            _library.UpdateSettings(s => s.RememberTrackFormat = v));

        this.WhenAnyValue(x => x.SearchBatchSize).Skip(1).Subscribe(v =>
            _library.UpdateSettings(s => s.SearchBatchSize = v));

        this.WhenAnyValue(x => x.EnableSearchCache).Skip(1).Subscribe(v =>
            _library.UpdateSettings(s => s.EnableSearchCache = v));

        this.WhenAnyValue(x => x.SearchCacheTtlMinutes).Skip(1).Subscribe(v =>
        {
            _library.UpdateSettings(s => s.SearchCacheTtlMinutes = v);
            _ = _searchCache.CleanupExpiredAsync();
        });
    }

    private void LoadAllSettings()
    {
        var s = _library.Settings;

        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = s.DiscordRpcEnabled;
        AutoPlayOnPaste = s.AutoPlayOnUrlPaste;
        SearchBatchSize = s.SearchBatchSize;
        EnableSmoothLoading = s.EnableSmoothLoading;
        MaxVolumeLimit = s.MaxVolumeLimit;
        TargetGainDb = s.TargetGainDb;
        QualityPreference = s.QualityPreference;
        RememberTrackFormat = s.RememberTrackFormat;
        EnableSearchCache = s.EnableSearchCache;
        SearchCacheTtlMinutes = s.SearchCacheTtlMinutes;
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == s.LanguageCode) ?? Languages[0];

        FakeChannelInput = _library.FakeAccountUrl ?? "";
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();

        var savedProfile = s.InternetProfile;
        SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == savedProfile) ?? InternetProfileOptions[1];

        ProxyEnabled = s.Proxy.Enabled;
        ProxyHost = s.Proxy.Host;
        ProxyPort = s.Proxy.Port;
        ProxyAuth = s.Proxy.UseAuth;
        ProxyUser = s.Proxy.Username;
        ProxyPass = s.Proxy.Password;

        ImageCacheLimitMb = s.Storage.ImageCacheLimitMb;
        AudioCacheLimitMb = s.Storage.AudioCacheLimitMb;

        MaxBitmapCacheItems = s.Storage.MaxBitmapCacheItems > 0 ? s.Storage.MaxBitmapCacheItems : 40;
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
        _library.UpdateSettings(s =>
        {
            s.InternetProfile = SelectedInternetProfile?.Value ?? InternetProfile.Medium;
            s.Proxy.Enabled = ProxyEnabled;
            s.Proxy.Host = ProxyHost;
            s.Proxy.Port = ProxyPort;
            s.Proxy.UseAuth = ProxyAuth;
            s.Proxy.Username = ProxyUser;
            s.Proxy.Password = ProxyPass;
        });
    }

    private void SaveStorageSettings()
    {
        _library.UpdateSettings(s =>
        {
            s.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
            s.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
        });
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
            _auth.SaveCookies(cookies.Trim());

            var (Name, Email, AvatarUrl) = await Program.Services
                .GetRequiredService<YoutubeUserDataService>()
                .GetAccountInfoAsync();

            _auth.UpdateUserProfile(Name, Email, AvatarUrl);

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
    }

    private async Task ClearHistoryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Dialog_ClearHistoryMessage"]))
            return;
        await _library.ClearHistoryAsync();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_HistoryCleared"]);
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"]))
            return;
        await _library.ResetAsync();
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