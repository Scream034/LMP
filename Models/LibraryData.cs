namespace MyLiteMusicPlayer.Models;

/// <summary>
/// Данные библиотеки и настройки пользователя, сохраняемые в JSON.
/// </summary>
public class LibraryData
{
    public Dictionary<string, TrackInfo> Tracks { get; set; } = [];
    public Dictionary<string, Playlist> Playlists { get; set; } = [];
    public List<string> LikedTrackIds { get; set; } = [];
    public List<string> RecentlyPlayedIds { get; set; } = [];

    // --- Настройки плеера ---
    public float Volume { get; set; } = 0.5f;

    // Для запоминания громкости перед Mute
    public int LastVolume { get; set; } = 50;

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    // --- Настройки звука ---
    public int MaxVolumeLimit { get; set; } = 100;
    public float TargetGainDb { get; set; } = 0f;

    // --- Настройки приложения ---
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;

    public int LoadBatchSize { get; set; } = 20;
    public bool EnableSmoothLoading { get; set; } = true;

    public AudioQualityPreference QualityPreference { get; set; } = AudioQualityPreference.BestAvailable;
    public bool RememberTrackFormat { get; set; } = true; // Запоминать ли выбор формата для треков
}

public enum RepeatMode
{
    None,      // Исправлено: Было Off в коде, но None в модели
    RepeatOne,
    RepeatAll
}

public enum AudioQualityPreference
{
    BestAvailable, // Преимущественно Opus (WebM)
    Standard       // Преимущественно AAC (MP4), совместимость
}