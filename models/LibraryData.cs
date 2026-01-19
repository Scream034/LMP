namespace MyLiteMusicPlayer.Models;

public class LibraryData
{
    public Dictionary<string, TrackInfo> Tracks { get; set; } = new();
    public Dictionary<string, Playlist> Playlists { get; set; } = new();
    public List<string> LikedTrackIds { get; set; } = new();
    public List<string> RecentlyPlayedIds { get; set; } = new();
    
    // Настройки
    public float Volume { get; set; } = 0.5f;
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;
    
    // Настройки приложения
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;
}

public enum RepeatMode
{
    None,
    RepeatOne,
    RepeatAll
}