using System.Text.Json.Serialization;

namespace LMP.Core.Models;

public class TrackInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public bool IsOfficialArtist { get; set; } = false;
    public bool IsMusic { get; set; } = false;
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
                var videoId = Id[3..];
                // YouTube thumbnail URLs
                return $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
            }

            return string.Empty;
        }
        set => _thumbnailUrl = value;
    }

    /// <summary>
    /// Для проверки - есть ли реальный thumbnail
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    /// Состояние в библиотеке
    public bool IsLiked { get; set; }
    public bool IsDisliked { get; set; }
    public bool IsDownloaded { get; set; }
    public string? LocalPath { get; set; }

    /// <summary>
    /// В каких плейлистах находится
    /// </summary>
    public HashSet<string> InPlaylists { get; set; } = [];

    /// <summary>
    /// Метаданные для радио
    /// </summary>
    public string? RadioSeedId { get; set; }

    /// <summary>
    /// Сохраняем предпочтительный кодек для этого трека (например: "webm" или "mp4")
    /// </summary>
    public string? PreferredContainer { get; set; }

    /// <summary>
    /// Предпочтительный битрейт (для точного выбора формата)
    /// </summary>
    public int PreferredBitrate { get; set; }

    /// <summary>
    /// Временный выбор контейнера для текущей сессии (ручное переключение).
    /// Имеет приоритет над сохраненным. Не сохраняется на диск.
    /// </summary>
    [JsonIgnore]
    public string? TransientContainer { get; set; }

    /// <summary>
    /// Временный выбор битрейта для текущей сессии.
    /// </summary>
    [JsonIgnore]
    public int TransientBitrate { get; set; }

    /// <summary>Закэшированный кодек (Opus/AAC/etc)</summary>
    [JsonIgnore]
    public string CachedCodec { get; set; } = "";

    /// <summary>Закэшированный битрейт</summary>
    [JsonIgnore]
    public int CachedBitrate { get; set; }

    /// <summary>Закэшированный контейнер</summary>
    [JsonIgnore]
    public string CachedContainer { get; set; } = "";

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
        InPlaylists = [.. InPlaylists],
        RadioSeedId = RadioSeedId
    };
}

