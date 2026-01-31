// Core/Models/AppSettings.cs
namespace LMP.Core.Models;

public enum YoutubeClientProfile
{
    AndroidVR, // Oculus Quest (Текущий рабочий)
    TV,        // Smart TV / Console (Резервный)
    Web,       // Обычный браузер (Требует n-token, но иногда работает)
    // iOS/Android пока убираем, так как они 100% требуют PO Token
}

public enum InternetProfile
{
    Low,      // Экономия трафика / Медленный интернет
    Medium,   // Баланс (по умолчанию)
    High,     // Высокое качество / Быстрый интернет
    Ultra     // Максимальное кэширование / Локальная сеть
}

public class ProxySettings
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public bool UseAuth { get; set; } = false;
    public string Username { get; set; } = "";
    public string Password { get; set; } = ""; // В реальном приложении стоит шифровать
}

public class StorageSettings
{
    public int ImageCacheLimitMb { get; set; } = 500;
    public int AudioCacheLimitMb { get; set; } = 2048;
    public int MaxBitmapCacheItems { get; set; } = 40; 
}

public class StreamingConfig
{
    // Генерируется динамически на основе InternetProfile, но можно переопределить
    public int ChunkSize { get; set; } = 128 * 1024;
    public int ReadAheadChunks { get; set; } = 3;
    public int MaxConcurrentDownloads { get; set; } = 4;
    public int DownloadTimeoutMs { get; set; } = 45000;
    public int VlcNetworkCachingMs { get; set; } = 2000;
    public int MaxRamChunks { get; set; } = 128; // ~16MB при 128KB чанках
}

public enum RepeatMode
{
    None,
    RepeatOne,
    RepeatAll
}

public enum AudioQualityPreference
{
    BestAvailable,
    Standard
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