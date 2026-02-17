using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
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

    private bool _isLoadingTheme;
    private bool _isUpdatingPreset;
    private bool _isLoadingSettings;

    #region Account

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

    #endregion

    #region Network

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

    #endregion

    #region Storage

    [Reactive] public string DownloadPath { get; set; } = string.Empty;

    public List<LocalizedItem<ImageCachePreset>> ImageCachePresets { get; } = [];
    [Reactive] public LocalizedItem<ImageCachePreset>? SelectedImageCachePreset { get; set; }
    [Reactive] public int MaxBitmapCacheItems { get; set; }

    [Reactive] public int ImageCacheLimitMb { get; set; }
    [Reactive] public int AudioCacheLimitMb { get; set; }
    [Reactive] public int DownloadedTracksLimitMb { get; set; }

    [Reactive] public string ImageCacheStats { get; private set; } = "...";
    [Reactive] public string AudioCacheStats { get; private set; } = "...";
    [Reactive] public double ImageCacheUsagePercent { get; private set; }
    [Reactive] public double AudioCacheUsagePercent { get; private set; }
    [Reactive] public string DownloadsStats { get; private set; } = "...";
    [Reactive] public double DownloadsUsagePercent { get; private set; }
    [Reactive] public bool AutoSaveToDownloads { get; set; }

    #endregion

    #region Theme

    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];
    [Reactive] public ThemeSettings? SelectedPreset { get; set; }
    [Reactive] public Color AccentColor { get; set; }
    [Reactive] public Color BgPrimaryColor { get; set; }
    [Reactive] public Color BgSecondaryColor { get; set; }
    [Reactive] public Color BgElevatedColor { get; set; }
    [Reactive] public Color TextPrimaryColor { get; set; }
    [Reactive] public Color TextSecondaryColor { get; set; }
    [Reactive] public bool HasUnsavedThemeChanges { get; set; }

    #endregion

    #region Audio

    public List<AudioQualityPreference> QualityOptions { get; } = [.. Enum.GetValues<AudioQualityPreference>()];
    
    [Reactive] public int MaxVolumeLimit { get; set; }
    [Reactive] public float TargetGainDb { get; set; }
    [Reactive] public AudioQualityPreference QualityPreference { get; set; }
    [Reactive] public bool RememberTrackFormat { get; set; }

    // Новые настройки аудио
    [Reactive] public bool VolumeBoostEnabled { get; set; }
    [Reactive] public bool SmoothVolumeEnabled { get; set; }
    [Reactive] public bool AudioNormalizationEnabled { get; set; }

    public List<LocalizedItem<VolumeCurveType>> VolumeCurveOptions { get; } = [];
    [Reactive] public LocalizedItem<VolumeCurveType>? SelectedVolumeCurve { get; set; }

    #endregion

    #region UI & Behavior

    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }
    [Reactive] public int SearchBatchSize { get; set; }
    [Reactive] public bool EnableSearchCache { get; set; }
    [Reactive] public int SearchCacheTtlMinutes { get; set; }

    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    #endregion

    #region Commands

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
    public ReactiveCommand<Unit, Unit> ClearDownloadsCommand { get; }

    #endregion

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

        // Commands
        LoginCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoginAsync));
        LogoutCommand = CreateCommand(ReactiveCommand.CreateFromTask(LogoutAsync));
        SetFakeAccountCommand = CreateCommand(ReactiveCommand.CreateFromTask(SetFakeAccountAsync));
        ClearFakeAccountCommand = CreateCommand(ReactiveCommand.Create(ClearFakeAccount));
        BrowseDownloadPathCommand = CreateCommand(ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync));
        ClearHistoryCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearHistoryAsync));
        ResetLibraryCommand = CreateCommand(ReactiveCommand.CreateFromTask(ResetLibraryAsync));
        ClearImageCacheCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearImageCacheAsync));
        ClearAudioCacheCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearAudioCacheAsync));
        ApplyThemeCommand = CreateCommand(ReactiveCommand.Create(ApplyTheme));
        ResetThemeCommand = CreateCommand(ReactiveCommand.Create(ResetTheme));
        ClearDownloadsCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearDownloadsAsync));

        // Client change subscription
        this.WhenAnyValue(x => x.SelectedClient)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(async c =>
            {
                _library.UpdateSettings(s => s.YoutubeClient = c.Value);
                YoutubeClientUtils.CurrentProfile = c.Value;
                await AudioEngine.ReinitializeWithProfileAsync(_library.Settings.InternetProfile);
                _youtube.ClearCache();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.DownloadedTracksLimitMb)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Storage.DownloadedTracksLimitMb = v);
                UpdateCacheStats();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AutoSaveToDownloads)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.Storage.AutoSaveToDownloads = v))
            .DisposeWith(Disposables);

        LoadAllSettings();
        UpdateCacheStats();
        SetupSubscriptions();

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, string e) => RefreshLocalizedLists();

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
            InternetProfileOptions.Add(new LocalizedItem<InternetProfile>(p, SL[$"NetProfile_{p}"]));
        SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == currentProfile) 
                                  ?? InternetProfileOptions[1];

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
        SelectedClient = ClientOptions.FirstOrDefault(x => x.Value == currentClient) ?? ClientOptions[0];

        // Volume curves
        var currentCurve = SelectedVolumeCurve?.Value ?? _library.Settings.Audio.VolumeCurve;
        VolumeCurveOptions.Clear();
        VolumeCurveOptions.Add(new(VolumeCurveType.Linear, SL["VolumeCurve_Linear"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Quadratic, SL["VolumeCurve_Quadratic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Logarithmic, SL["VolumeCurve_Logarithmic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Cubic, SL["VolumeCurve_Cubic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.SpeedOfLight, SL["VolumeCurve_SpeedOfLight"]));
        SelectedVolumeCurve = VolumeCurveOptions.FirstOrDefault(x => x.Value == currentCurve) 
                              ?? VolumeCurveOptions[1];
    }

    private void SetupSubscriptions()
    {
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.UpdateSettings(s => s.LanguageCode = lang.Code);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedInternetProfile)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(p =>
            {
                _library.UpdateSettings(s => s.InternetProfile = p.Value);
                NetworkRestartRequired = true;
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ProxyEnabled, x => x.ProxyHost, x => x.ProxyPort, 
                          x => x.ProxyAuth, x => x.ProxyUser, x => x.ProxyPass)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(_ => { NetworkRestartRequired = true; SaveNetworkSettings(); })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(_ => SaveStorageSettings())
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedImageCachePreset)
            .Skip(1)
            .Where(p => !_isUpdatingPreset && !_isLoadingSettings && p != null)
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
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.MaxBitmapCacheItems)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(val =>
            {
                _library.UpdateSettings(s => s.Storage.MaxBitmapCacheItems = val);
                _imageCache.EnforceLimits();

                if (!_isUpdatingPreset)
                {
                    _isUpdatingPreset = true;
                    SelectedImageCachePreset = val switch
                    {
                        20 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low),
                        50 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium),
                        100 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High),
                        _ => null
                    };
                    _isUpdatingPreset = false;
                }
            })
            .DisposeWith(Disposables);

        // Theme colors
        this.WhenAnyValue(
                x => x.AccentColor, x => x.BgPrimaryColor, x => x.BgSecondaryColor,
                x => x.BgElevatedColor, x => x.TextPrimaryColor, x => x.TextSecondaryColor)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(_ =>
            {
                if (!_isLoadingTheme)
                    HasUnsavedThemeChanges = true;
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedPreset)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(ApplyPresetToColorPickers)
            .DisposeWith(Disposables);

        // Audio settings
        this.WhenAnyValue(x => x.MaxVolumeLimit)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.MaxVolumeLimit = v);
                _audio.OnMaxVolumeLimitChanged(v);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.TargetGainDb)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.TargetGainDb = v);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.QualityPreference)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.QualityPreference = v);
                _youtube.ClearCache();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.VolumeBoostEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Audio.VolumeBoostEnabled = v);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SmoothVolumeEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Audio.SmoothVolumeEnabled = v);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AudioNormalizationEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Audio.NormalizationEnabled = v);
                // TODO: Включить/выключить нормализацию в AudioEngine
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedVolumeCurve)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(c =>
            {
                _library.UpdateSettings(s => s.Audio.VolumeCurve = c.Value);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        // UI settings
        this.WhenAnyValue(x => x.DiscordRpcEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.DiscordRpcEnabled = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AutoPlayOnPaste)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.AutoPlayOnUrlPaste = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.EnableSmoothLoading)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.EnableSmoothLoading = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.RememberTrackFormat)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.RememberTrackFormat = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SearchBatchSize)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.SearchBatchSize = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.EnableSearchCache)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.EnableSearchCache = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SearchCacheTtlMinutes)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.SearchCacheTtlMinutes = v);
                _ = _searchCache.CleanupExpiredAsync();
            })
            .DisposeWith(Disposables);
    }

    private void LoadAllSettings()
    {
        _isLoadingSettings = true;
        
        try
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

            // Audio settings
            VolumeBoostEnabled = s.Audio.VolumeBoostEnabled;
            SmoothVolumeEnabled = s.Audio.SmoothVolumeEnabled;
            AudioNormalizationEnabled = s.Audio.NormalizationEnabled;
            SelectedVolumeCurve = VolumeCurveOptions.FirstOrDefault(x => x.Value == s.Audio.VolumeCurve)
                                  ?? VolumeCurveOptions[1];

            FakeChannelInput = _library.FakeAccountUrl ?? "";
            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();

            var savedProfile = s.InternetProfile;
            SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == savedProfile) 
                                      ?? InternetProfileOptions[1];

            var savedClient = s.YoutubeClient;
            SelectedClient = ClientOptions.FirstOrDefault(x => x.Value == savedClient) 
                             ?? ClientOptions[0];

            ProxyEnabled = s.Proxy.Enabled;
            ProxyHost = s.Proxy.Host;
            ProxyPort = s.Proxy.Port;
            ProxyAuth = s.Proxy.UseAuth;
            ProxyUser = s.Proxy.Username;
            ProxyPass = s.Proxy.Password;

            ImageCacheLimitMb = s.Storage.ImageCacheLimitMb;
            AudioCacheLimitMb = s.Storage.AudioCacheLimitMb;
            DownloadedTracksLimitMb = s.Storage.DownloadedTracksLimitMb;
            AutoSaveToDownloads = s.Storage.AutoSaveToDownloads;

            // Image cache preset
            MaxBitmapCacheItems = s.Storage.MaxBitmapCacheItems > 0 ? s.Storage.MaxBitmapCacheItems : 40;
            _isUpdatingPreset = true;
            SelectedImageCachePreset = MaxBitmapCacheItems switch
            {
                20 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low),
                50 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium),
                100 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High),
                _ => null
            };
            _isUpdatingPreset = false;

            LoadThemeColors();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void LoadThemeColors()
    {
        _isLoadingTheme = true;
        try
        {
            var currentTheme = _themeManager.GetCurrentTheme();
            ApplyThemeToColorPickers(currentTheme);

            // Находим соответствующий пресет
            var matchingPreset = ThemePresets.FirstOrDefault(p =>
                p.AccentColor == currentTheme.AccentColor &&
                p.BgPrimary == currentTheme.BgPrimary);

            SelectedPreset = matchingPreset ?? ThemePresets.FirstOrDefault();

            HasUnsavedThemeChanges = false;
        }
        finally
        {
            _isLoadingTheme = false;
        }
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
        await _imageCache.ClearDiskCacheAsync();
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        await _streamCache.ClearAllAsync();
        UpdateCacheStats();
    }

    private void UpdateCacheStats()
    {
        var (memItems, _, imgCount, imgSizeMb) = _imageCache.GetStats();
        var (audioFileCount, audioSizeMb) = StreamCacheManager.GetStats();
        var (downloadFileCount, downloadSizeMb) = StreamCacheManager.GetDownloadsStats();

        ImageCacheStats = $"{imgSizeMb} MB / {ImageCacheLimitMb} MB ({imgCount} {SL["Common_Files"]}, RAM: {memItems})";
        AudioCacheStats = $"{audioSizeMb} MB / {AudioCacheLimitMb} MB ({audioFileCount} {SL["Common_Files"]})";
        DownloadsStats = $"{downloadSizeMb} MB / {DownloadedTracksLimitMb} MB ({downloadFileCount} {SL["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0
            ? Math.Clamp((double)imgSizeMb / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0
            ? Math.Clamp((double)audioSizeMb / AudioCacheLimitMb, 0, 1) : 0;
        DownloadsUsagePercent = DownloadedTracksLimitMb > 0
            ? Math.Clamp((double)downloadSizeMb / DownloadedTracksLimitMb, 0, 1) : 0;
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
        var cookies = await _dialog.ShowInputAsync(SL["Dialog_Login_Title"],
            SL["Dialog_LoginMessage"]);

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

    private async Task ClearDownloadsAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Settings_ClearDownloadsConfirm"]))
            return;

        await StreamCacheManager.ClearDownloadsAsync();
        UpdateCacheStats();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        }

        base.Dispose(disposing);
    }
}