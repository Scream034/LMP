using LMP.Core.Models;
using ReactiveUI;

namespace LMP.Core.Services;

public class MusicLibraryManager(
    LibraryService library,
    YoutubeUserDataService ytUser,
    CookieAuthService auth) : ReactiveObject
{
    private readonly LibraryService _library = library;
    private readonly YoutubeUserDataService _ytUser = ytUser;
    private readonly CookieAuthService _auth = auth;

    public async Task ToggleLikeAsync(TrackInfo track)
    {
        // Сначала пытаемся отправить запрос, и только при успехе меняем локальный статус
        if (_auth.IsAuthenticated)
        {
            try
            {
                bool newStatus = !track.IsLiked;
                string rating = newStatus ? "like" : "none";

                await _ytUser.RateVideoAsync(track.Id, rating);

                // Только если не вылетело исключение:
                _library.ToggleLike(track);

                Log.Info($"[Sync] Track {track.Id} rated '{rating}' on YouTube.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to sync like: {ex.Message}");
                // Можно добавить уведомление пользователю через событие или DialogService
            }
        }
        else
        {
            // Если оффлайн, просто меняем локально
            _library.ToggleLike(track);
        }
    }

    public async Task SyncLikedTracksAsync()
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
            var localLiked = _library.GetLikedPlaylist();
            int addedCount = 0;

            if (likedTracks == null || likedTracks.Count == 0) return;

            var tracksToProcess = ((IEnumerable<TrackInfo>)likedTracks).Reverse();

            foreach (var track in tracksToProcess)
            {
                // 1. Обновляем статус
                track.IsLiked = true;
                
                // 2. Сохраняем сам трек. Это важно!
                // Если трека не было в базе, он создастся.
                // Если был, обновятся поля (включая IsLiked).
                _library.AddOrUpdateTrack(track);

                // 3. Обновляем плейлист
                if (!localLiked.TrackIds.Contains(track.Id))
                {
                    localLiked.TrackIds.Insert(0, track.Id);
                    track.InPlaylists.Add(LibraryService.LikedPlaylistId);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                localLiked.UpdatedAt = DateTime.Now;
                _library.AddOrUpdatePlaylist(localLiked);
                Log.Info($"[Sync] Successfully added {addedCount} new liked tracks.");
            } else
            {
                // Для существующих треков тоже полезно обновить метаданные (если они изменились на сервере)
                foreach(var t in likedTracks)
                {
                     // Просто убеждаемся, что IsLiked = true в базе
                     if (_library.GetTrack(t.Id)?.IsLiked == false)
                     {
                         var local = _library.GetTrack(t.Id);
                         if (local != null) 
                         {
                             local.IsLiked = true;
                             _library.AddOrUpdateTrack(local);
                         }
                     }
                }
                Log.Info("[Sync] No new liked tracks found.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Liked tracks sync failed: {ex.Message}");
        }
    }

    public async Task CreatePlaylistAsync(string name, PlaylistSyncMode mode)
    {
        var newPlaylist = _library.CreatePlaylist(name);
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
                Log.Error($"[Sync] Failed to create remote playlist. {ex.Message}");
                newPlaylist.SyncMode = PlaylistSyncMode.LocalOnly;
            }
        }
        _library.AddOrUpdatePlaylist(newPlaylist);
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        _library.RemovePlaylist(playlistId);

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

    public async Task UploadPlaylistToAccountAsync(string localPlaylistId)
    {
        if (!_auth.IsAuthenticated) return;
        var localPl = _library.GetPlaylist(localPlaylistId);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var ytId = await _ytUser.CreatePlaylistAsync(localPl.Name, $"Uploaded from {G.AppName}");
            localPl.YoutubeId = ytId;
            localPl.SyncMode = PlaylistSyncMode.TwoWaySync;
            _library.AddOrUpdatePlaylist(localPl);

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

    public async Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        _library.AddOrUpdateTrack(track);

        if (!playlist.TrackIds.Contains(track.Id))
        {
            playlist.TrackIds.Add(track.Id);
            playlist.UpdatedAt = DateTime.Now;
            _library.AddOrUpdatePlaylist(playlist);
            track.InPlaylists.Add(playlistId);
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

    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        if (playlist.TrackIds.Remove(trackId))
        {
            playlist.UpdatedAt = DateTime.Now;
            _library.AddOrUpdatePlaylist(playlist);

            var t = _library.GetTrack(trackId);
            t?.InPlaylists.Remove(playlistId);
        }
        // Removal from YouTube via InnerTube needs extra logic (setVideoId), skipped for now
    }

    // NEW: Method for reordering tracks
    public Task MovePlaylistTrackAsync(string playlistId, int oldIndex, int newIndex)
    {
        _library.MoveTrackInPlaylist(playlistId, oldIndex, newIndex);

        // Remote reordering on YouTube is skipped because it requires specific PlaylistItem IDs
        // which are not currently mapped/tracked in the local library.

        return Task.CompletedTask;
    }

    public void ConvertToLocal(string playlistId)
    {
        var pl = _library.GetPlaylist(playlistId);
        if (pl == null) return;

        var copy = new Playlist
        {
            Name = pl.Name + " (Local)",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackIds = [.. pl.TrackIds],
            ThumbnailUrl = pl.ThumbnailUrl,
            Author = "Me"
        };
        _library.AddOrUpdatePlaylist(copy);
    }
}
