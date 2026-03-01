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
    /// Изменение синхронизации с YouTube.
    /// null = пользователь не трогал переключатель.
    /// true = хочет привязать к облаку.
    /// false = хочет отвязать от облака.
    /// </summary>
    public bool? SyncToCloud { get; set; }
}