using System.Text.Json.Serialization;
using LMP.Core.Services;

namespace LMP.Core.Models;

public enum PlaylistSyncMode
{
    LocalOnly,      // Только локально (JSON)
    TwoWaySync,     // Синхронизация с аккаунтом (JSON + YouTube API)
    CloudPublic     // Публичный/Чужой плейлист (Только чтение, "Фейк")
}

public class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Внутреннее имя (сохраняется в JSON)
    [JsonPropertyName("name")]
    public string StoredName { get; set; } = "New Playlist";

    // Отображаемое имя (вычисляется динамически для системных плейлистов)
    [JsonIgnore]
    public string Name
    {
        get
        {
            // Для плейлиста "Любимое" всегда возвращаем локализованное имя
            if (Id == LibraryService.LikedPlaylistId)
            {
                return LocalizationService.Instance["Playlist_Liked"];
            }
            return StoredName;
        }
        set => StoredName = value;
    }

    public string? ThumbnailUrl { get; set; }
    public string? Author { get; set; }

    // --- СИНХРОНИЗАЦИЯ ---

    // Новое единое поле режима
    public PlaylistSyncMode SyncMode { get; set; } = PlaylistSyncMode.LocalOnly;

    // ID плейлиста на YouTube (ранее YoutubePlaylistId)
    public string? YoutubeId { get; set; }

    // ETag для разрешения конфликтов в будущем
    public string? ETag { get; set; }

    // --- СОВМЕСТИМОСТЬ И УДОБСТВО (Helpers) ---

    // Плейлист считается локальным, если он создан нами или синхронизирован
    [JsonIgnore]
    public bool IsLocal => SyncMode == PlaylistSyncMode.LocalOnly || SyncMode == PlaylistSyncMode.TwoWaySync;

    // Плейлист из аккаунта (для иконок)
    [JsonIgnore]
    public bool IsFromAccount => SyncMode == PlaylistSyncMode.TwoWaySync;

    // Плейлист "Фейковый" (только чтение)
    [JsonIgnore]
    public bool IsFakeAccountSource => SyncMode == PlaylistSyncMode.CloudPublic;

    // Можно ли редактировать (добавлять/удалять треки)
    [JsonIgnore]
    public bool IsEditable => SyncMode != PlaylistSyncMode.CloudPublic;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<string> TrackIds { get; set; } = [];

    [JsonIgnore]
    public int TrackCount => TrackIds.Count;
}

