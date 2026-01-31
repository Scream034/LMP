// Core/Data/Entities/PlaylistTrackEntity.cs
namespace LMP.Core.Data.Entities;

public class PlaylistTrackEntity
{
    public string PlaylistId { get; set; } = string.Empty;
    public string TrackId { get; set; } = string.Empty;
    public int Position { get; set; }
    
    public PlaylistEntity Playlist { get; set; } = null!;
    public TrackEntity Track { get; set; } = null!;
}