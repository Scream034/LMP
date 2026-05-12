using LMP.Core.Models;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Services;

/// <summary>
/// Сервис для работы с данными пользователя YouTube Music.
/// Делегирует сетевые операции в YoutubeProvider, добавляя бизнес-логику.
/// </summary>
public partial class YoutubeUserDataService
{
    private readonly YoutubeProvider _provider;
    private readonly CookieAuthService _auth;

    public YoutubeUserDataService(YoutubeProvider provider, CookieAuthService auth)
    {
        _provider = provider;
        _auth = auth;
    }

    #region Liked Tracks

    /// <summary>
    /// Получает лайкнутые треки в зависимости от режима синхронизации.
    /// </summary>
    /// <remarks>
    /// <para><b>MusicOnly:</b> Один запрос VLLM через WEB_REMIX — возвращает только музыку.
    /// Ранее дополнительно загружался плейлист LL (все лайки YouTube) для поиска "пропущенных"
    /// треков, что генерировало 10+ лишних HTTP-запросов. Убрано: YTM API — исчерпывающий
    /// источник музыкальных лайков, дублирование через LL не даёт значимого выигрыша.</para>
    ///
    /// <para><b>AllVideos:</b> Загружает LL целиком — все лайкнутые видео включая немузыкальные.</para>
    /// </remarks>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(
        LikeSyncMode mode = LikeSyncMode.MusicOnly)
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            List<TrackInfo> likedTracks;

            switch (mode)
            {
                case LikeSyncMode.MusicOnly:
                    Log.Info("[Sync] Fetching Music Likes (LM) from YouTube Music...");
                    try
                    {
                        likedTracks = await _provider.GetClient().Music.GetLikedTracksAsync();
                        Log.Info($"[Sync] Got {likedTracks.Count} music likes from LM.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Sync] Music API failed, falling back to LL: {ex.Message}");
                        var allLikes = await GetAllLikedVideosAsync();
                        likedTracks = allLikes.FindAll(t => t.IsMusic);
                    }
                    break;

                case LikeSyncMode.AllVideos:
                    Log.Info("[Sync] Fetching ALL Liked Videos (LL)...");
                    likedTracks = await GetAllLikedVideosAsync();
                    break;

                case LikeSyncMode.LocalOnly:
                    Log.Info("[Sync] LocalOnly mode - no cloud sync.");
                    return [];

                default:
                    return [];
            }

            for (int i = 0; i < likedTracks.Count; i++)
            {
                var track = likedTracks[i];
                track.IsLiked = true;
                if (!track.Id.StartsWith("yt_", StringComparison.Ordinal))
                    track.Id = "yt_" + track.Id;
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

    #endregion

    #region Rating

    public async Task RateVideoAsync(string videoId, string rating)
    {
        await _provider.LikeTrackAsync(videoId, rating == "like");
    }

    #endregion

    #region Playlist Operations

    public async Task<string> CreatePlaylistAsync(string title, string description = "")
    {
        // Description is no longer supported via WEB client mutations.
        // Title-only creation via PlaylistMutationController.
        return await _provider.CreatePlaylistAsync(title)
            ?? throw new InvalidOperationException("YouTube API returned null playlist ID.");
    }

    public async Task DeletePlaylistAsync(string youtubePlaylistId)
    {
        await _provider.DeletePlaylistAsync(youtubePlaylistId);
    }

    public async Task AddTrackToPlaylistAsync(string youtubePlaylistId, string videoId)
    {
        await _provider.AddToPlaylistAsync(youtubePlaylistId, videoId);
    }

    #endregion

    #region Account Info

    public async Task<(string Name, string Email, string AvatarUrl)> GetAccountInfoAsync()
    {
        if (!_auth.IsAuthenticated) return ("Guest", "", "");

        try
        {
            var json = await _provider.GetClient().Music.GetAccountMenuAsync();

            var header = json.GetPropertyOrNull("actions")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("openPopupAction")
                ?.GetPropertyOrNull("popup")
                ?.GetPropertyOrNull("multiPageMenuRenderer")
                ?.GetPropertyOrNull("header")
                ?.GetPropertyOrNull("activeAccountHeaderRenderer");

            if (header == null) return ("User", "", "");

            var name = header.Value.GetPropertyOrNull("accountName")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("text")
                ?.GetStringOrNull() ?? "User";

            var email = header.Value.GetPropertyOrNull("email")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("text")
                ?.GetStringOrNull() ?? "";

            var avatar = header.Value.GetPropertyOrNull("accountPhoto")
                ?.GetPropertyOrNull("thumbnails")
                ?.EnumerateArrayOrNull()
                ?.LastOrDefault()
                .GetPropertyOrNull("url")
                ?.GetStringOrNull() ?? "";

            return (name, email, avatar);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to get account menu: {ex.Message}");
            return ("User", "", "");
        }
    }

    #endregion

    #region Library

    public async Task<List<Playlist>> GetMyPlaylistsAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            return await _provider.GetClient().Music.GetLibraryPlaylistsAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to get user playlists: {ex.Message}");
            return [];
        }
    }

    #endregion
}