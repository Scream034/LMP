namespace LMP.Core.Data.Entities;

public sealed class PlaylistEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? YoutubeId { get; set; }
    public string? Author { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CustomColor { get; set; }

    /// <summary>
    /// Автоматически вычисленный доминантный цвет из обложки.
    /// </summary>
    public string? ComputedColor { get; set; }

    /// <summary>
    /// Описание плейлиста.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Идентификатор владельца (аккаунта), к которому привязан плейлист.
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    public int SyncMode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PlaylistTrackEntity> PlaylistTracks { get; set; } = [];
}