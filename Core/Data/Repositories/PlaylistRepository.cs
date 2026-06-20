using System.Runtime.CompilerServices;
using LMP.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Репозиторий списков воспроизведения на базе SQLite.
/// Обеспечивает строгую изоляцию плейлистов на уровне аккаунтов:
/// аутентифицированные пользователи видят только свои плейлисты,
/// гостевой режим объединяет записи с <c>OwnerId</c> равным <c>""</c> и <c>"guest"</c>.
/// </summary>
public sealed class PlaylistRepository(IDbContextFactory<LibraryDbContext> factory) : IPlaylistRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory = factory;

    /// <summary>
    /// Определяет, является ли владелец гостем (пустая строка или литерал "guest").
    /// Гостевые записи с <c>OwnerId=""</c> и <c>OwnerId="guest"</c> считаются эквивалентными.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGuest(string ownerId) => string.IsNullOrEmpty(ownerId) || ownerId == "guest";

    /// <inheritdoc />
    public async Task<Playlist?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default)
    {
        if (id == LibraryService.LikedPlaylistId)
        {
            var trackIds = await GetTrackIdsAsync(id, ownerId, ct).ConfigureAwait(false);
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

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = await ctx.Playlists
            .FirstOrDefaultAsync(p => p.Id == id && p.Id != LibraryService.LikedPlaylistId &&
                (IsGuest(ownerId)
                    ? (p.OwnerId == "" || p.OwnerId == "guest")
                    : p.OwnerId == ownerId), ct).ConfigureAwait(false);

        if (entity is null) return null;

        var playlist = MapToModel(entity);

        playlist.TrackIds = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == id)
            .OrderBy(pt => pt.Position)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct).ConfigureAwait(false);

        playlist.TrackCount = playlist.TrackIds.Count;

        return playlist;
    }

    /// <inheritdoc />
    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var playlistsWithCounts = await ctx.Playlists
            .Where(p => p.Id != LibraryService.LikedPlaylistId &&
                (IsGuest(ownerId)
                    ? (p.OwnerId == "" || p.OwnerId == "guest")
                    : p.OwnerId == ownerId))
            .Select(p => new
            {
                Playlist = p,
                TrackCount = ctx.PlaylistTracks.Count(pt => pt.PlaylistId == p.Id)
            })
            .OrderBy(x => x.Playlist.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var list = new List<(Playlist Playlist, int TrackCount)>(playlistsWithCounts.Count + 1);

        var likedTrackCount = await ctx.LikedTracks
            .CountAsync(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId, ct).ConfigureAwait(false);

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
        var withCounts = await GetAllWithCountsAsync(ownerId, ct).ConfigureAwait(false);
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
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks
                .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
                .OrderByDescending(lt => lt.LikedAt)
                .Select(lt => lt.TrackId)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        return await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetTrackIdsAsync(
        string playlistId, string ownerId, int limit, int offset = 0, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks
                .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
                .OrderByDescending(lt => lt.LikedAt)
                .Skip(offset)
                .Take(limit)
                .Select(lt => lt.TrackId)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        return await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .Skip(offset)
            .Take(limit)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetTrackCountAsync(string playlistId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks.CountAsync(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId, ct).ConfigureAwait(false);
        }

        return await ctx.PlaylistTracks.CountAsync(pt => pt.PlaylistId == playlistId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Playlist playlist, CancellationToken ct = default)
    {
        if (playlist.Id == LibraryService.LikedPlaylistId)
            return;

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.Playlists.FirstOrDefaultAsync(p => p.Id == playlist.Id, ct).ConfigureAwait(false);

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

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ctx.Playlists.Where(p => p.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RenameAsync(string id, string newName, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ctx.Playlists
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, newName)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddTrackAsync(string playlistId, string trackId, string ownerId, int? position = null, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await SetLikedDirectAsync(trackId, ownerId, true, ct).ConfigureAwait(false);
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var exists = await ctx.PlaylistTracks
            .AnyAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct).ConfigureAwait(false);
        if (exists) return;

        var trackExists = await ctx.Tracks.AnyAsync(t => t.Id == trackId, ct).ConfigureAwait(false);
        if (!trackExists)
        {
            Log.Warn($"[PlaylistRepo] Cannot add track {trackId} to playlist {playlistId} - track does not exist");
            return;
        }

        int pos = position ?? await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .MaxAsync(pt => (int?)pt.Position, ct).ConfigureAwait(false) + 1 ?? 0;

        ctx.PlaylistTracks.Add(new PlaylistTrackEntity
        {
            PlaylistId = playlistId,
            TrackId = trackId,
            Position = pos
        });

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        await ctx.Playlists
            .Where(p => p.Id == playlistId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> AddTracksAsync(string playlistId, IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            int added = 0;
            foreach (var trackId in trackIds)
            {
                await SetLikedDirectAsync(trackId, ownerId, true, ct).ConfigureAwait(false);
                added++;
            }
            return added;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var trackIdList = trackIds as IList<string> ?? [.. trackIds];
        if (trackIdList.Count == 0) return 0;

        var existingTrackIds = await ctx.Tracks
            .Where(t => trackIdList.Contains(t.Id))
            .Select(t => t.Id)
            .ToHashSetAsync(ct).ConfigureAwait(false);

        var alreadyLinked = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && trackIdList.Contains(pt.TrackId))
            .Select(pt => pt.TrackId)
            .ToHashSetAsync(ct).ConfigureAwait(false);

        int maxPos = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .MaxAsync(pt => (int?)pt.Position, ct).ConfigureAwait(false) ?? -1;

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
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            await ctx.Playlists
                .Where(p => p.Id == playlistId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct).ConfigureAwait(false);
        }

        return addedCount;
    }

    /// <inheritdoc />
    public async Task RemoveTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await SetLikedDirectAsync(trackId, ownerId, false, ct).ConfigureAwait(false);
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entry = await ctx.PlaylistTracks
            .FirstOrDefaultAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct).ConfigureAwait(false);

        if (entry is null) return;

        int removedPos = entry.Position;
        ctx.PlaylistTracks.Remove(entry);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.Position > removedPos)
            .ExecuteUpdateAsync(s => s.SetProperty(pt => pt.Position, pt => pt.Position - 1), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        if (oldIndex == newIndex) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var tracks = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .ToListAsync(ct).ConfigureAwait(false);

        if (oldIndex < 0 || oldIndex >= tracks.Count ||
            newIndex < 0 || newIndex >= tracks.Count) return;

        var movingTrack = tracks[oldIndex];
        tracks.RemoveAt(oldIndex);
        tracks.Insert(newIndex, movingTrack);

        for (int i = 0; i < tracks.Count; i++)
        {
            tracks[i].Position = i;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks.AnyAsync(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId, ct).ConfigureAwait(false);
        }

        return await ctx.PlaylistTracks
            .AnyAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetPlaylistsForTrackAsync(string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var ids = await ctx.PlaylistTracks
            .Where(pt => pt.TrackId == trackId && pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (IsGuest(ownerId)
                    ? (pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest")
                    : pt.Playlist.OwnerId == ownerId))
            .Select(pt => pt.PlaylistId)
            .ToListAsync(ct).ConfigureAwait(false);

        var set = new HashSet<string>(ids, StringComparer.Ordinal);

        var isLiked = await ctx.LikedTracks.AnyAsync(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId, ct).ConfigureAwait(false);
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

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var links = await ctx.PlaylistTracks
            .Where(pt => ids.Contains(pt.TrackId) && pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (IsGuest(ownerId)
                    ? (pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest")
                    : pt.Playlist.OwnerId == ownerId))
            .Select(pt => new { pt.TrackId, pt.PlaylistId })
            .ToListAsync(ct).ConfigureAwait(false);

        var likedTrackIds = await ctx.LikedTracks
            .Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && ids.Contains(lt.TrackId))
            .Select(lt => lt.TrackId)
            .ToHashSetAsync(ct).ConfigureAwait(false);

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
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await ctx.LikedTracks
                .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
                .Join(ctx.Tracks,
                    lt => lt.TrackId,
                    t => t.Id,
                    (lt, t) => t.DurationTicks)
                .SumAsync(ct).ConfigureAwait(false);
        }

        var totalTicks = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (IsGuest(ownerId)
                    ? (pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest")
                    : pt.Playlist.OwnerId == ownerId))
            .Join(ctx.Tracks,
                pt => pt.TrackId,
                t => t.Id,
                (pt, t) => t.DurationTicks)
            .SumAsync(ct).ConfigureAwait(false);

        return totalTicks;
    }

    /// <inheritdoc />
    public async Task<long> GetTotalLibraryDurationAsync(string ownerId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var customTrackIds = ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId != LibraryService.LikedPlaylistId &&
                (IsGuest(ownerId)
                    ? (pt.Playlist.OwnerId == "" || pt.Playlist.OwnerId == "guest")
                    : pt.Playlist.OwnerId == ownerId))
            .Select(pt => pt.TrackId);

        var likedTrackIds = ctx.LikedTracks
            .Where(lt => IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId)
            .Select(lt => lt.TrackId);

        var allUserTracks = customTrackIds.Union(likedTrackIds);

        var totalTicks = await ctx.Tracks
            .Where(t => allUserTracks.Contains(t.Id))
            .SumAsync(t => t.DurationTicks, ct).ConfigureAwait(false);

        return totalTicks;
    }

    /// <inheritdoc />
    public async Task<int> AdoptOrphanPlaylistsAsync(string newOwnerId, CancellationToken ct = default)
    {
        if (IsGuest(newOwnerId)) return 0;

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var adopted = await ctx.Playlists
            .Where(p => p.OwnerId == "" || p.OwnerId == "guest")
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.OwnerId, newOwnerId)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct).ConfigureAwait(false);

        if (adopted > 0)
            Log.Info($"[PlaylistRepo] Adopted {adopted} orphan playlist(s) for owner {newOwnerId}");

        return adopted;
    }

    private async Task SetLikedDirectAsync(string trackId, string ownerId, bool liked, CancellationToken ct)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (liked)
        {
            var exists = await ctx.LikedTracks.AnyAsync(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId, ct).ConfigureAwait(false);
            if (!exists)
            {
                ctx.LikedTracks.Add(new LikedTrackEntity { OwnerId = ownerId, TrackId = trackId, LikedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            await ctx.LikedTracks.Where(lt => (IsGuest(ownerId) ? (lt.OwnerId == "" || lt.OwnerId == "guest") : lt.OwnerId == ownerId) && lt.TrackId == trackId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }

    #region SetVideoId

    public async Task<string?> GetSetVideoIdAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId)
            .Select(pt => pt.SetVideoId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateSetVideoIdAsync(string playlistId, string trackId, string setVideoId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId)
            .ExecuteUpdateAsync(s => s.SetProperty(pt => pt.SetVideoId, setVideoId), ct).ConfigureAwait(false);
    }

    public async Task UpdateSetVideoIdsAsync(
        string playlistId,
        IReadOnlyList<(string TrackId, string SetVideoId)> mappings,
        CancellationToken ct = default)
    {
        if (mappings.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entries = await ctx.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .ToListAsync(ct).ConfigureAwait(false);

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
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
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