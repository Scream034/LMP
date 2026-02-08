using LMP.Core.Models;

namespace LMP.Core.Data.Repositories;

public interface IPlaylistRepository
{
    Task<Playlist?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<Playlist>> GetAllAsync(CancellationToken ct = default);
    Task<List<string>> GetTrackIdsAsync(string playlistId, CancellationToken ct = default);
    Task<int> GetTrackCountAsync(string playlistId, CancellationToken ct = default);

    Task UpsertAsync(Playlist playlist, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task RenameAsync(string id, string newName, CancellationToken ct = default);

    Task AddTrackAsync(string playlistId, string trackId, int? position = null, CancellationToken ct = default);
    Task<int> AddTracksAsync(string playlistId, IEnumerable<string> trackIds, CancellationToken ct = default);
    Task RemoveTrackAsync(string playlistId, string trackId, CancellationToken ct = default);
    Task MoveTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default);
    Task<bool> ContainsTrackAsync(string playlistId, string trackId, CancellationToken ct = default);
    Task<HashSet<string>> GetPlaylistsForTrackAsync(string trackId, CancellationToken ct = default);
    /// <summary>
    /// Batch-загрузка плейлистов для нескольких треков за один SQL-запрос.
    /// Возвращает словарь: trackId → набор playlistId.
    /// </summary>
    Task<Dictionary<string, HashSet<string>>> GetPlaylistsForTracksAsync(
        IEnumerable<string> trackIds,
        CancellationToken ct = default);
    /// <summary>
    /// Gets all playlists with their track counts (more efficient than loading all track IDs).
    /// </summary>
    Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(CancellationToken ct = default);
    /// <summary>
    /// Gets total duration of all tracks in playlist (in ticks).
    /// </summary>
    Task<long> GetTotalDurationTicksAsync(string playlistId, CancellationToken ct = default);
}