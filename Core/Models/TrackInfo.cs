using System.Text.Json.Serialization;
 // Для IBatchItem
using LMP.Core.Youtube.Search; // Для ISearchResult

namespace LMP.Core.Models;

// Реализуем IBatchItem для поддержки пагинации YouTube
// Реализуем ISearchResult для поддержки возврата из SearchClient
public class TrackInfo : IBatchItem, ISearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    
    // Свойства для YouTube
    public string? ChannelId { get; set; }
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
            if (!string.IsNullOrEmpty(_thumbnailUrl)) return _thumbnailUrl;
            if (Id.StartsWith("yt_") && Id.Length > 3)
            {
                var videoId = Id[3..];
                return $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
            }
            return string.Empty;
        }
        set => _thumbnailUrl = value;
    }

    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    // State
    public bool IsLiked { get; set; }
    public bool IsDisliked { get; set; }
    public bool IsDownloaded { get; set; }
    public string? LocalPath { get; set; }

    public HashSet<string> InPlaylists { get; set; } = [];
    public string? RadioSeedId { get; set; }

    // Format preferences
    public string? PreferredContainer { get; set; }
    public int PreferredBitrate { get; set; }
    
    [JsonIgnore] public string? TransientContainer { get; set; }
    [JsonIgnore] public int TransientBitrate { get; set; }

    [JsonIgnore] public string CachedCodec { get; set; } = "";
    [JsonIgnore] public int CachedBitrate { get; set; }
    [JsonIgnore] public string CachedContainer { get; set; } = "";
    
    // ISearchResult implementation
    // Для TrackInfo Url и Title уже есть, совпадают с интерфейсом

    public TrackInfo Clone() => new()
    {
        Id = Id,
        Title = Title,
        Author = Author,
        ChannelId = ChannelId,
        IsOfficialArtist = IsOfficialArtist,
        IsMusic = IsMusic,
        Url = Url,
        StreamUrl = StreamUrl,
        Duration = Duration,
        _thumbnailUrl = _thumbnailUrl,
        IsLiked = IsLiked,
        IsDisliked = IsDisliked,
        IsDownloaded = IsDownloaded,
        LocalPath = LocalPath,
        InPlaylists = [.. InPlaylists],
        RadioSeedId = RadioSeedId,
        PreferredContainer = PreferredContainer,
        PreferredBitrate = PreferredBitrate
    };
}