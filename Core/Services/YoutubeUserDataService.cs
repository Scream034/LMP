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

    public async Task RateVideoAsync(string videoId, string rating)
    {
        await _provider.LikeTrackAsync(videoId, rating == "like");
    }

    public async Task<List<TrackInfo>> GetLikedTracksAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            Log.Info("[Sync] Starting liked videos sync from YouTube (LL)...");
            
            // Используем обычный клиент плейлистов для LL. 
            // Берем первые 1000 треков для начала.
            var likedTracks = await _provider.GetClient().Playlists
                .GetVideosAsync(new PlaylistId("LL"))
                .TakeAsync(1000) 
                .ToListAsync();

            if (likedTracks.Count == 0)
            {
                Log.Info("[Sync] Playlist LL returned 0 tracks. Trying fallback to Music Liked (LM)...");
                // Если LL пуст (или не доступен), пробуем Music API (VLLM)
                // Это вернет только музыку, но лучше чем ничего.
                try 
                {
                    var musicLikes = await _provider.GetClient().Music.GetLikedTracksAsync();
                    likedTracks.AddRange(musicLikes);
                } 
                catch (Exception ex) 
                {
                     Log.Warn($"[Sync] Music fallback failed: {ex.Message}");
                }
            }

            foreach (var track in likedTracks)
            {
                track.IsLiked = true;
                if (!track.Id.StartsWith("yt_"))
                {
                    track.Id = "yt_" + track.Id;
                }
            }
            
            Log.Info($"[Sync] Fetched {likedTracks.Count} liked items.");
            return likedTracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks: {ex.Message}");
            return [];
        }
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