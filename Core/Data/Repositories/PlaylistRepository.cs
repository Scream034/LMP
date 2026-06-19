using System.Runtime.CompilerServices;
using LMP.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Репозиторий списков воспроизведения на базе SQLite.
/// Исключает дублирование системных плейлистов и обеспечивает отображение локальных плейлистов под любыми аккаунтами.
/// </summary>
public sealed class PlaylistRepository(IDbContextFactory<LibraryDbContext> factory) : IPlaylistRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory = factory;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGuest(string ownerId) => string.IsNullOrEmpty(ownerId) || ownerId == "guest";

    /// <inheritdoc />
    public async Task<Playlist?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default)
    {
        if (id == LibraryService.LikedPlaylistId)
        {
            var trackIds = await GetTrackIdsAsync(id, ownerId, ct);
            return new Playlist
            {
                Id = LibraryService.LikedPlaylistId,
                StoredName = "Liked",
                SyncMode = PlaylistSyncMode.LocalOnly,
                TrackIds = trackIds,
                TrackCount = trackIds.Count,
                OwnerId = ownerId
            };
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Плейлист доступен, если он принадлежит текущему профилю, либо гостю (общие гостевые плейлисты)
        // Исключаем из физической выборки системный "liked", чтобы избежать дублирования
        var entity = await ctx.Playlists
            .FirstOrDefaultAsync(p => p.Id == id && p.Id != LibraryService.LikedPlaylistId &&
                (p.OwnerId == ownerId || p.OwnerId == "" || p.OwnerId == "guest"), ct);

        if (entity is null) return null;

        var playlist = MapToModel(entity);

        playlist.TrackIds = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == id)
            .OrderBy(pt => pt.Position)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct);

        playlist.TrackCount = playlist.TrackIds.Count;

        return playlist;
    }

    /// <inheritdoc />
    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Загружаем облачные плейлисты текущего аккаунта И гостевые плейлисты устройства
        // Исключаем "liked" из физического SQL запроса во избежание дублирования в боковой панели
        var playlistsWithCounts = await ctx.Playlists
            .Where(p => p.Id != LibraryService.LikedPlaylistId &&
                (p.OwnerId == ownerId || p.OwnerId == "" || p.OwnerId == "guest"))
            .Select(p => new
            {
                Playlist = p,
                TrackCount = ctx.PlaylistTracks.Count(pt => pt.PlaylistId == p.Id)
            })
            .OrderBy(x => x.Playlist.Name)
            .ToListAsync(ct);

        var list = new List<(Playlist Playlist, int TrackCount)>(playlistsWithCounts.Count + 1);

        // Интегрируем виртуальный Liked
        var likedTrackCount = await ctx.LikedTracks
            .CountAsync(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId, ct);

        var likedPlaylist = new Playlist
        {
            Id = LibraryService.LikedPlaylistId,
            StoredName = "Liked",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackCount = likedTrackCount,
            OwnerId = ownerId
        };
        list.Add((likedPlaylist, likedTrackCount));

        for (int i = 0; i < playlistsWithCounts.Count; i++)
        {
            var item = playlistsWithCounts[i];
            var pl = MapToModel(item.Playlist);
            pl.TrackCount = item.TrackCount;
            list.Add((pl, item.TrackCount));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<List<Playlist>> GetAllAsync(string ownerId, CancellationToken ct = default)
    {
        var withCounts = await GetAllWithCountsAsync(ownerId, ct);
        var result = new List<Playlist>(withCounts.Count);
        for (int i = 0; i < withCounts.Count; i++)
        {
            var pl = withCounts[i].Playlist;
            pl.TrackCount = withCounts[i].TrackCount;
            result.Add(pl);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<List<string>> GetTrackIdsAsync(string playlistId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks
                .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
                .OrderByDescending(lt => lt.LikedAt)
                .Select(lt => lt.TrackId)
                .ToListAsync(ct);
        }

        return await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetTrackIdsAsync(
        string playlistId, string ownerId, int limit, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks
                .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
                .OrderByDescending(lt => lt.LikedAt)
                .Skip(offset)
                .Take(limit)
                .Select(lt => lt.TrackId)
                .ToListAsync(ct);
        }

        return await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .Skip(offset)
            .Take(limit)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> GetTrackCountAsync(string playlistId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks.CountAsync(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId, ct);
        }

        return await ctx.PlaylistTracks.CountAsync(pt => pt.PlaylistId == playlistId, ct);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Playlist playlist, CancellationToken ct = default)
    {
        if (playlist.Id == LibraryService.LikedPlaylistId)
            return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.Playlists.FirstOrDefaultAsync(p => p.Id == playlist.Id, ct);

        if (existing != null)
        {
            var entry = ctx.Entry(existing);
            entry.State = EntityState.Detached;

            existing.Name = playlist.StoredName;
            existing.YoutubeId = playlist.YoutubeId;
            existing.Author = playlist.Author;
            existing.ThumbnailUrl = playlist.ThumbnailUrl;
            existing.CustomColor = playlist.CustomColor;
            existing.ComputedColor = playlist.ComputedColor;
            existing.Description = playlist.Description;
            existing.SyncMode = (int)playlist.SyncMode;
            existing.OwnerId = playlist.OwnerId;
            existing.UpdatedAt = DateTime.UtcNow;

            ctx.Playlists.Update(existing);
        }
        else
        {
            var entity = MapToEntity(playlist);
            entity.CreatedAt = DateTime.UtcNow;
            entity.UpdatedAt = DateTime.UtcNow;
            ctx.Playlists.Add(entity);
        }

        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Playlists.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task RenameAsync(string id, string newName, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Playlists
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, newName)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
    }

    /// <inheritdoc />
    public async Task AddTrackAsync(string playlistId, string trackId, string ownerId, int? position = null, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await SetLikedDirectAsync(trackId, ownerId, true, ct);
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var exists = await ctx.PlaylistTracks
            .AnyAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct);
        if (exists) return;

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

        await ctx.Playlists
            .Where(p => p.Id == playlistId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
    }

    /// <inheritdoc />
    public async Task<int> AddTracksAsync(string playlistId, IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            int added = 0;
            foreach (var trackId in trackIds)
            {
                await SetLikedDirectAsync(trackId, ownerId, true, ct);
                added++;
            }
            return added;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var trackIdList = trackIds as IList<string> ?? [.. trackIds];
        if (trackIdList.Count == 0) return 0;

        var existingTrackIds = await ctx.Tracks
            .Where(t => trackIdList.Contains(t.Id))
            .Select(t => t.Id)
            .ToHashSetAsync(ct);

        var alreadyLinked = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && trackIdList.Contains(pt.TrackId))
            .Select(pt => pt.TrackId)
            .ToHashSetAsync(ct);

        int maxPos = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .MaxAsync(pt => (int?)pt.Position, ct) ?? -1;

        int addedCount = 0;
        for (int i = 0; i < trackIdList.Count; i++)
        {
            var trackId = trackIdList[i];
            if (!existingTrackIds.Contains(trackId) || alreadyLinked.Contains(trackId))
                continue;

            maxPos++;
            ctx.PlaylistTracks.Add(new PlaylistTrackEntity
            {
                PlaylistId = playlistId,
                TrackId = trackId,
                Position = maxPos
            });
            addedCount++;
        }

        if (addedCount > 0)
        {
            await ctx.SaveChangesAsync(ct);

            await ctx.Playlists
                .Where(p => p.Id == playlistId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
        }

        return addedCount;
    }

    /// <inheritdoc />
    public async Task RemoveTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await SetLikedDirectAsync(trackId, ownerId, false, ct);
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entry = await ctx.PlaylistTracks
            .FirstOrDefaultAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct);

        if (entry is null) return;

        int removedPos = entry.Position;
        ctx.PlaylistTracks.Remove(entry);
        await ctx.SaveChangesAsync(ct);

        await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.Position > removedPos)
            .ExecuteUpdateAsync(s => s.SetProperty(pt => pt.Position, pt => pt.Position - 1), ct);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<bool> ContainsTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks.AnyAsync(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId, ct);
        }

        return await ctx.PlaylistTracks
            .AnyAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct);
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetPlaylistsForTrackAsync(string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ids = await ctx.PlaylistTracks
            .Where(pt => pt.TrackId == trackId && pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (pt.Playlist.OwnerId == ownerId || pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest"))
            .Select(pt => pt.PlaylistId)
            .ToListAsync(ct);

        var set = new HashSet<string>(ids, StringComparer.Ordinal);

        var isLiked = await ctx.LikedTracks.AnyAsync(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId, ct);
        if (isLiked)
        {
            set.Add(LibraryService.LikedPlaylistId);
        }

        return set;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, HashSet<string>>> GetPlaylistsForTracksAsync(
        IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default)
    {
        var ids = trackIds as IList<string> ?? [.. trackIds];
        if (ids.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var links = await ctx.PlaylistTracks
            .Where(pt => ids.Contains(pt.TrackId) && pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (pt.Playlist.OwnerId == ownerId || pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest"))
            .Select(pt => new { pt.TrackId, pt.PlaylistId })
            .ToListAsync(ct);

        var likedTrackIds = await ctx.LikedTracks
            .Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && ids.Contains(lt.TrackId))
            .Select(lt => lt.TrackId)
            .ToHashSetAsync(ct);

        var result = new Dictionary<string, HashSet<string>>(ids.Count);
        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (!result.TryGetValue(link.TrackId, out var set))
            {
                set = [];
                result[link.TrackId] = set;
            }
            set.Add(link.PlaylistId);
        }

        foreach (var trackId in likedTrackIds)
        {
            if (!result.TryGetValue(trackId, out var set))
            {
                set = [];
                result[trackId] = set;
            }
            set.Add(LibraryService.LikedPlaylistId);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<long> GetTotalDurationTicksAsync(string playlistId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks
                .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
                .Join(ctx.Tracks,
                    lt => lt.TrackId,
                    t => t.Id,
                    (lt, t) => t.DurationTicks)
                .SumAsync(ct);
        }

        var totalTicks = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (pt.Playlist.OwnerId == ownerId || pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest"))
            .Join(ctx.Tracks,
                pt => pt.TrackId,
                t => t.Id,
                (pt, t) => t.DurationTicks)
            .SumAsync(ct);

        return totalTicks;
    }

    /// <inheritdoc />
    public async Task<long> GetTotalLibraryDurationAsync(string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var customTrackIds = ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (pt.Playlist.OwnerId == ownerId || pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest"))
            .Select(pt => pt.TrackId);

        var likedTrackIds = ctx.LikedTracks
            .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
            .Select(lt => lt.TrackId);

        var allUserTracks = customTrackIds.Union(likedTrackIds);

        var totalTicks = await ctx.Tracks
            .Where(t => allUserTracks.Contains(t.Id))
            .SumAsync(t => t.DurationTicks, ct);

        return totalTicks;
    }

    private async Task SetLikedDirectAsync(string trackId, string ownerId, bool liked, CancellationToken ct)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (liked)
        {
            var exists = await ctx.LikedTracks.AnyAsync(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId, ct);
            if (!exists)
            {
                ctx.LikedTracks.Add(new LikedTrackEntity { OwnerId = ownerId, TrackId = trackId, LikedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync(ct);
            }
        }
        else
        {
            await ctx.LikedTracks.Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId).ExecuteDeleteAsync(ct);
        }
    }

    #region SetVideoId

    public async Task<string?> GetSetVideoIdAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        return await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId)
            .Select(pt => pt.SetVideoId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task UpdateSetVideoIdAsync(string playlistId, string trackId, string setVideoId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId)
            .ExecuteUpdateAsync(s => s.SetProperty(pt => pt.SetVideoId, setVideoId), ct);
    }

    public async Task UpdateSetVideoIdsAsync(
        string playlistId,
        IReadOnlyList<(string TrackId, string SetVideoId)> mappings,
        CancellationToken ct = default)
    {
        if (mappings.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var entries = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .ToListAsync(ct);

        var entryMap = new Dictionary<string, PlaylistTrackEntity>(entries.Count, StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
            entryMap[entries[i].TrackId] = entries[i];

        int updated = 0;
        for (int i = 0; i < mappings.Count; i++)
        {
            var (trackId, setVideoId) = mappings[i];
            if (entryMap.TryGetValue(trackId, out var entry))
            {
                entry.SetVideoId = setVideoId;
                updated++;
            }
        }

        if (updated > 0)
            await ctx.SaveChangesAsync(ct);
    }

    #endregion

    #region Mapping

    private static Playlist MapToModel(PlaylistEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        YoutubeId = e.YoutubeId,
        Author = e.Author,
        ThumbnailUrl = e.ThumbnailUrl,
        CustomColor = e.CustomColor,
        ComputedColor = e.ComputedColor,
        Description = e.Description,
        SyncMode = (PlaylistSyncMode)e.SyncMode,
        OwnerId = e.OwnerId,
        UpdatedAt = e.UpdatedAt
    };

    private static PlaylistEntity MapToEntity(Playlist m) => new()
    {
        Id = m.Id,
        Name = m.StoredName,
        YoutubeId = m.YoutubeId,
        Author = m.Author,
        ThumbnailUrl = m.ThumbnailUrl,
        CustomColor = m.CustomColor,
        ComputedColor = m.ComputedColor,
        Description = m.Description,
        SyncMode = (int)m.SyncMode,
        OwnerId = m.OwnerId,
        UpdatedAt = DateTime.UtcNow
    };

    #endregion
}