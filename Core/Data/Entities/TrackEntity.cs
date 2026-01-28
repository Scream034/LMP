// Core/Data/Entities/TrackEntity.cs
namespace LMP.Core.Data.Entities;

public class TrackEntity
{
    public string Id { get; set; } = string.Empty;
    
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? ChannelId { get; set; }
    public string Url { get; set; } = string.Empty;
    public long DurationTicks { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    
    public bool IsOfficialArtist { get; set; }
    public bool IsMusic { get; set; }
    public bool IsLiked { get; set; }
    public bool IsDisliked { get; set; }
    public bool IsDownloaded { get; set; }
    public string? LocalPath { get; set; }
    
    public string? PreferredContainer { get; set; }
    public int PreferredBitrate { get; set; }
    public string? RadioSeedId { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    public ICollection<PlaylistTrackEntity> PlaylistTracks { get; set; } = [];
}