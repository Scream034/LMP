using System;
using System.Collections.Generic;

namespace MyLiteMusicPlayer.Models;

public class TrackInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    
    // Состояние в библиотеке
    public bool IsLiked { get; set; }
    public bool IsDisliked { get; set; }
    public bool IsDownloaded { get; set; }
    public string? LocalPath { get; set; }
    
    // В каких плейлистах находится
    public HashSet<string> InPlaylists { get; set; } = new();
    
    // Метаданные для радио
    public string? RadioSeedId { get; set; }
    
    public TrackInfo Clone() => new()
    {
        Id = Id,
        Title = Title,
        Author = Author,
        Url = Url,
        StreamUrl = StreamUrl,
        Duration = Duration,
        ThumbnailUrl = ThumbnailUrl,
        IsLiked = IsLiked,
        IsDisliked = IsDisliked,
        IsDownloaded = IsDownloaded,
        LocalPath = LocalPath,
        InPlaylists = new HashSet<string>(InPlaylists),
        RadioSeedId = RadioSeedId
    };
}