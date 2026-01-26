using LMP.Core.Models;

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
            // MusicClient теперь возвращает сразу List<TrackInfo>
            var likedTracks = await _provider.GetClient().Music.GetLikedTracksAsync();

            // Дополнительно проставляем IsLiked (хотя MusicClient это уже делает)
            // и проверяем валидность данных
            foreach (var track in likedTracks)
            {
                track.IsLiked = true;
                if (!track.Id.StartsWith("yt_"))
                {
                    track.Id = "yt_" + track.Id;
                }
            }

            return likedTracks;
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks (LM): {ex.Message} ({ex.StatusCode})");
            return [];
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks (LM): {ex.Message}");
            return [];
        }
    }

    public async Task<string> CreatePlaylistAsync(string title, string description = "")
    {
        return await _provider.CreatePlaylistAsync(title) ?? throw new Exception("Create failed");
    }

    public async Task DeletePlaylistAsync(string youtubePlaylistId)
    {
        // Удаление пока не реализовано в InnerTube
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
            // MusicClient возвращает List<LMP.Core.Models.Playlist>
            var ytPlaylists = await _provider.GetClient().Music.GetLibraryPlaylistsAsync();

            // Данные уже в нужном формате, просто возвращаем
            return ytPlaylists;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to get user playlists: {ex.Message}");
            return [];
        }
    }
}