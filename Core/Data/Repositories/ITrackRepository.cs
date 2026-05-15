using LMP.Core.Models;

namespace LMP.Core.Data.Repositories;

public interface ITrackRepository
{
    // Read
    Task<TrackInfo?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<TrackInfo>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task<List<TrackInfo>> GetAllAsync(int limit = 10000, int offset = 0, CancellationToken ct = default);
    Task<List<TrackInfo>> SearchAsync(string query, int limit = 50, int offset = 0, CancellationToken ct = default);
    Task<List<TrackInfo>> GetLikedAsync(int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<List<TrackInfo>> GetDownloadedAsync(int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<List<TrackInfo>> GetRecentlyPlayedAsync(int limit = 50, CancellationToken ct = default);
    Task<List<TrackInfo>> GetLocalTracksAsync(int limit = 1000, int offset = 0, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<int> CountLikedAsync(CancellationToken ct = default);
    Task<int> CountLocalAsync(CancellationToken ct = default);

    // Write
    Task UpsertAsync(TrackInfo track, CancellationToken ct = default);
    Task UpsertBatchAsync(IEnumerable<TrackInfo> tracks, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task SetLikedAsync(string id, bool liked, CancellationToken ct = default);
    Task SetDownloadedAsync(string id, bool downloaded, string? localPath, CancellationToken ct = default);
    /// <summary>Сохраняет вычисленный gain нормализации для трека.</summary>
    Task SaveNormalizationGainAsync(string id, float gain, CancellationToken ct = default);

    // History
    Task AddToHistoryAsync(string trackId, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);
}