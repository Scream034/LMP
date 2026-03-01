using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Models;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Music;

public class MusicClient(HttpClient http)
{
    private readonly MusicController _controller = new(http);

    public async Task<List<TrackInfo>> GetLikedTracksAsync(
        CancellationToken cancellationToken = default)
    {
        var allTracks = new List<TrackInfo>(100);
        var response = await _controller.GetBrowseAsync(
            browseId: "VLLM", cancellationToken: cancellationToken);

        ProcessShelves(response.Shelves, allTracks);

        var continuation = response.ContinuationToken;
        int page = 1;

        while (!string.IsNullOrEmpty(continuation))
        {
            if (cancellationToken.IsCancellationRequested) break;

            response = await _controller.GetBrowseAsync(
                continuation: continuation, cancellationToken: cancellationToken);
            var prevCount = allTracks.Count;
            ProcessShelves(response.Shelves, allTracks);

            if (allTracks.Count == prevCount) break;

            continuation = response.ContinuationToken;
            page++;
            if (page > 50) break;
        }

        return allTracks;
    }

    public async Task<List<Playlist>> GetLibraryPlaylistsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetBrowseAsync(
            browseId: "FEmusic_liked_playlists", cancellationToken: cancellationToken);
        var result = new List<Playlist>();

        foreach (var shelf in response.Shelves)
        {
            foreach (var item in shelf.Items)
            {
                if (item.Type == "Playlist" && !string.IsNullOrEmpty(item.Id))
                {
                    string? bestThumb = ThumbnailUtils.GetBestUrl(item.Thumbnails);

                    result.Add(new Playlist
                    {
                        Id = string.Concat("yt_", item.Id),
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

    public async Task<List<MusicShelf>> GetPersonalizedHomeAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetBrowseAsync(
            browseId: "FEmusic_home", cancellationToken: cancellationToken);
        return response.Shelves;
    }

    private static void ProcessShelves(List<MusicShelf> shelves, List<TrackInfo> targetList)
    {
        for (int s = 0; s < shelves.Count; s++)
        {
            var items = shelves[s].Items;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Type != "Song" || string.IsNullOrEmpty(item.Id))
                    continue;

                string bestThumb = ThumbnailUtils.GetBestUrl(item.Thumbnails)
                    ?? string.Concat("https://i.ytimg.com/vi/", item.Id, "/mqdefault.jpg");

                targetList.Add(new TrackInfo
                {
                    Id = string.Concat("yt_", item.Id),
                    Title = item.Title,
                    Author = item.Author ?? item.Album ?? "Unknown",
                    Duration = item.Duration ?? TimeSpan.Zero,
                    ThumbnailUrl = bestThumb,
                    IsMusic = true,
                    IsLiked = true,
                    Url = string.Concat("https://www.youtube.com/watch?v=", item.Id)
                });
            }
        }
    }

    public void SetVisitorData(string visitorData) =>
        _controller.VisitorData = visitorData;

    #region Track Actions

    public async Task LikeTrackAsync(
        string videoId, bool like, CancellationToken cancellationToken = default)
    {
        var endpoint = like ? "like/like" : "like/removelike";
        await _controller.SendLikeActionAsync(endpoint, videoId, cancellationToken);
    }

    #endregion

    #region Playlist CRUD

    public async Task<string> CreatePlaylistAsync(
        string title,
        string description = "",
        List<string>? initialVideoIds = null,
        CancellationToken cancellationToken = default)
    {
        return await _controller.CreatePlaylistAsync(
            title, description, initialVideoIds, cancellationToken);
    }

    /// <summary>
    /// Переименовывает плейлист в YouTube Music.
    /// </summary>
    public async Task RenamePlaylistAsync(
        string playlistId,
        string newTitle,
        CancellationToken cancellationToken = default)
    {
        await _controller.RenamePlaylistAsync(playlistId, newTitle, cancellationToken);
    }

    /// <summary>
    /// Обновляет описание плейлиста.
    /// </summary>
    public async Task SetPlaylistDescriptionAsync(
        string playlistId,
        string description,
        CancellationToken cancellationToken = default)
    {
        await _controller.SetPlaylistDescriptionAsync(
            playlistId, description, cancellationToken);
    }

    /// <summary>
    /// Удаляет плейлист из YouTube Music аккаунта.
    /// </summary>
    public async Task DeletePlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        await _controller.DeletePlaylistAsync(playlistId, cancellationToken);
    }

    /// <summary>
    /// Добавляет видео в плейлист. Возвращает setVideoId если доступен в ответе.
    /// </summary>
    public async Task<string?> AddToPlaylistAsync(
        string playlistId,
        string videoId,
        CancellationToken cancellationToken = default)
    {
        return await _controller.AddPlaylistItemAsync(
            playlistId, videoId, cancellationToken);
    }

    /// <summary>
    /// Removes a track from a YouTube Music playlist.
    /// Requires setVideoId — the unique playlist item identifier.
    /// </summary>
    public async Task RemoveFromPlaylistAsync(
        string playlistId,
        string videoId,
        string setVideoId,
        CancellationToken cancellationToken = default)
    {
        await _controller.RemovePlaylistItemAsync(
            playlistId, videoId, setVideoId, cancellationToken);
    }

    /// <summary>
    /// Fetches playlist content with setVideoId for each track.
    /// </summary>
    public async Task<List<PlaylistVideoData>> GetPlaylistVideosAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        return await _controller.GetPlaylistVideosAsync(playlistId, cancellationToken);
    }

    #endregion

    #region Account

    public async Task<JsonElement> GetAccountMenuAsync(
        CancellationToken cancellationToken = default) =>
        await _controller.GetAccountMenuAsync(cancellationToken);

    #endregion
}