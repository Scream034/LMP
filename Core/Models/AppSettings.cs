// Core/Models/AppSettings.cs
namespace LMP.Core.Models;

public enum YoutubeClientProfile
{
    AndroidVR, // Oculus Quest (Текущий рабочий)
    TV,        // Smart TV / Console (Резервный)
    Web,       // Обычный браузер (Требует n-token, но иногда работает)
    // iOS/Android пока убираем, так как они 100% требуют PO Token
}

/// <summary>
/// Application settings. Stored as JSON in Settings table.
/// </summary>
public class AppSettings
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

    // === Fake Account ===
    public string? FakeAccountChannelUrl { get; set; }

    // === Search ===
    public string LastSearchQuery { get; set; } = "";
    public List<string> SearchHistory { get; set; } = [];
}