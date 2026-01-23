namespace MyLiteMusicPlayer.Core.Models;

public class LibraryData
{
    public Dictionary<string, TrackInfo> Tracks { get; set; } = [];
    public Dictionary<string, Playlist> Playlists { get; set; } = [];
    public List<string> LikedTrackIds { get; set; } = [];
    public List<string> RecentlyPlayedIds { get; set; } = [];

    // --- Search History ---
    public string LastSearchQuery { get; set; } = "";
    public List<string> SearchHistory { get; set; } = [];

    // --- Fake Account (только URL, остальное кэшируется в памяти) ---
    public string? FakeAccountChannelUrl { get; set; }

    // --- Settings ---
    public float Volume { get; set; } = 0.5f;
    public int LastVolume { get; set; } = 50;
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;
    public double PlaylistHeaderHeight { get; set; } = 320;
    public int MaxVolumeLimit { get; set; } = 100;
    public float TargetGainDb { get; set; } = 0f;
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;
    public int LoadBatchSize { get; set; } = 20;
    public int SearchBatchSize { get; set; } = 30;
    public bool EnableSearchCache { get; set; } = true;
    public int SearchCacheTtlMinutes { get; set; } = 120; // 2 часа по умолчанию
    public bool EnableSmoothLoading { get; set; } = true;
    public AudioQualityPreference QualityPreference { get; set; } = AudioQualityPreference.BestAvailable;
    public bool RememberTrackFormat { get; set; } = true;
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
