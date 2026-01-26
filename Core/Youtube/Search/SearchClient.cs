using System.Runtime.CompilerServices;
using LMP.Core.Models;

using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Search;

public class SearchClient(HttpClient http)
{
    private readonly SearchController _controller = new(http);
    private const string FallbackChannelId = "UCBR8-6071WBgIc8o-99y5Lg";

    // Возвращаем Batch<ISearchResult>, где элементы - это TrackInfo или Playlist
    public async IAsyncEnumerable<Batch<ISearchResult>> GetResultBatchesAsync(
        string searchQuery,
        SearchFilter searchFilter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var encounteredIds = new HashSet<string>(StringComparer.Ordinal);
        var continuationToken = default(string?);
        bool isMusicContext = searchFilter == SearchFilter.Music;

        do
        {
            var searchResults = await _controller.GetSearchResponseAsync(searchQuery, searchFilter, continuationToken, cancellationToken);
            var batchItems = new List<ISearchResult>();

            // 1. VIDEOS -> TrackInfo
            if (searchFilter is SearchFilter.None or SearchFilter.Video or SearchFilter.Music)
            {
                foreach (var videoData in searchResults.Videos)
                {
                    var videoId = videoData.Id;
                    if (string.IsNullOrWhiteSpace(videoId) || !encounteredIds.Add(videoId)) continue;

                    var channelId = videoData.ChannelId;
                    if (string.IsNullOrWhiteSpace(channelId) || !channelId.StartsWith("UC"))
                        channelId = FallbackChannelId;

                    var bestThumb = videoData.Thumbnails
                         .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                         .TryGetWithHighestResolution()?.Url
                         ?? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

                    bool isMusic = videoData.IsMusicItem || isMusicContext;

                    batchItems.Add(new TrackInfo
                    {
                        Id = $"yt_{videoId}",
                        Title = videoData.Title ?? "",
                        Author = videoData.Author ?? "YouTube",
                        ChannelId = channelId,
                        Duration = videoData.Duration ?? TimeSpan.Zero,
                        ThumbnailUrl = bestThumb,
                        IsOfficialArtist = videoData.IsOfficialArtist,
                        IsMusic = isMusic,
                        Url = $"https://www.youtube.com/watch?v={videoId}"
                    });
                }
            }

            // 2. PLAYLISTS -> Playlist
            if (searchFilter is SearchFilter.None or SearchFilter.Playlist or SearchFilter.Music)
            {
                foreach (var playlistData in searchResults.Playlists)
                {
                    var playlistId = playlistData.Id;
                    if (string.IsNullOrWhiteSpace(playlistId) || !encounteredIds.Add(playlistId)) continue;

                    var bestThumb = playlistData.Thumbnails
                        .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                        .TryGetWithHighestResolution()?.Url;

                    batchItems.Add(new Playlist
                    {
                        Id = $"yt_{playlistId}", // Префикс для UI
                        YoutubeId = playlistId,
                        StoredName = playlistData.Title ?? "Unknown Playlist",
                        Author = playlistData.Author,
                        ThumbnailUrl = bestThumb,
                        SyncMode = PlaylistSyncMode.CloudPublic
                    });
                }
            }
            
            // Каналы (ChannelSearchResult) пока оставим за скобками или реализуем отдельно, 
            // так как LMP.Core.Models не имеет модели Channel. 
            // Если нужно, можно добавить класс ChannelInfo : ISearchResult

            if (batchItems.Count > 0)
                yield return Batch.Create(batchItems);

            continuationToken = searchResults.ContinuationToken;

        } while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    public IAsyncEnumerable<ISearchResult> GetResultsAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.None, ct).FlattenAsync();

    // Специализированный метод для получения только треков
    public IAsyncEnumerable<TrackInfo> GetVideosAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.Video, ct).FlattenAsync().OfTypeAsync<TrackInfo>();

    // Специализированный метод для плейлистов
    public IAsyncEnumerable<Playlist> GetPlaylistsAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.Playlist, ct).FlattenAsync().OfTypeAsync<Playlist>();
}