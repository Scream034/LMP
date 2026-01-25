using MyLiteMusicPlayer.Core.Models;

namespace MyLiteMusicPlayer.Core.Services;

public partial class YoutubeUserDataService
{
    private readonly YoutubeProvider _provider;
    private readonly CookieAuthService _auth; // Changed

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
            // ИСПОЛЬЗУЕМ НОВЫЙ МЕТОД из MusicClient
            var likedItems = await _provider.GetClient().Music.GetLikedTracksAsync();

            // Конвертируем MusicItem в TrackInfo
            return likedItems.Select(item => new TrackInfo
            {
                Id = "yt_" + item.Id, // Добавляем префикс
                Title = item.Title,
                Author = item.Author ?? "Unknown",
                Duration = item.Duration ?? TimeSpan.Zero,
                ThumbnailUrl = item.Thumbnails.LastOrDefault()?.Url ?? "", // Берем самое большое качество
                IsMusic = true,
                IsLiked = true 
            }).ToList();
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks (LM): {ex.Message} ({ex.StatusCode})");
            // Если 403, значит, куки протухли или невалидны.
            // Очищаем куки, чтобы пользователь мог авторизоваться заново.
            if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // _auth.Logout();
                // Log.Warn("[Sync] Authentication failed (403 Forbidden). Cookies cleared.");
            }
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
            // Используем YouTube Music API (через InnerTube), так как он возвращает именно библиотеку
            // FEmusic_liked_playlists вернет все плейлисты в библиотеке пользователя
            var ytPlaylists = await _provider.GetClient().Music.GetLibraryPlaylistsAsync();

            // Конвертируем YoutubeExplode.Playlists.Playlist в Core.Models.Playlist
            // (или возвращаем как есть, если LibraryViewModel умеет работать с типами YE, 
            // но в коде ViewModel ожидается Core.Models.Playlist через адаптер.
            // Внимание: метод возвращает List<YoutubeExplode.Playlists.Playlist>, 
            // а интерфейс требует List<Core.Models.Playlist> или адаптации).

            // В исходном коде LibraryViewModel ожидалось, что метод вернет Core.Models.Playlist
            // Но мы не можем просто так вернуть YE типы.
            // LibraryViewModel делает: var ytPlaylists = await _youtube.GetUserPlaylistsByAuthAsync();
            // и потом Select -> PlaylistSearchResult.

            // Чтобы не ломать типы, сконвертируем здесь
            var result = new List<MyLiteMusicPlayer.Core.Models.Playlist>();

            foreach (var p in ytPlaylists)
            {
                // Ищем самое большое изображение
                var thumb = p.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault()?.Url;

                result.Add(new MyLiteMusicPlayer.Core.Models.Playlist
                {
                    Id = p.Id.Value,
                    YoutubeId = p.Id.Value,
                    StoredName = p.Title,
                    Author = p.Author?.ChannelTitle ?? "Me",
                    ThumbnailUrl = thumb,
                    SyncMode = PlaylistSyncMode.TwoWaySync
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to get user playlists: {ex.Message}");
            return [];
        }
    }
}