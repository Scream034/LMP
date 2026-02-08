using LMP.Core.Data.Entities;
using LMP.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

public sealed class PlaylistRepository(IDbContextFactory<LibraryDbContext> factory) : IPlaylistRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory = factory;

    public async Task<Playlist?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entity = await ctx.Playlists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return null;

        var playlist = MapToModel(entity);

        // Load track IDs and set count
        playlist.TrackIds = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == id)
            .OrderBy(pt => pt.Position)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct);

        playlist.TrackCount = playlist.TrackIds.Count;

        return playlist;
    }

    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Get playlists with track counts in a single query
        var playlistsWithCounts = await ctx.Playlists
            .Select(p => new
            {
                Playlist = p,
                TrackCount = ctx.PlaylistTracks.Count(pt => pt.PlaylistId == p.Id)
            })
            .OrderBy(x => x.Playlist.Name)
            .ToListAsync(ct);

        return [.. playlistsWithCounts.Select(x => (MapToModel(x.Playlist), x.TrackCount))];
    }

    public async Task<List<Playlist>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Get playlists with track counts in efficient query
        var playlistsWithCounts = await ctx.Playlists
            .Select(p => new
            {
                Playlist = p,
                TrackCount = ctx.PlaylistTracks.Count(pt => pt.PlaylistId == p.Id)
            })
            .OrderBy(x => x.Playlist.Name)
            .ToListAsync(ct);

        return [.. playlistsWithCounts
            .Select(x =>
            {
                var playlist = MapToModel(x.Playlist);
                playlist.TrackCount = x.TrackCount;
                return playlist;
            })];
    }

    public async Task<List<string>> GetTrackIdsAsync(string playlistId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        return await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct);
    }

    public async Task<int> GetTrackCountAsync(string playlistId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.PlaylistTracks.CountAsync(pt => pt.PlaylistId == playlistId, ct);
    }

    public async Task UpsertAsync(Playlist playlist, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.Playlists.FirstOrDefaultAsync(p => p.Id == playlist.Id, ct);
        var entity = MapToEntity(playlist);

        if (existing != null)
        {
            entity.CreatedAt = existing.CreatedAt;
            ctx.Entry(existing).CurrentValues.SetValues(entity);
        }
        else
        {
            entity.CreatedAt = DateTime.UtcNow;
            ctx.Playlists.Add(entity);
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Playlists.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task RenameAsync(string id, string newName, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Playlists
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, newName)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
    }

    public async Task AddTrackAsync(string playlistId, string trackId, int? position = null, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Check if relationship already exists
        var exists = await ctx.PlaylistTracks
            .AnyAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct);
        if (exists) return;

        // IMPORTANT: Check if track exists in database first!
        var trackExists = await ctx.Tracks.AnyAsync(t => t.Id == trackId, ct);
        if (!trackExists)
        {
            Log.Warn($"[PlaylistRepo] Cannot add track {trackId} to playlist {playlistId} - track does not exist");
            return;
        }

        int pos = position ?? await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .MaxAsync(pt => (int?)pt.Position, ct) + 1 ?? 0;

        ctx.PlaylistTracks.Add(new PlaylistTrackEntity
        {
            PlaylistId = playlistId,
            TrackId = trackId,
            Position = pos
        });

        await ctx.SaveChangesAsync(ct);

        // Update playlist timestamp
        await ctx.Playlists
            .Where(p => p.Id == playlistId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Batch add tracks to playlist - skips tracks that don't exist.
    /// Returns the count of actually added tracks.
    /// </summary>
    public async Task<int> AddTracksAsync(string playlistId, IEnumerable<string> trackIds, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var trackIdList = trackIds.ToList();
        if (trackIdList.Count == 0) return 0;

        // Get existing tracks in DB
        var existingTrackIds = await ctx.Tracks
            .Where(t => trackIdList.Contains(t.Id))
            .Select(t => t.Id)
            .ToHashSetAsync(ct);

        // Get already linked tracks
        var alreadyLinked = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && trackIdList.Contains(pt.TrackId))
            .Select(pt => pt.TrackId)
            .ToHashSetAsync(ct);

        // Get current max position
        int maxPos = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .MaxAsync(pt => (int?)pt.Position, ct) ?? -1;

        int added = 0;
        foreach (var trackId in trackIdList)
        {
            // Skip if track doesn't exist or already linked
            if (!existingTrackIds.Contains(trackId) || alreadyLinked.Contains(trackId))
                continue;

            maxPos++;
            ctx.PlaylistTracks.Add(new PlaylistTrackEntity
            {
                PlaylistId = playlistId,
                TrackId = trackId,
                Position = maxPos
            });
            added++;
        }

        if (added > 0)
        {
            await ctx.SaveChangesAsync(ct);

            await ctx.Playlists
                .Where(p => p.Id == playlistId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
        }

        return added;
    }

    public async Task RemoveTrackAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entry = await ctx.PlaylistTracks
            .FirstOrDefaultAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct);

        if (entry is null) return;

        int removedPos = entry.Position;
        ctx.PlaylistTracks.Remove(entry);
        await ctx.SaveChangesAsync(ct);

        // Reorder remaining tracks
        await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.Position > removedPos)
            .ExecuteUpdateAsync(s => s.SetProperty(pt => pt.Position, pt => pt.Position - 1), ct);
    }

    public async Task MoveTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        if (oldIndex == newIndex) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var tracks = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .ToListAsync(ct);

        if (oldIndex < 0 || oldIndex >= tracks.Count ||
            newIndex < 0 || newIndex >= tracks.Count) return;

        var movingTrack = tracks[oldIndex];
        tracks.RemoveAt(oldIndex);
        tracks.Insert(newIndex, movingTrack);

        for (int i = 0; i < tracks.Count; i++)
        {
            tracks[i].Position = i;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> ContainsTrackAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.PlaylistTracks
            .AnyAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct);
    }

    public async Task<HashSet<string>> GetPlaylistsForTrackAsync(string trackId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ids = await ctx.PlaylistTracks
            .Where(pt => pt.TrackId == trackId)
            .Select(pt => pt.PlaylistId)
            .ToListAsync(ct);
        return [.. ids];
    }

    public async Task<Dictionary<string, HashSet<string>>> GetPlaylistsForTracksAsync(
    IEnumerable<string> trackIds,
    CancellationToken ct = default)
    {
        var ids = trackIds as IList<string> ?? [.. trackIds];

        if (ids.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Один SQL-запрос вместо N
        var links = await ctx.PlaylistTracks
            .Where(pt => ids.Contains(pt.TrackId))
            .Select(pt => new { pt.TrackId, pt.PlaylistId })
            .ToListAsync(ct);

        // Группируем в памяти
        var result = new Dictionary<string, HashSet<string>>(ids.Count);

        foreach (var link in links)
        {
            if (!result.TryGetValue(link.TrackId, out var set))
            {
                set = [];
                result[link.TrackId] = set;
            }
            set.Add(link.PlaylistId);
        }

        return result;
    }

    public async Task<long> GetTotalDurationTicksAsync(string playlistId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // JOIN PlaylistTracks с Tracks и суммируем DurationTicks
        var totalTicks = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .Join(ctx.Tracks,
                pt => pt.TrackId,
                t => t.Id,
                (pt, t) => t.DurationTicks)
            .SumAsync(ct);

        return totalTicks;
    }

    #region Mapping

    private static Playlist MapToModel(PlaylistEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        YoutubeId = e.YoutubeId,
        Author = e.Author,
        ThumbnailUrl = e.ThumbnailUrl,
        SyncMode = (PlaylistSyncMode)e.SyncMode,
        UpdatedAt = e.UpdatedAt
    };

    private static PlaylistEntity MapToEntity(Playlist m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        YoutubeId = m.YoutubeId,
        Author = m.Author,
        ThumbnailUrl = m.ThumbnailUrl,
        SyncMode = (int)m.SyncMode,
        UpdatedAt = DateTime.UtcNow
    };

    #endregion
}