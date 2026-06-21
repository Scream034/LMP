namespace LMP.Core.Data.Entities;

/// <summary>
/// EF Core сущность плейлиста. Хранит полный набор данных
/// включая ownership, visibility и cloud state.
/// </summary>
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
    /// Идентификатор владельца локальной записи (для изоляции между аккаунтами).
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>
    /// YouTube Channel ID владельца плейлиста на YouTube.
    /// </summary>
    public string? OwnerChannelId { get; set; }

    /// <summary>
    /// Кому принадлежит плейлист. Mapped from <see cref="PlaylistOwnership"/>.
    /// </summary>
    public int Ownership { get; set; }

    /// <summary>
    /// Уровень доступа. Mapped from <see cref="PlaylistVisibility"/>.
    /// </summary>
    public int Visibility { get; set; }

    public int SyncMode { get; set; }

    /// <summary>
    /// Количество просмотров плейлиста по данным YouTube Music.
    /// </summary>
    public long? ViewCount { get; set; }

    /// <summary>
    /// Дата создания/обновления плейлиста в строковом формате.
    /// </summary>
    public DateOnly? ReleaseDate { get; set; }

    /// <summary>
    /// Количество треков по данным YouTube API (до полной загрузки).
    /// </summary>
    public int? CloudTrackCount { get; set; }

    /// <summary>
    /// UTC-время последней успешной синхронизации.
    /// </summary>
    public DateTime? LastSyncedAtUtc { get; set; }

    /// <summary>
    /// Плейлист недоступен в облаке (удалён/закрыт).
    /// </summary>
    public bool IsCloudUnavailable { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PlaylistTrackEntity> PlaylistTracks { get; set; } = [];
}