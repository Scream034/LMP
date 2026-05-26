using System.Runtime.CompilerServices;
using LMP.Core.Models;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Helpers.Extensions;
using static LMP.Core.Youtube.Bridge.SearchResponse;

namespace LMP.Core.Youtube.Search;

public class SearchClient(HttpClient http)
{
    private readonly SearchController _controller = new(http);

    // Кэшированные UTF-8 имена для частых свойств
    private static readonly byte[] Utf8Thumbnails = "thumbnails"u8.ToArray();
    private static readonly byte[] Utf8Title = "title"u8.ToArray();

    public async IAsyncEnumerable<Batch<ISearchResult>> GetResultBatchesAsync(
        string searchQuery,
        SearchFilter searchFilter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var encounteredIds = new HashSet<string>(64, StringComparer.Ordinal);
        string? continuationToken = null;

        bool isMusicContext = searchFilter is SearchFilter.Music
            or SearchFilter.MusicSong
            or SearchFilter.MusicVideo
            or SearchFilter.MusicAlbum;

        bool processVideos = ShouldProcessVideos(searchFilter);
        bool processPlaylists = ShouldProcessPlaylists(searchFilter);

        do
        {
            var searchResults = await _controller.GetSearchResponseAsync(
                searchQuery, searchFilter, continuationToken, cancellationToken);

            int estimatedCount = (processVideos ? searchResults.Videos.Count : 0) +
                                 (processPlaylists ? searchResults.Playlists.Count : 0);
            var batchItems = new List<ISearchResult>(estimatedCount);

            if (processVideos)
            {
                ProcessVideos(searchResults.Videos, batchItems, encounteredIds, isMusicContext);
            }

            if (processPlaylists)
            {
                ProcessPlaylists(searchResults.Playlists, batchItems, encounteredIds);
            }

            if (batchItems.Count > 0)
                yield return Batch.Create(batchItems);

            continuationToken = searchResults.ContinuationToken;

        } while (!string.IsNullOrEmpty(continuationToken));
    }

    /// <summary>
    /// вынесли в отдельный метод для лучшего инлайнинга.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessVideos(
        IReadOnlyList<VideoData> videos,
        List<ISearchResult> batchItems,
        HashSet<string> encounteredIds,
        bool isMusicContext)
    {
        for (int i = 0; i < videos.Count; i++)
        {
            var videoData = videos[i];
            if (videoData.IsShort) continue;

            var videoId = videoData.Id;
            if (string.IsNullOrEmpty(videoId) || !encounteredIds.Add(videoId))
                continue;

            string thumbUrl = GetBestThumbnailUrl(videoData.Thumbnails, videoId);
            bool isMusic = isMusicContext || videoData.IsMusicItem;

            batchItems.Add(new TrackInfo
            {
                Id = videoId, // TrackInfo setter автоматически добавит префикс
                Title = videoData.Title ?? "",
                // Используем локализацию
                Author = videoData.Author ?? Services.LocalizationService.Instance["Track_UnknownAuthor"],
                ChannelId = videoData.ChannelId,
                Duration = videoData.Duration ?? TimeSpan.Zero,
                ThumbnailUrl = thumbUrl,
                IsOfficialArtist = videoData.IsOfficialArtist,
                IsMusic = isMusic,
                Url = isMusicContext
                    ? $"https://music.youtube.com/watch?v={videoId}"
                    : $"https://www.youtube.com/watch?v={videoId}"
            });
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessPlaylists(
        IReadOnlyList<PlaylistData> playlists,
        List<ISearchResult> batchItems,
        HashSet<string> encounteredIds)
    {
        for (int i = 0; i < playlists.Count; i++)
        {
            var playlistData = playlists[i];
            var playlistId = playlistData.Id;
            if (string.IsNullOrEmpty(playlistId) || !encounteredIds.Add(playlistId))
                continue;

            string? thumbUrl = GetBestThumbnailUrl(playlistData.Thumbnails);

            batchItems.Add(new Playlist
            {
                Id = $"yt_pl_{playlistId}",
                YoutubeId = playlistId,
                StoredName = playlistData.Title ?? "Unknown Playlist",
                Author = playlistData.Author,
                ThumbnailUrl = thumbUrl,
                SyncMode = PlaylistSyncMode.CloudPublic
            });
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetBestThumbnailUrl(IReadOnlyList<ThumbnailData> thumbnails, string? fallbackVideoId = null)
    {
        string? bestUrl = null;
        int bestArea = -1;

        for (int i = 0; i < thumbnails.Count; i++)
        {
            var t = thumbnails[i];
            if (t.Url == null) continue;

            int area = (t.Width ?? 0) * (t.Height ?? 0);
            if (area > bestArea)
            {
                bestArea = area;
                bestUrl = t.Url;
            }
        }

        return bestUrl
            ?? (fallbackVideoId != null
                ? $"https://i.ytimg.com/vi/{fallbackVideoId}/mqdefault.jpg"
                : "");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldProcessVideos(SearchFilter filter) =>
        filter is SearchFilter.None
            or SearchFilter.Video
            or SearchFilter.Music
            or SearchFilter.MusicSong
            or SearchFilter.MusicVideo;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldProcessPlaylists(SearchFilter filter) =>
        filter is SearchFilter.None
            or SearchFilter.Playlist
            or SearchFilter.MusicPlaylist;

    /// <summary>
    /// Возвращает результаты поиска через WEB_REMIX с фильтром MusicSong.
    /// Исключает нерелевантный контент (туториалы, геймплей) силами YTM.
    /// </summary>
    public IAsyncEnumerable<ISearchResult> GetResultsAsync(
        string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.MusicSong, ct).FlattenAsync();

    public IAsyncEnumerable<TrackInfo> GetVideosAsync(string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.Video, ct).FlattenAsync().OfTypeAsync<TrackInfo>();

    public IAsyncEnumerable<TrackInfo> GetMusicAsync(string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.MusicSong, ct).FlattenAsync().OfTypeAsync<TrackInfo>();

    public IAsyncEnumerable<Playlist> GetPlaylistsAsync(string query, CancellationToken ct = default) =>
        GetResultBatchesAsync(query, SearchFilter.Playlist, ct).FlattenAsync().OfTypeAsync<Playlist>();
}