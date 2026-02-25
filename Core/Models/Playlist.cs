using System.Text.Json.Serialization;
using LMP.Core.Services;
// Для IBatchItem
using LMP.Core.Youtube.Search; // Для ISearchResult

namespace LMP.Core.Models;

public enum PlaylistSyncMode
{
    LocalOnly,
    TwoWaySync,
    CloudPublic
}

// Реализуем IBatchItem и ISearchResult
public class Playlist : IBatchItem, ISearchResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string StoredName { get; set; } = "New Playlist";

    [JsonIgnore]
    public string Name
    {
        get
        {
            if (Id == LibraryService.LikedPlaylistId)
                return LocalizationService.Instance["Playlist_Liked"];
            return StoredName;
        }
        set => StoredName = value;
    }

    // ISearchResult implementation
    [JsonIgnore]
    public string Title => Name;

    [JsonIgnore]
    public string Url => YoutubeId != null ? $"https://www.youtube.com/playlist?list={YoutubeId}" : string.Empty;

    public string? ThumbnailUrl { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }

    public PlaylistSyncMode SyncMode { get; set; } = PlaylistSyncMode.LocalOnly;
    public string? YoutubeId { get; set; }
    public string? ETag { get; set; }

    [JsonIgnore] public bool IsLocal => SyncMode == PlaylistSyncMode.LocalOnly || SyncMode == PlaylistSyncMode.TwoWaySync;
    [JsonIgnore] public bool IsFromAccount => SyncMode == PlaylistSyncMode.TwoWaySync;
    [JsonIgnore] public bool IsFakeAccountSource => SyncMode == PlaylistSyncMode.CloudPublic;
    [JsonIgnore] public bool IsEditable => SyncMode != PlaylistSyncMode.CloudPublic;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public List<string> TrackIds { get; set; } = [];
    [JsonIgnore]
    public int TrackCount { get; set; }
}