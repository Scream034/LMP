using LMP.Core.Models;


namespace LMP.Core.Youtube.Music;

public class MusicClient(HttpClient http)
{
    private readonly MusicController _controller = new(http);

    public async Task<List<TrackInfo>> GetLikedTracksAsync(CancellationToken cancellationToken = default)
    {
        var allTracks = new List<TrackInfo>();
        var response = await _controller.GetBrowseAsync(browseId: "VLLM", cancellationToken: cancellationToken);

        ProcessShelves(response.Shelves, allTracks);

        var continuation = response.ContinuationToken;
        int page = 1;

        while (!string.IsNullOrEmpty(continuation))
        {
            if (cancellationToken.IsCancellationRequested) break;

            response = await _controller.GetBrowseAsync(continuation: continuation, cancellationToken: cancellationToken);
            var prevCount = allTracks.Count;
            ProcessShelves(response.Shelves, allTracks);

            if (allTracks.Count == prevCount) break; 

            continuation = response.ContinuationToken;
            page++;
            if (page > 50) break;
        }

        return allTracks;
    }

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
                    var bestThumb = item.Thumbnails
                        .OrderByDescending(t => t.Resolution.Area)
                        .FirstOrDefault()?.Url;

                    result.Add(new Playlist
                    {
                        Id = $"yt_{item.Id}",
                        YoutubeId = item.Id,
                        StoredName = item.Title,
                        Author = item.Author ?? "Unknown",
                        ThumbnailUrl = bestThumb,
                        SyncMode = PlaylistSyncMode.TwoWaySync
                    });
                }
            }
        }
        return result;
    }

    // Возвращаем List<MusicShelf>, которые потом обрабатывает провайдер
    public async Task<List<MusicShelf>> GetPersonalizedHomeAsync(CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetBrowseAsync(browseId: "FEmusic_home", cancellationToken: cancellationToken);
        return response.Shelves;
    }

    private void ProcessShelves(List<MusicShelf> shelves, List<TrackInfo> targetList)
    {
        foreach (var shelf in shelves)
        {
            foreach (var item in shelf.Items)
            {
                if (item.Type == "Song" && !string.IsNullOrEmpty(item.Id))
                {
                    var bestThumb = item.Thumbnails
                       .OrderByDescending(t => t.Resolution.Area)
                       .FirstOrDefault()?.Url
                       ?? $"https://i.ytimg.com/vi/{item.Id}/mqdefault.jpg";

                    targetList.Add(new TrackInfo
                    {
                        Id = $"yt_{item.Id}",
                        Title = item.Title,
                        Author = item.Author ?? item.Album ?? "Unknown",
                        Duration = item.Duration ?? TimeSpan.Zero,
                        ThumbnailUrl = bestThumb,
                        IsMusic = true,
                        IsLiked = true,
                        Url = $"https://www.youtube.com/watch?v={item.Id}"
                    });
                }
            }
        }
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
}