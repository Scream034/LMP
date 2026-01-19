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

    private string _thumbnailUrl = string.Empty;
    public string ThumbnailUrl
    {
        get
        {
            // Если есть URL, возвращаем его
            if (!string.IsNullOrEmpty(_thumbnailUrl))
                return _thumbnailUrl;

            // Fallback: генерируем URL превью YouTube по ID
            if (Id.StartsWith("yt_") && Id.Length > 3)
            {
                var videoId = Id.Substring(3);
                // YouTube thumbnail URLs
                return $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
            }

            return string.Empty;
        }
        set => _thumbnailUrl = value;
    }

    // Для проверки - есть ли реальный thumbnail
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    // Состояние в библиотеке
    public bool IsLiked { get; set; }
    public bool IsDisliked { get; set; }
    public bool IsDownloaded { get; set; }
    public string? LocalPath { get; set; }

    // В каких плейлистах находится
    public HashSet<string> InPlaylists { get; set; } = [];

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
        _thumbnailUrl = _thumbnailUrl,
        IsLiked = IsLiked,
        IsDisliked = IsDisliked,
        IsDownloaded = IsDownloaded,
        LocalPath = LocalPath,
        InPlaylists = new HashSet<string>(InPlaylists),
        RadioSeedId = RadioSeedId
    };
}