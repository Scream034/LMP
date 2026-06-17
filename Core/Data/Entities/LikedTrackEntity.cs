namespace LMP.Core.Data.Entities;

/// <summary>
/// Сущность лайкнутого трека пользователя.
/// Позволяет разделять лайки между разными аккаунтами.
/// </summary>
public sealed class LikedTrackEntity
{
    /// <summary>
    /// Идентификатор владельца (DisplayId активного аккаунта).
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>
    /// Уникальный идентификатор трека.
    /// </summary>
    public string TrackId { get; set; } = string.Empty;

    /// <summary>
    /// Время установки лайка.
    /// </summary>
    public DateTime LikedAt { get; set; }

    public TrackEntity Track { get; set; } = null!;
}