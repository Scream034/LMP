using LMP.Core.Data.Entities;
using LMP.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

public sealed partial class TrackRepository : ITrackRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory;

    public TrackRepository(IDbContextFactory<LibraryDbContext> factory)
    {
        _factory = factory;
    }

    #region Read Operations

    public async Task<TrackInfo?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var entity = await ctx.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
        return entity is null ? null : MapToModel(entity);
    }

    public async Task<List<TrackInfo>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .Where(t => idList.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        return [.. idList
            .Where(entities.ContainsKey)
            .Select(id => MapToModel(entities[id]))];
    }

    public async Task<List<TrackInfo>> SearchAsync(string query, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Use LIKE search (FTS can be added later if needed)
        var pattern = $"%{query}%";
        var entities = await ctx.Tracks
            .Where(t => EF.Functions.Like(t.Title, pattern) ||
                       EF.Functions.Like(t.Author, pattern))
            .OrderBy(t => t.Title)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return [.. entities.Select(MapToModel)];
    }

    public async Task<List<TrackInfo>> GetLikedAsync(int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .Where(t => t.IsLiked)
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return [.. entities.Select(MapToModel)];
    }

    public async Task<List<TrackInfo>> GetDownloadedAsync(int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .Where(t => t.IsDownloaded)
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return [.. entities.Select(MapToModel)];
    }

    public async Task<List<TrackInfo>> GetRecentlyPlayedAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var recentIds = await ctx.RecentlyPlayed
            .OrderByDescending(r => r.PlayedAt)
            .Take(limit)
            .Select(r => r.TrackId)
            .ToListAsync(ct);

        if (recentIds.Count == 0) return [];

        return await GetByIdsAsync(recentIds, ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Tracks.CountAsync(ct);
    }

    public async Task<int> CountLikedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Tracks.CountAsync(t => t.IsLiked, ct);
    }

    #endregion

    #region Write Operations

    public async Task UpsertAsync(TrackInfo track, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.Tracks.FirstOrDefaultAsync(t => t.Id == track.Id, ct);

        if (existing != null)
        {
            // Update existing - detach first to avoid tracking conflicts
            UpdateEntityFromModel(existing, track);
            existing.UpdatedAt = DateTime.UtcNow;
            ctx.Tracks.Update(existing);
        }
        else
        {
            // Insert new
            var entity = MapToEntity(track);
            entity.CreatedAt = DateTime.UtcNow;
            entity.UpdatedAt = DateTime.UtcNow;
            ctx.Tracks.Add(entity);
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpsertBatchAsync(IEnumerable<TrackInfo> tracks, CancellationToken ct = default)
    {
        var trackList = tracks.ToList();
        if (trackList.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ids = trackList.Select(t => t.Id).ToList();

        // Get existing tracks with tracking enabled for this operation
        var existingEntities = await ctx.Tracks
            .Where(t => ids.Contains(t.Id))
            .ToListAsync(ct);

        var existingMap = existingEntities.ToDictionary(t => t.Id);
        var now = DateTime.UtcNow;

        foreach (var track in trackList)
        {
            if (existingMap.TryGetValue(track.Id, out var existing))
            {
                // Update existing entity
                UpdateEntityFromModel(existing, track);
                existing.UpdatedAt = now;
                // Entity is already tracked, will be updated on SaveChanges
            }
            else
            {
                // Add new entity
                var entity = MapToEntity(track);
                entity.CreatedAt = now;
                entity.UpdatedAt = now;
                ctx.Tracks.Add(entity);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Tracks.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task SetLikedAsync(string id, bool liked, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Tracks
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsLiked, liked)
                .SetProperty(t => t.IsDisliked, false)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
    }

    public async Task SetDownloadedAsync(string id, bool downloaded, string? localPath, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Tracks
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsDownloaded, downloaded)
                .SetProperty(t => t.LocalPath, localPath)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
    }

    #endregion

    #region History

    public async Task AddToHistoryAsync(string trackId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Remove existing entry for this track
        await ctx.RecentlyPlayed
            .Where(r => r.TrackId == trackId)
            .ExecuteDeleteAsync(ct);

        ctx.RecentlyPlayed.Add(new RecentlyPlayedEntity
        {
            TrackId = trackId,
            PlayedAt = DateTime.UtcNow
        });

        await ctx.SaveChangesAsync(ct);

        // Cleanup old entries
        var oldEntries = await ctx.RecentlyPlayed
            .OrderByDescending(r => r.PlayedAt)
            .Skip(100)
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (oldEntries.Count > 0)
        {
            await ctx.RecentlyPlayed
                .Where(r => oldEntries.Contains(r.Id))
                .ExecuteDeleteAsync(ct);
        }
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.RecentlyPlayed.ExecuteDeleteAsync(ct);
    }

    public async Task<List<TrackInfo>> GetAllAsync(int limit = 10000, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return [.. entities.Select(MapToModel)];
    }

    public async Task<List<TrackInfo>> GetLocalTracksAsync(int limit = 1000, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Локальные треки: ID начинается с "local_" ИЛИ IsDownloaded = true
        var entities = await ctx.Tracks
            .Where(t => t.Id.StartsWith("local_") || t.IsDownloaded)
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return [.. entities.Select(MapToModel)];
    }

    public async Task<int> CountLocalAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Tracks.CountAsync(t => t.Id.StartsWith("local_") || t.IsDownloaded, ct);
    }

    #endregion

    #region Mapping

    private static TrackInfo MapToModel(TrackEntity e) => new()
    {
        Id = e.Id,
        Title = e.Title,
        Author = e.Author,
        ChannelId = e.ChannelId,
        Url = e.Url,
        Duration = TimeSpan.FromTicks(e.DurationTicks),
        ThumbnailUrl = e.ThumbnailUrl,
        IsOfficialArtist = e.IsOfficialArtist,
        IsMusic = e.IsMusic,
        IsLiked = e.IsLiked,
        IsDisliked = e.IsDisliked,
        IsDownloaded = e.IsDownloaded,
        LocalPath = e.LocalPath,
        PreferredContainer = e.PreferredContainer,
        PreferredBitrate = e.PreferredBitrate,
        RadioSeedId = e.RadioSeedId
    };

    private static TrackEntity MapToEntity(TrackInfo m) => new()
    {
        Id = m.Id,
        Title = m.Title ?? "",
        Author = m.Author ?? "",
        ChannelId = m.ChannelId,
        Url = m.Url ?? "",
        DurationTicks = m.Duration.Ticks,
        ThumbnailUrl = m.ThumbnailUrl ?? "",
        IsOfficialArtist = m.IsOfficialArtist,
        IsMusic = m.IsMusic,
        IsLiked = m.IsLiked,
        IsDisliked = m.IsDisliked,
        IsDownloaded = m.IsDownloaded,
        LocalPath = m.LocalPath,
        PreferredContainer = m.PreferredContainer,
        PreferredBitrate = m.PreferredBitrate,
        RadioSeedId = m.RadioSeedId
    };

    private static void UpdateEntityFromModel(TrackEntity entity, TrackInfo model)
    {
        entity.Title = model.Title ?? entity.Title;
        entity.Author = model.Author ?? entity.Author;
        entity.ChannelId = model.ChannelId ?? entity.ChannelId;
        entity.Url = model.Url ?? entity.Url;
        entity.DurationTicks = model.Duration.Ticks > 0 ? model.Duration.Ticks : entity.DurationTicks;
        entity.ThumbnailUrl = model.ThumbnailUrl ?? entity.ThumbnailUrl;
        entity.IsOfficialArtist = model.IsOfficialArtist || entity.IsOfficialArtist;
        entity.IsMusic = model.IsMusic || entity.IsMusic;
        entity.IsLiked = model.IsLiked;
        entity.IsDisliked = model.IsDisliked;
        entity.IsDownloaded = model.IsDownloaded;
        entity.LocalPath = model.LocalPath ?? entity.LocalPath;
        entity.PreferredContainer = model.PreferredContainer ?? entity.PreferredContainer;
        entity.PreferredBitrate = model.PreferredBitrate > 0 ? model.PreferredBitrate : entity.PreferredBitrate;
        entity.RadioSeedId = model.RadioSeedId ?? entity.RadioSeedId;
    }

    #endregion
}