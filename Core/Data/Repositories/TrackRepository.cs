using System.Runtime.CompilerServices;
using LMP.Core.Audio.Normalization;
using LMP.Core.Data.Entities;
using LMP.Core.Youtube.Utils;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Реализация репозитория управления треками в SQLite-хранилище.
/// Использует асинхронный контекст EF Core Factory для предотвращения блокировок потоков.
/// </summary>
public sealed partial class TrackRepository : ITrackRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory;

    public TrackRepository(IDbContextFactory<LibraryDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Вспомогательный предикат для выявления гостевой или пустой сессии, подлежащих слиянию в единый профиль.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGuest(string ownerId) => string.IsNullOrEmpty(ownerId) || ownerId == "guest";

    #region Чтение

    /// <inheritdoc />
    public async Task<TrackInfo?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var entity = await ctx.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null) return null;

        var model = MapToModel(entity);
        model.IsLiked = await ctx.LikedTracks.AnyAsync(lt =>
            (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == id, ct);
        return model;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetByIdsAsync(IEnumerable<string> ids, string ownerId, CancellationToken ct = default)
    {
        var idList = ids as IList<string> ?? [.. ids];
        if (idList.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .Where(t => idList.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var likedTrackIds = await ctx.LikedTracks
            .Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && idList.Contains(lt.TrackId))
            .Select(lt => lt.TrackId)
            .ToHashSetAsync(ct);

        var result = new List<TrackInfo>(idList.Count);
        for (int i = 0; i < idList.Count; i++)
        {
            var id = idList[i];
            if (entities.TryGetValue(id, out var entity))
            {
                var model = MapToModel(entity);
                model.IsLiked = likedTrackIds.Contains(id);
                result.Add(model);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> SearchAsync(string query, string ownerId, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var pattern = $"%{query}%";
        var entities = await ctx.Tracks
            .Where(t => EF.Functions.Like(t.Title, pattern) ||
                       EF.Functions.Like(t.Author, pattern))
            .OrderBy(t => t.Title)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var trackIds = entities.Select(e => e.Id).ToList();
        var likedTrackIds = await ctx.LikedTracks
            .Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && trackIds.Contains(lt.TrackId))
            .Select(lt => lt.TrackId)
            .ToHashSetAsync(ct);

        var models = new List<TrackInfo>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
        {
            var model = MapToModel(entities[i]);
            model.IsLiked = likedTrackIds.Contains(model.Id);
            models.Add(model);
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetLikedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var rows = await ctx.LikedTracks
            .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
            .Join(ctx.Tracks,
                lt => lt.TrackId,
                t => t.Id,
                (lt, t) => new { lt.LikedAt, Track = t })
            .OrderByDescending(x => x.LikedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var models = new List<TrackInfo>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var model = MapToModel(rows[i].Track);
            model.IsLiked = true;
            models.Add(model);
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetDownloadedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .Where(t => t.IsDownloaded)
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var trackIds = entities.Select(e => e.Id).ToList();
        var likedTrackIds = await ctx.LikedTracks
            .Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && trackIds.Contains(lt.TrackId))
            .Select(lt => lt.TrackId)
            .ToHashSetAsync(ct);

        var models = new List<TrackInfo>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
        {
            var model = MapToModel(entities[i]);
            model.IsLiked = likedTrackIds.Contains(model.Id);
            models.Add(model);
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetRecentlyPlayedAsync(string ownerId, int limit = 50, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var recentIds = await ctx.RecentlyPlayed
            .Where(r => IsGuest(ownerId) ? (r.OwnerId == "" || r.OwnerId == "guest") : r.OwnerId == ownerId)
            .OrderByDescending(r => r.PlayedAt)
            .Take(limit)
            .Select(r => r.TrackId)
            .ToListAsync(ct);

        if (recentIds.Count == 0) return [];

        return await GetByIdsAsync(recentIds, ownerId, ct);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Tracks.CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> CountLikedAsync(string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.LikedTracks.CountAsync(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId, ct);
    }

    /// <inheritdoc />
    public async Task<int> CountLocalAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Tracks.CountAsync(t => t.Id.StartsWith("local_") || t.IsDownloaded, ct);
    }

    #endregion

    #region Запись

    /// <inheritdoc />
    public async Task UpsertAsync(TrackInfo track, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.Tracks.FirstOrDefaultAsync(t => t.Id == track.Id, ct);

        if (existing != null)
        {
            UpdateEntityFromModel(existing, track);
            existing.UpdatedAt = DateTime.UtcNow;
            ctx.Tracks.Update(existing);
        }
        else
        {
            var entity = MapToEntity(track);
            entity.CreatedAt = DateTime.UtcNow;
            entity.UpdatedAt = DateTime.UtcNow;
            ctx.Tracks.Add(entity);
        }

        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpsertBatchAsync(IEnumerable<TrackInfo> tracks, CancellationToken ct = default)
    {
        var trackList = tracks as IList<TrackInfo> ?? [.. tracks];
        if (trackList.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ids = trackList.Select(t => t.Id).ToList();

        var existingEntities = await ctx.Tracks
            .Where(t => ids.Contains(t.Id))
            .ToListAsync(ct);

        var existingMap = existingEntities.ToDictionary(t => t.Id);
        var now = DateTime.UtcNow;

        for (int i = 0; i < trackList.Count; i++)
        {
            var track = trackList[i];
            if (existingMap.TryGetValue(track.Id, out var existing))
            {
                UpdateEntityFromModel(existing, track);
                existing.UpdatedAt = now;
            }
            else
            {
                var entity = MapToEntity(track);
                entity.CreatedAt = now;
                entity.UpdatedAt = now;
                ctx.Tracks.Add(entity);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Tracks.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetLikedAsync(string id, string ownerId, bool liked, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (liked)
        {
            var exists = await ctx.LikedTracks
                .AnyAsync(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == id, ct);

            if (!exists)
            {
                ctx.LikedTracks.Add(new LikedTrackEntity
                {
                    OwnerId = ownerId,
                    TrackId = id,
                    LikedAt = DateTime.UtcNow
                });
                await ctx.SaveChangesAsync(ct);
            }
        }
        else
        {
            await ctx.LikedTracks
                .Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == id)
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task SaveNormalizationMetadataAsync(
        string id,
        float integratedLufs,
        int source,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Tracks
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IntegratedLufs, integratedLufs)
                .SetProperty(t => t.IntegratedLufsSource, source)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
    }

    #endregion

    #region История

    /// <inheritdoc />
    public async Task AddToHistoryAsync(string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        await ctx.RecentlyPlayed
            .Where(r => (IsGuest(ownerId) ? (r.OwnerId == "" || r.OwnerId == "guest") : r.OwnerId == ownerId) && r.TrackId == trackId)
            .ExecuteDeleteAsync(ct);

        ctx.RecentlyPlayed.Add(new RecentlyPlayedEntity
        {
            TrackId = trackId,
            OwnerId = ownerId,
            PlayedAt = DateTime.UtcNow
        });

        await ctx.SaveChangesAsync(ct);

        var oldEntries = await ctx.RecentlyPlayed
            .Where(r => IsGuest(ownerId) ? (r.OwnerId == "" || r.OwnerId == "guest") : r.OwnerId == ownerId)
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

    /// <inheritdoc />
    public async Task ClearHistoryAsync(string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.RecentlyPlayed
            .Where(r => IsGuest(ownerId) ? (r.OwnerId == "" || r.OwnerId == "guest") : r.OwnerId == ownerId)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetAllAsync(string ownerId, int limit = 10000, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var trackIds = entities.Select(e => e.Id).ToList();
        var likedTrackIds = await ctx.LikedTracks
            .Where(lt => lt.OwnerId == ownerId && trackIds.Contains(lt.TrackId))
            .Select(lt => lt.TrackId)
            .ToHashSetAsync(ct);

        var models = new List<TrackInfo>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
        {
            var model = MapToModel(entities[i]);
            model.IsLiked = likedTrackIds.Contains(model.Id);
            models.Add(model);
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetLocalTracksAsync(string ownerId, int limit = 1000, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entities = await ctx.Tracks
            .Where(t => t.Id.StartsWith("local_") || t.IsDownloaded)
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var trackIds = entities.Select(e => e.Id).ToList();
        var likedTrackIds = await ctx.LikedTracks
            .Where(lt => lt.OwnerId == ownerId && trackIds.Contains(lt.TrackId))
            .Select(lt => lt.TrackId)
            .ToHashSetAsync(ct);

        var models = new List<TrackInfo>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
        {
            var model = MapToModel(entities[i]);
            model.IsLiked = likedTrackIds.Contains(model.Id);
            models.Add(model);
        }

        return models;
    }

    #endregion

    #region Маппинг

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        IsDisliked = e.IsDisliked,
        IsDownloaded = e.IsDownloaded,
        LocalPath = e.LocalPath,
        PreferredFormat = ParsePreferredFormat(e.PreferredContainer),
        PreferredBitrate = e.PreferredBitrate,
        RadioSeedId = e.RadioSeedId,
        IntegratedLufs = e.IntegratedLufs ?? float.NaN,
        IntegratedLufsSource = (LoudnessSource)e.IntegratedLufsSource
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        IsDisliked = m.IsDisliked,
        IsDownloaded = m.IsDownloaded,
        LocalPath = m.LocalPath,
        PreferredContainer = PersistPreferredFormat(m.PreferredFormat),
        PreferredBitrate = m.PreferredBitrate,
        RadioSeedId = m.RadioSeedId,
        IntegratedLufs = float.IsNaN(m.IntegratedLufs) || !float.IsFinite(m.IntegratedLufs)
            ? null : m.IntegratedLufs,
        IntegratedLufsSource = (int)m.IntegratedLufsSource
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        entity.IsDisliked = model.IsDisliked;
        entity.IsDownloaded = model.IsDownloaded;
        entity.LocalPath = model.LocalPath ?? entity.LocalPath;
        if (model.PreferredFormat.HasValue && model.PreferredFormat != AudioFormat.Unknown)
            entity.PreferredContainer = PersistPreferredFormat(model.PreferredFormat);
        entity.PreferredBitrate = model.PreferredBitrate > 0 ? model.PreferredBitrate : entity.PreferredBitrate;
        entity.RadioSeedId = model.RadioSeedId ?? entity.RadioSeedId;

        if (!float.IsNaN(model.IntegratedLufs) && float.IsFinite(model.IntegratedLufs))
        {
            entity.IntegratedLufs = model.IntegratedLufs;
            entity.IntegratedLufsSource = (int)model.IntegratedLufsSource;
        }
    }

    #endregion

    private static AudioFormat? ParsePreferredFormat(string? container)
    {
        var format = YoutubeIdHelper.MapContainerToFormat(container);
        return format == AudioFormat.Unknown ? null : format;
    }

    private static string? PersistPreferredFormat(AudioFormat? format)
    {
        return format is { } value && value != AudioFormat.Unknown
            ? value.ToContainerName()
            : null;
    }
}