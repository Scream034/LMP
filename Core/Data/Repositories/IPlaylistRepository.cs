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

    Task<Dictionary<string, HashSet<string>>> GetPlaylistsForTracksAsync(
        IEnumerable<string> trackIds, CancellationToken ct = default);

    Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(CancellationToken ct = default);
    Task<long> GetTotalDurationTicksAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает сумму Ticks всех треков во всех плейлистах одним SQL-запросом.
    /// Решает проблему N+1 запросов при обновлении статистики библиотеки.
    /// </summary>
    Task<long> GetTotalLibraryDurationAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the YouTube setVideoId for a track in a playlist.
    /// Required for removing tracks from YouTube playlists via API.
    /// </summary>
    Task<string?> GetSetVideoIdAsync(string playlistId, string trackId, CancellationToken ct = default);

    /// <summary>
    /// Updates the YouTube setVideoId for a track in a playlist.
    /// Called during playlist sync when setVideoId is received from YouTube API.
    /// </summary>
    Task UpdateSetVideoIdAsync(string playlistId, string trackId, string setVideoId, CancellationToken ct = default);

    /// <summary>
    /// Batch updates setVideoIds for multiple tracks in a playlist.
    /// Used during full playlist sync from YouTube.
    /// </summary>
    Task UpdateSetVideoIdsAsync(string playlistId, IReadOnlyList<(string TrackId, string SetVideoId)> mappings, CancellationToken ct = default);

    /// <summary>
    /// Returns track IDs with SQL-level LIMIT/OFFSET.
    /// Use for paged loading (e.g. cover picker in edit dialog).
    /// </summary>
    Task<List<string>> GetTrackIdsAsync(string playlistId, int limit, int offset = 0, CancellationToken ct = default);
}