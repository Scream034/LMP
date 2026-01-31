// Core/Data/Entities/RecentlyPlayedEntity.cs
namespace LMP.Core.Data.Entities;

public class RecentlyPlayedEntity
{
    public int Id { get; set; }
    public string TrackId { get; set; } = string.Empty;
    public DateTime PlayedAt { get; set; }
}