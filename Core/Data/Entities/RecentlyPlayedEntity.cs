namespace LMP.Core.Data.Entities;

public sealed class RecentlyPlayedEntity
{
    public int Id { get; set; }
    public string TrackId { get; set; } = string.Empty;
    public DateTime PlayedAt { get; set; }
}