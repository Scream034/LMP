namespace MyLiteMusicPlayer.Models;

public class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public string? Author { get; set; }
    public string? YoutubePlaylistId { get; set; }

    public bool IsLocal { get; set; } = true;
    public bool IsFromAccount { get; set; } // Из Google аккаунта

    // Новые настройки синхронизации
    public bool AllowOffline { get; set; } = true;
    public bool AllowNetwork { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<string> TrackIds { get; set; } = [];

    public int TrackCount => TrackIds.Count;
}