using LMP.Core.Models;
using ReactiveUI;

namespace LMP.Core.Services;

public class MusicLibraryManager : ReactiveObject
{
    private readonly LibraryService _library;
    private readonly YoutubeUserDataService _ytUser;
    private readonly CookieAuthService _auth;

    public MusicLibraryManager(
        LibraryService library,
        YoutubeUserDataService ytUser,
        CookieAuthService auth)
    {
        _library = library;
        _ytUser = ytUser;
        _auth = auth;
    }

    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        if (_auth.IsAuthenticated)
        {
            try
            {
                bool newStatus = !track.IsLiked;
                string rating = newStatus ? "like" : "none";

                await _ytUser.RateVideoAsync(track.Id, rating);
                await _library.ToggleLikeAsync(track, ct);

                Log.Info($"[Sync] Track {track.Id} rated '{rating}' on YouTube.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to sync like: {ex.Message}");
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

            // Получаем треки. Обычно YouTube API возвращает Most Recent first.
            var likedTracks = await _ytUser.GetLikedTracksAsync();
            var localLiked = await _library.GetLikedPlaylistAsync(ct);
            int addedCount = 0;

            if (likedTracks == null || likedTracks.Count == 0) return;

            var existingIds = new HashSet<string>(localLiked.TrackIds);

            // Давайте просто уберем Reverse(), как просили, чтобы вернуть "естественный" порядок прихода данных.
            IEnumerable<TrackInfo> tracksToProcess = likedTracks;

            foreach (var track in tracksToProcess)
            {
                if (ct.IsCancellationRequested) break;

                track.IsLiked = true;
                // Сохраняем в базу (Upsert)
                await _library.AddOrUpdateTrackAsync(track, ct);

                if (!existingIds.Contains(track.Id))
                {
                    // Добавляем в плейлист
                    await _library.AddTrackToPlaylistAsync(track, LibraryService.LikedPlaylistId, ct);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                Log.Info($"[Sync] Successfully added {addedCount} new liked tracks.");
            }
            else
            {
                Log.Info("[Sync] No new liked tracks found.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Liked tracks sync failed: {ex.Message}");
        }
    }

    public async Task CreatePlaylistAsync(string name, PlaylistSyncMode mode, CancellationToken ct = default)
    {
        var newPlaylist = await _library.CreatePlaylistAsync(name, ct);
        newPlaylist.SyncMode = mode;

        if (mode == PlaylistSyncMode.TwoWaySync && _auth.IsAuthenticated)
        {
            try
            {
                var ytId = await _ytUser.CreatePlaylistAsync(name, $"Created via {G.AppName}");
                newPlaylist.YoutubeId = ytId;
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to create remote playlist: {ex.Message}");
                newPlaylist.SyncMode = PlaylistSyncMode.LocalOnly;
            }
        }

        await _library.AddOrUpdatePlaylistAsync(newPlaylist, ct);
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        await _library.DeletePlaylistAsync(playlistId, ct);

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(playlist.YoutubeId) &&
            _auth.IsAuthenticated)
        {
            try
            {
                await _ytUser.DeletePlaylistAsync(playlist.YoutubeId);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Error deleting remote playlist: {ex.Message}");
            }
        }
    }

    public async Task UploadPlaylistToAccountAsync(string localPlaylistId, CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        var localPl = await _library.GetPlaylistAsync(localPlaylistId, ct);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var ytId = await _ytUser.CreatePlaylistAsync(localPl.Name, $"Uploaded from {G.AppName}");
            localPl.YoutubeId = ytId;
            localPl.SyncMode = PlaylistSyncMode.TwoWaySync;
            await _library.AddOrUpdatePlaylistAsync(localPl, ct);

            // Background upload tracks
            _ = Task.Run(async () =>
            {
                foreach (var trackId in localPl.TrackIds)
                {
                    if (trackId.StartsWith("yt_"))
                    {
                        await _ytUser.AddTrackToPlaylistAsync(ytId, trackId);
                        await Task.Delay(600);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Upload failed: {ex.Message}");
        }
    }

    public async Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        await _library.AddOrUpdateTrackAsync(track, ct);

        if (!playlist.TrackIds.Contains(track.Id))
        {
            await _library.AddTrackToPlaylistAsync(track, playlistId, ct);
        }

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(playlist.YoutubeId) &&
            _auth.IsAuthenticated)
        {
            try
            {
                await _ytUser.AddTrackToPlaylistAsync(playlist.YoutubeId, track.Id);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Add track failed: {ex.Message}");
            }
        }
    }

    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        await _library.RemoveTrackFromPlaylistAsync(trackId, playlistId, ct);
        // Remote removal via InnerTube requires setVideoId, skipped for now
    }

    public async Task MovePlaylistTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        await _library.MoveTrackInPlaylistAsync(playlistId, oldIndex, newIndex, ct);
        // Remote reordering requires PlaylistItem IDs, not implemented
    }

    public async Task ConvertToLocalAsync(string playlistId, CancellationToken ct = default)
    {
        var pl = await _library.GetPlaylistAsync(playlistId, ct);
        if (pl == null) return;

        var copy = new Playlist
        {
            Name = pl.Name + " (Local)",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackIds = [.. pl.TrackIds],
            ThumbnailUrl = pl.ThumbnailUrl,
            Author = "Me"
        };

        await _library.AddOrUpdatePlaylistAsync(copy, ct);
    }

    public async Task<bool> MergePlaylistsAsync(string sourceId, string targetId, CancellationToken ct = default)
    {
        var source = await _library.GetPlaylistAsync(sourceId, ct);
        var target = await _library.GetPlaylistAsync(targetId, ct);

        if (source == null || target == null || !target.IsLocal) return false;

        var existing = new HashSet<string>(target.TrackIds);
        int added = 0;

        foreach (var trackId in source.TrackIds)
        {
            if (!existing.Contains(trackId))
            {
                var track = await _library.GetTrackAsync(trackId, ct);
                if (track != null)
                {
                    await _library.AddTrackToPlaylistAsync(track, targetId, ct);
                    added++;
                }
            }
        }

        Log.Info($"[Merge] Added {added} tracks from '{source.Name}' to '{target.Name}'");

        return true;
    }
}