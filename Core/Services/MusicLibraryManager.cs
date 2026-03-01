using LMP.Core.Models;
using LMP.Core.Youtube.Music;
using ReactiveUI;

namespace LMP.Core.Services;

/// <summary>
/// Координирует операции между локальной БД и YouTube.
/// Single Responsibility: оркестрация sync-операций.
/// </summary>
public class MusicLibraryManager : ReactiveObject
{
    private readonly LibraryService _library;
    private readonly YoutubeUserDataService _ytUser;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;

    public MusicLibraryManager(
        LibraryService library,
        YoutubeUserDataService ytUser,
        YoutubeProvider youtube,
        CookieAuthService auth)
    {
        _library = library;
        _ytUser = ytUser;
        _youtube = youtube;
        _auth = auth;
    }

    #region Likes

    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        if (_auth.IsAuthenticated)
        {
            try
            {
                bool newStatus = !track.IsLiked;
                await _youtube.LikeTrackAsync(track.Id, newStatus);
                await _library.ToggleLikeAsync(track, ct);
                Log.Info($"[Sync] Track {track.Id} liked={newStatus} on YouTube.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to sync like: {ex.Message}");
                // Всё равно переключаем локально
                await _library.ToggleLikeAsync(track, ct);
            }
        }
        else
        {
            await _library.ToggleLikeAsync(track, ct);
        }
    }

    public async Task SyncLikedTracksAsync(CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated)
        {
            Log.Info("[Sync] Not authenticated. Skipping liked videos sync.");
            return;
        }

        try
        {
            Log.Info("[Sync] Starting liked videos sync from YouTube...");

            var likedTracks = await _ytUser.GetLikedTracksAsync();
            if (likedTracks.Count == 0) return;

            var localLikedTrackIds = await _library.GetPlaylistTrackIdsAsync(
                LibraryService.LikedPlaylistId, ct);
            var existingIds = new HashSet<string>(localLikedTrackIds, StringComparer.Ordinal);
            int addedCount = 0;

            foreach (var track in likedTracks)
            {
                if (ct.IsCancellationRequested) break;

                track.IsLiked = true;
                await _library.AddOrUpdateTrackAsync(track, ct);

                if (existingIds.Add(track.Id))
                {
                    await _library.AddTrackToPlaylistAsync(
                        track, LibraryService.LikedPlaylistId, ct);
                    addedCount++;
                }
            }

            Log.Info(addedCount > 0
                ? $"[Sync] Added {addedCount} new liked tracks."
                : "[Sync] No new liked tracks found.");
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Liked tracks sync failed: {ex.Message}");
        }
    }

    #endregion

    #region Playlist CRUD

    /// <summary>
    /// Создаёт плейлист локально и опционально в YouTube.
    /// </summary>
    public async Task<Playlist> CreatePlaylistAsync(
        string name,
        PlaylistSyncMode mode,
        CancellationToken ct = default)
    {
        var newPlaylist = await _library.CreatePlaylistAsync(name, ct);
        newPlaylist.SyncMode = mode;

        if (mode == PlaylistSyncMode.TwoWaySync && _auth.IsAuthenticated)
        {
            try
            {
                var ytId = await _youtube.CreatePlaylistAsync(name);
                newPlaylist.YoutubeId = ytId;
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to create remote playlist: {ex.Message}");
                newPlaylist.SyncMode = PlaylistSyncMode.LocalOnly;
            }
        }

        await _library.AddOrUpdatePlaylistAsync(newPlaylist, ct);
        return newPlaylist;
    }

    public async Task DeletePlaylistAsync(
        string playlistId, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        // Удаляем локально
        await _library.DeletePlaylistAsync(playlistId, ct);

        // Удаляем из YouTube если синхронизирован
        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                await _youtube.DeletePlaylistAsync(playlist.YoutubeId);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Error deleting remote playlist: {ex.Message}");
            }
        }
    }

    #endregion

    #region Playlist Operations

    public async Task UploadPlaylistToAccountAsync(
        string localPlaylistId, CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        var localPl = await _library.GetPlaylistAsync(localPlaylistId, ct);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var ytId = await _youtube.CreatePlaylistAsync(localPl.Name);
            if (string.IsNullOrEmpty(ytId))
                throw new InvalidOperationException("YouTube returned empty playlist ID.");

            localPl.YoutubeId = ytId;
            localPl.SyncMode = PlaylistSyncMode.TwoWaySync;
            await _library.AddOrUpdatePlaylistAsync(localPl, ct);

            // BUG 2 FIX: Read track IDs from DB instead of transient TrackIds list
            var trackIds = await _library.GetPlaylistTrackIdsAsync(localPlaylistId, ct);

            // Фоновая загрузка треков
            _ = Task.Run(async () =>
            {
                int uploaded = 0;
                for (int i = 0; i < trackIds.Count; i++)
                {
                    var trackId = trackIds[i];
                    if (!trackId.StartsWith("yt_")) continue;

                    var setVideoId = await _youtube.AddToPlaylistAsync(ytId, trackId);

                    // Persist setVideoId for future removal support (BUG 3)
                    if (!string.IsNullOrEmpty(setVideoId))
                    {
                        try
                        {
                            await _library.UpdateSetVideoIdAsync(
                                localPlaylistId, trackId, setVideoId);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[Sync] Failed to save setVideoId: {ex.Message}");
                        }
                    }

                    uploaded++;

                    // Rate limiting
                    await Task.Delay(uploaded % 5 == 0 ? 1000 : 300);
                }
                Log.Info($"[Sync] Uploaded {uploaded} tracks to {ytId}");
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"Upload failed: {ex.Message}");
        }
    }

    public async Task AddTrackToPlaylistAsync(
        string playlistId, TrackInfo track, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        await _library.AddOrUpdateTrackAsync(track, ct);

        // BUG 2 FIX: Check DB instead of transient TrackIds
        bool alreadyInPlaylist = await _library.IsTrackInPlaylistAsync(track.Id, playlistId, ct);
        if (!alreadyInPlaylist)
            await _library.AddTrackToPlaylistAsync(track, playlistId, ct);

        // Синхронизируем с YouTube
        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                var setVideoId = await _youtube.AddToPlaylistAsync(playlist.YoutubeId, track.Id);

                // Persist setVideoId for future removal support (BUG 3)
                if (!string.IsNullOrEmpty(setVideoId))
                {
                    await _library.UpdateSetVideoIdAsync(playlistId, track.Id, setVideoId, ct);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Add track to cloud failed: {ex.Message}");
            }
        }
    }

    public async Task RemoveTrackFromPlaylistAsync(
     string playlistId, string trackId, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);

        // Grab setVideoId BEFORE removing from local DB
        string? setVideoId = null;
        bool needsYoutubeSync = playlist != null
            && playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated;

        if (needsYoutubeSync)
        {
            setVideoId = await _library.GetSetVideoIdAsync(playlistId, trackId, ct);

            // BUG 3 FIX: On-demand fetch if setVideoId not in local DB
            if (string.IsNullOrEmpty(setVideoId))
            {
                Log.Info($"[Sync] No cached setVideoId for {trackId}, fetching from YouTube...");
                try
                {
                    var items = await _youtube.GetPlaylistItemsWithSetVideoIdAsync(
                        playlist!.YoutubeId!, ct);

                    if (items.Count > 0)
                    {
                        // Build mappings and persist all setVideoIds for future use
                        var mappings = new List<(string TrackId, string SetVideoId)>(items.Count);
                        string? targetSetVideoId = null;

                        for (int i = 0; i < items.Count; i++)
                        {
                            var item = items[i];
                            var localTrackId = "yt_" + item.VideoId;
                            mappings.Add((localTrackId, item.SetVideoId));

                            if (string.Equals(localTrackId, trackId, StringComparison.Ordinal)
                                || string.Equals(item.VideoId, trackId, StringComparison.Ordinal))
                            {
                                targetSetVideoId = item.SetVideoId;
                            }
                        }

                        // Batch update all setVideoIds in DB
                        await _library.UpdateSetVideoIdsAsync(playlistId, mappings, ct);

                        setVideoId = targetSetVideoId;
                        Log.Info($"[Sync] Fetched {items.Count} setVideoIds, target: {setVideoId ?? "not found"}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Sync] Failed to fetch setVideoIds: {ex.Message}");
                }
            }
        }

        // Remove locally
        await _library.RemoveTrackFromPlaylistAsync(trackId, playlistId, ct);

        // Sync removal to YouTube
        if (needsYoutubeSync)
        {
            if (!string.IsNullOrEmpty(setVideoId))
            {
                // Fire-and-forget YouTube removal
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _youtube.RemoveFromPlaylistAsync(
                            playlist!.YoutubeId!, trackId, setVideoId);
                        Log.Info($"[Sync] Removed track {trackId} from YouTube playlist {playlist.YoutubeId}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Sync] Failed to remove track from cloud: {ex.Message}");
                    }
                });
            }
            else
            {
                Log.Warn($"[Sync] No setVideoId for track {trackId} in playlist {playlistId} — YouTube removal skipped");
            }
        }
    }

    public async Task MovePlaylistTrackAsync(
        string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        await _library.MoveTrackInPlaylistAsync(playlistId, oldIndex, newIndex, ct);
    }

    public async Task ConvertToLocalAsync(
        string playlistId, CancellationToken ct = default)
    {
        var pl = await _library.GetPlaylistAsync(playlistId, ct);
        if (pl == null) return;

        // BUG 2 FIX: Read track IDs from DB
        var trackIds = await _library.GetPlaylistTrackIdsAsync(playlistId, ct);

        var copy = new Playlist
        {
            Name = pl.Name + " (Local)",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackIds = trackIds,
            ThumbnailUrl = pl.ThumbnailUrl,
            CustomColor = pl.CustomColor,
            Author = "Me"
        };

        await _library.AddOrUpdatePlaylistAsync(copy, ct);
    }

    public async Task<bool> MergePlaylistsAsync(
        string sourceId, string targetId, CancellationToken ct = default)
    {
        var source = await _library.GetPlaylistAsync(sourceId, ct);
        var target = await _library.GetPlaylistAsync(targetId, ct);
        if (source == null || target == null || !target.IsLocal) return false;

        // BUG 2 FIX: Read track IDs from DB
        var sourceTrackIds = await _library.GetPlaylistTrackIdsAsync(sourceId, ct);
        var targetTrackIds = await _library.GetPlaylistTrackIdsAsync(targetId, ct);
        var existing = new HashSet<string>(targetTrackIds, StringComparer.Ordinal);
        int added = 0;

        for (int i = 0; i < sourceTrackIds.Count; i++)
        {
            var trackId = sourceTrackIds[i];
            if (existing.Contains(trackId)) continue;

            var track = await _library.GetTrackAsync(trackId, ct);
            if (track != null)
            {
                await _library.AddTrackToPlaylistAsync(track, targetId, ct);
                added++;
            }
        }

        Log.Info($"[Merge] Added {added} tracks from '{source.Name}' to '{target.Name}'");
        return true;
    }

    #endregion
}