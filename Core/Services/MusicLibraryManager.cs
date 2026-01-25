using MyLiteMusicPlayer.Core.Models;
using ReactiveUI;

namespace MyLiteMusicPlayer.Core.Services;

public class MusicLibraryManager(
    LibraryService library,
    YoutubeUserDataService ytUser,
    CookieAuthService auth) : ReactiveObject // Changed
{
    private readonly LibraryService _library = library;
    private readonly YoutubeUserDataService _ytUser = ytUser;
    private readonly CookieAuthService _auth = auth; // Changed

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
        if (!_auth.IsAuthenticated) return;

        try
        {
            Log.Info("[Sync] Starting liked videos sync from YouTube...");
            var likedTracks = await _ytUser.GetLikedTracksAsync();
            var localLiked = _library.GetLikedPlaylist();
            int addedCount = 0;

            // Важный момент с порядком! 
            // likedTracks[0] - это самый последний лайкнутый (Newest).
            // Мы вставляем в начало списка (Insert 0).
            // Чтобы сохранить порядок [Newest, Old1, Old2...], нужно вставлять с конца.
            // Иначе получится [Old2, Old1, Newest].
            
            // Если likedTracks пуст или null, метод вернет управление без ошибок
            if (likedTracks == null || likedTracks.Count == 0) return;

            // Переворачиваем список для вставки
            var tracksToProcess = ((IEnumerable<TrackInfo>)likedTracks).Reverse();

            foreach (var track in tracksToProcess)
            {
                track.IsLiked = true;
                _library.AddOrUpdateTrack(track);

                if (!localLiked.TrackIds.Contains(track.Id))
                {
                    localLiked.TrackIds.Insert(0, track.Id);
                    track.InPlaylists.Add("liked");
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
                var ytId = await _ytUser.CreatePlaylistAsync(name, "Created via LiteMusicPlayer");
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
            var ytId = await _ytUser.CreatePlaylistAsync(localPl.Name, "Uploaded from LiteMusicPlayer");
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
            if (t != null) t.InPlaylists.Remove(playlistId);
        }
        // Removal from YouTube via InnerTube needs extra logic (setVideoId), skipped for now
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