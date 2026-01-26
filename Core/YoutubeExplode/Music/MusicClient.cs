using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace YoutubeExplode.Music;

public class MusicClient(HttpClient http)
{
    private readonly MusicController _controller = new(http);

    /// <summary>
    /// Получает треки из плейлиста "Понравившееся" (Liked Music).
    /// Использует browseId="VLLM" и пагинацию.
    /// </summary>
    public async Task<List<MusicItem>> GetLikedTracksAsync(CancellationToken cancellationToken = default)
    {
        var allItems = new List<MusicItem>();
        
        // 1. Первый запрос (VLLM)
        // ВНИМАНИЕ: Используем VLLM для доступа к виртуальному плейлисту лайков
        var response = await _controller.GetBrowseAsync(browseId: "VLLM", cancellationToken: cancellationToken);
        
        allItems.AddRange(response.Shelves.SelectMany(s => s.Items));
        
        var continuation = response.ContinuationToken;
        int page = 1;

        // 2. Загрузка следующих страниц
        while (!string.IsNullOrEmpty(continuation))
        {
            if (cancellationToken.IsCancellationRequested) break;
            
            // Запрос следующей страницы с токеном
            response = await _controller.GetBrowseAsync(continuation: continuation, cancellationToken: cancellationToken);
            
            var newItems = response.Shelves.SelectMany(s => s.Items).ToList();
            if (newItems.Count == 0) break;
            
            allItems.AddRange(newItems);
            continuation = response.ContinuationToken;
            page++;
            
            // Защита от бесконечного цикла (опционально)
            // if (page > 50) break; 
        }

        return allItems;
    }

    /// <summary>
    /// Получает библиотеку плейлистов пользователя.
    /// </summary>
    public async Task<List<Playlist>> GetLibraryPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetBrowseAsync(browseId: "FEmusic_liked_playlists", cancellationToken: cancellationToken);

        var result = new List<Playlist>();

        foreach (var shelf in response.Shelves)
        {
            foreach (var item in shelf.Items)
            {
                if (item.Type == "Playlist" && !string.IsNullOrEmpty(item.Id))
                {
                    var id = new PlaylistId(item.Id);
                    var thumbs = item.Thumbnails.Select(t => new Thumbnail(t.Url, t.Resolution)).ToArray();

                    var pl = new Playlist(
                        id,
                        item.Title,
                        new Author(new Channels.ChannelId("UC00000000000000000000000"), item.Author ?? "Unknown"),
                        "",
                        null,
                        thumbs
                    );

                    result.Add(pl);
                }
            }
        }

        return result;
    }

    public async Task<List<MusicShelf>> GetPersonalizedHomeAsync(CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetBrowseAsync(browseId: "FEmusic_home", cancellationToken: cancellationToken);
        return response.Shelves.ToList();
    }

    public async Task<List<MusicShelf>> GetArtistAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetBrowseAsync(browseId: channelId, cancellationToken: cancellationToken);
        return response.Shelves.ToList();
    }

    public async Task LikeTrackAsync(string videoId, bool like, CancellationToken cancellationToken = default)
    {
        var endpoint = like ? "like/like" : "like/removelike";
        await _controller.SendLikeActionAsync(endpoint, videoId, cancellationToken);
    }

    public async Task<string> CreatePlaylistAsync(string title, string description = "", List<string>? initialVideoIds = null, CancellationToken cancellationToken = default)
    {
        return await _controller.CreatePlaylistAsync(title, description, initialVideoIds, cancellationToken);
    }

    public async Task AddToPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default)
    {
        await _controller.EditPlaylistAsync(playlistId, videoId, "ACTION_ADD_VIDEO", cancellationToken);
    }

    public async Task RemoveFromPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotImplementedException("Removing via InnerTube requires setVideoId which is not yet parsed.");
    }
}