namespace LMP.Core.Models;

/// <summary>
/// Результат диалога редактирования плейлиста.
/// </summary>
public sealed class EditPlaylistResult
{
    public string Name { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string? CustomColor { get; set; }

    /// <summary>
    /// Описание плейлиста. null — не менялось.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Вычисленный из обложки цвет (если был пересчитан в диалоге).
    /// null — не пересчитывался, оставляем как есть.
    /// </summary>
    public string? ComputedColor { get; set; }

    /// <summary>
    /// Изменение синхронизации с YouTube.
    /// null — пользователь не трогал переключатель.
    /// true — хочет привязать к облаку.
    /// false — хочет отвязать от облака.
    /// </summary>
    public bool? SyncToCloud { get; set; }

    /// <summary>
    /// Пользователь запросил создание локальной копии плейлиста.
    /// При true <see cref="PlaylistEditService"/> создаёт новый локальный плейлист
    /// вместо редактирования оригинала. Копия никогда не привязывается к YouTube.
    /// </summary>
    public bool ShouldCreateCopy { get; set; }
}