using System.Runtime.CompilerServices;
using LMP.Core.Models;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Search;

public class SearchClient(HttpClient http)
{
    private readonly SearchController _controller = new(http);

    /// <summary>
    /// Возвращает результаты поиска батчами.
    /// </summary>
    public async IAsyncEnumerable<Batch<ISearchResult>> GetResultBatchesAsync(
        string searchQuery,
        SearchFilter searchFilter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var encounteredIds = new HashSet<string>(StringComparer.Ordinal);
        var continuationToken = default(string?);

        // Определяем контекст один раз
        bool isMusicContext = searchFilter is SearchFilter.Music 
            or SearchFilter.MusicSong 
            or SearchFilter.MusicVideo 
            or SearchFilter.MusicAlbum;

        do
        {
            var searchResults = await _controller.GetSearchResponseAsync(
                searchQuery, searchFilter, continuationToken, cancellationToken);

            var batchItems = new List<ISearchResult>();

            // Обрабатываем видео/треки
            if (ShouldProcessVideos(searchFilter))
            {
                foreach (var videoData in searchResults.Videos)
                {
                    if (videoData.IsShort) continue;

                    var videoId = videoData.Id;
                    if (string.IsNullOrWhiteSpace(videoId) || !encounteredIds.Add(videoId)) 
                        continue;

                    var bestThumb = videoData.Thumbnails
                        .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                        .TryGetWithHighestResolution()?.Url
                        ?? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

                    // УПРОЩЁННАЯ ЛОГИКА:
                    // Если ищем через YouTube Music API — это музыка
                    // Если ищем через обычный YouTube — это видео
                    // Без эмпирики!
                    bool isMusic = isMusicContext || videoData.IsMusicItem;

                    batchItems.Add(new TrackInfo
                    {
                        Id = $"yt_{videoId}",
                        Title = videoData.Title ?? "",
                        Author = videoData.Author ?? "Unknown",
                        ChannelId = videoData.ChannelId,
                        Duration = videoData.Duration ?? TimeSpan.Zero,
                        ThumbnailUrl = bestThumb,
                        IsOfficialArtist = videoData.IsOfficialArtist,
                        IsMusic = isMusic,
                        Url = isMusicContext 
                            ? $"https://music.youtube.com/watch?v={videoId}"
                            : $"https://www.youtube.com/watch?v={videoId}"
                    });
                }
            }

            // Обрабатываем плейлисты
            if (ShouldProcessPlaylists(searchFilter))
            {
                foreach (var playlistData in searchResults.Playlists)
                {
                    var playlistId = playlistData.Id;
                    if (string.IsNullOrWhiteSpace(playlistId) || !encounteredIds.Add(playlistId)) 
                        continue;

                    var bestThumb = playlistData.Thumbnails
                        .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                        .TryGetWithHighestResolution()?.Url;

                    batchItems.Add(new Playlist
                    {
                        Id = $"yt_pl_{playlistId}",
                        YoutubeId = playlistId,
                        StoredName = playlistData.Title ?? "Unknown Playlist",
                        Author = playlistData.Author,
                        ThumbnailUrl = bestThumb,
                        SyncMode = PlaylistSyncMode.CloudPublic
                    });
                }
            }

            if (batchItems.Count > 0)
                yield return Batch.Create(batchItems);

            continuationToken = searchResults.ContinuationToken;

        } while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    private static bool ShouldProcessVideos(SearchFilter filter) =>
        filter is SearchFilter.None 
            or SearchFilter.Video 
            or SearchFilter.Music 
            or SearchFilter.MusicSong 
            or SearchFilter.MusicVideo;

    private static bool ShouldProcessPlaylists(SearchFilter filter) =>
        filter is SearchFilter.None 
            or SearchFilter.Playlist 
            or SearchFilter.MusicPlaylist;

    // Удобные методы
    public IAsyncEnumerable<ISearchResult> GetResultsAsync(string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.None, ct).FlattenAsync();

    public IAsyncEnumerable<TrackInfo> GetVideosAsync(string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.Video, ct).FlattenAsync().OfTypeAsync<TrackInfo>();

    public IAsyncEnumerable<TrackInfo> GetMusicAsync(string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.MusicSong, ct).FlattenAsync().OfTypeAsync<TrackInfo>();

    public IAsyncEnumerable<Playlist> GetPlaylistsAsync(string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.Playlist, ct).FlattenAsync().OfTypeAsync<Playlist>();
}