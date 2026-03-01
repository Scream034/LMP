namespace LMP.Core.Data.Entities;

public sealed class PlaylistTrackEntity
{
    public string PlaylistId { get; set; } = string.Empty;
    public string TrackId { get; set; } = string.Empty;
    public int Position { get; set; }
    public string? SetVideoId { get; set; }

    public PlaylistEntity Playlist { get; set; } = null!;
    public TrackEntity Track { get; set; } = null!;
}