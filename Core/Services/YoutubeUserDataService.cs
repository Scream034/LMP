using LMP.Core.Models;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Services;

public partial class YoutubeUserDataService
{
    private readonly YoutubeProvider _provider;
    private readonly CookieAuthService _auth;

    public YoutubeUserDataService(YoutubeProvider provider, CookieAuthService auth)
    {
        _provider = provider;
        _auth = auth;
    }

    /// <summary>
    /// Получает лайкнутые треки в зависимости от режима синхронизации.
    /// </summary>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(LikeSyncMode mode = LikeSyncMode.MusicOnly)
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            List<TrackInfo> likedTracks = [];

            switch (mode)
            {
                case LikeSyncMode.MusicOnly:
                    // Используем YouTube Music API (LM плейлист)
                    Log.Info("[Sync] Fetching Music Likes (LM) from YouTube Music...");
                    try
                    {
                        var musicLikes = await _provider.GetClient().Music.GetLikedTracksAsync();
                        likedTracks.AddRange(musicLikes);
                        Log.Info($"[Sync] Got {musicLikes.Count} music likes from LM.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Sync] Music API failed, falling back to LL: {ex.Message}");
                        // Fallback на LL с фильтрацией
                        var allLikes = await GetAllLikedVideosAsync();
                        likedTracks.AddRange(allLikes.Where(t => t.IsMusic));
                    }
                    break;

                case LikeSyncMode.AllVideos:
                    // Используем стандартный YouTube API (LL плейлист)
                    Log.Info("[Sync] Fetching ALL Liked Videos (LL)...");
                    likedTracks = await GetAllLikedVideosAsync();
                    break;

                case LikeSyncMode.LocalOnly:
                    // Не синхронизируем с облаком
                    Log.Info("[Sync] LocalOnly mode - no cloud sync.");
                    return [];
            }

            // Помечаем все как лайкнутые и добавляем префикс
            foreach (var track in likedTracks)
            {
                track.IsLiked = true;
                if (!track.Id.StartsWith("yt_"))
                {
                    track.Id = "yt_" + track.Id;
                }
            }

            Log.Info($"[Sync] Total liked tracks: {likedTracks.Count}");
            return likedTracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks: {ex.Message}");
            return [];
        }
    }

    private async Task<List<TrackInfo>> GetAllLikedVideosAsync()
    {
        return await _provider.GetClient().Playlists
            .GetVideosAsync(new PlaylistId("LL"))
            .TakeAsync(1000)
            .ToListAsync();
    }

    public async Task RateVideoAsync(string videoId, string rating)
    {
        await _provider.LikeTrackAsync(videoId, rating == "like");
    }

    public async Task<(string Name, string Email, string AvatarUrl)> GetAccountInfoAsync()
    {
        if (!_auth.IsAuthenticated) return ("Guest", "", "");

        try
        {
            var json = await _provider.GetClient().Music.GetAccountMenuAsync();

            var header = json.GetPropertyOrNull("actions")?.EnumerateArrayOrNull()?.FirstOrDefault()
                .GetPropertyOrNull("openPopupAction")
                ?.GetPropertyOrNull("popup")
                ?.GetPropertyOrNull("multiPageMenuRenderer")
                ?.GetPropertyOrNull("header")
                ?.GetPropertyOrNull("activeAccountHeaderRenderer");

            if (header != null)
            {
                var name = header.Value.GetPropertyOrNull("accountName")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull() ?? "User";
                var email = header.Value.GetPropertyOrNull("email")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull() ?? "";

                var thumbs = header.Value.GetPropertyOrNull("accountPhoto")?.GetPropertyOrNull("thumbnails");
                var avatar = thumbs?.EnumerateArrayOrNull()?.LastOrDefault().GetPropertyOrNull("url")?.GetStringOrNull() ?? "";

                return (name, email, avatar);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to get account menu: {ex.Message}");
        }
        return ("User", "", "");
    }

    public async Task<string> CreatePlaylistAsync(string title, string description = "")
    {
        return await _provider.CreatePlaylistAsync(title) ?? throw new Exception("Create failed");
    }

    public async Task DeletePlaylistAsync(string youtubePlaylistId)
    {
        await Task.CompletedTask;
    }

    public async Task<string> AddTrackToPlaylistAsync(string youtubePlaylistId, string videoId)
    {
        await _provider.AddToPlaylistAsync(youtubePlaylistId, videoId);
        return "ok";
    }

    public async Task<List<Playlist>> GetMyPlaylistsAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            var ytPlaylists = await _provider.GetClient().Music.GetLibraryPlaylistsAsync();
            return ytPlaylists;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to get user playlists: {ex.Message}");
            return [];
        }
    }
}