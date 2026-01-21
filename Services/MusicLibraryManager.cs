using MyLiteMusicPlayer.Models;
using ReactiveUI;

namespace MyLiteMusicPlayer.Services;

public class MusicLibraryManager(
    LibraryService library,
    YoutubeUserDataService ytUser,
    GoogleAuthService auth) : ReactiveObject
{
    private readonly LibraryService _library = library;
    private readonly YoutubeUserDataService _ytUser = ytUser;
    private readonly GoogleAuthService _auth = auth;

    // --- ЛАЙКИ И СИНХРОНИЗАЦИЯ ---

    public async Task ToggleLikeAsync(TrackInfo track)
    {
        bool newStatus = !track.IsLiked;

        // 1. Оптимистичное обновление UI
        _library.ToggleLike(track);

        // 2. Если авторизованы - шлем запрос в API
        if (_auth.IsAuthenticated)
        {
            try
            {
                string rating = newStatus ? "like" : "none";
                await _ytUser.RateVideoAsync(track.Id, rating);
                Log.Info($"[Sync] Track {track.Id} rated '{rating}' on YouTube.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to sync like: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Синхронизирует плейлист "Liked Videos" с аккаунта в локальный "liked"
    /// </summary>
    public async Task SyncLikedTracksAsync()
    {
        if (!_auth.IsAuthenticated)
        {
            Log.Info("[Sync] Skipping Liked Sync: Not authenticated.");
            return;
        }

        try
        {
            Log.Info("[Sync] Starting liked videos sync from YouTube...");

            var likedTracks = await _ytUser.GetLikedTracksAsync();
            var localLiked = _library.GetLikedPlaylist();

            int addedCount = 0;
            // Инвертируем список, чтобы новые (в начале списка YT) добавлялись в начало локального
            // Но т.к. Insert(0,...) переворачивает, проходим с конца или просто проверяем
            // YT отдает последние лайкнутые первыми.

            foreach (var track in likedTracks)
            {
                track.IsLiked = true;
                _library.AddOrUpdateTrack(track);

                if (!localLiked.TrackIds.Contains(track.Id))
                {
                    // Добавляем в начало плейлиста
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

    // --- УПРАВЛЕНИЕ ПЛЕЙЛИСТАМИ ---

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
                newPlaylist.SyncMode = PlaylistSyncMode.TwoWaySync;
                Log.Info($"[Sync] Created remote playlist {ytId}");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to create remote playlist. Downgrading to LocalOnly. {ex.Message}");
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
                Log.Info($"[Sync] Deleted remote playlist {playlist.YoutubeId}");
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
                Log.Info($"[Sync] Uploaded all tracks to {localPl.Name}");
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Upload failed: {ex.Message}");
        }
    }

    // --- ДОБАВЛЕНИЕ/УДАЛЕНИЕ ТРЕКОВ ---

    public async Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        _library.AddOrUpdateTrack(track);

        // 1. Локальное обновление
        if (!playlist.TrackIds.Contains(track.Id))
        {
            playlist.TrackIds.Add(track.Id);
            playlist.UpdatedAt = DateTime.Now;
            _library.AddOrUpdatePlaylist(playlist);
            track.InPlaylists.Add(playlistId);
        }

        // 2. Сетевое обновление
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

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync && _auth.IsAuthenticated)
        {
            Log.Warn("[Sync] Remove from remote playlist not fully implemented (requires PlaylistItemId mapping)");
        }
    }

    // --- ИМПОРТ / МИГРАЦИЯ ---

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
            Author = _auth.State.UserName ?? "Me"
        };

        _library.AddOrUpdatePlaylist(copy);
    }
}