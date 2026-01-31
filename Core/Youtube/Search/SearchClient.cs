using System.Runtime.CompilerServices;
using LMP.Core.Models;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Search;

public class SearchClient(HttpClient http)
{
    private readonly SearchController _controller = new(http);
    private const string FallbackChannelId = "UCBR8-6071WBgIc8o-99y5Lg";

    public async IAsyncEnumerable<Batch<ISearchResult>> GetResultBatchesAsync(
        string searchQuery,
        SearchFilter searchFilter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var encounteredIds = new HashSet<string>(StringComparer.Ordinal);
        var continuationToken = default(string?);

        bool isMusicContext = searchFilter == SearchFilter.Music ||
                              searchFilter == SearchFilter.MusicSong ||
                              searchFilter == SearchFilter.MusicVideo;

        do
        {
            var searchResults = await _controller.GetSearchResponseAsync(
                searchQuery, searchFilter, continuationToken, cancellationToken);
            
            var batchItems = new List<ISearchResult>();

            if (searchFilter is SearchFilter.None or SearchFilter.Video or 
                SearchFilter.Music or SearchFilter.MusicSong or SearchFilter.MusicVideo)
            {
                foreach (var videoData in searchResults.Videos)
                {
                    if (videoData.IsShort) continue;

                    var videoId = videoData.Id;
                    if (string.IsNullOrWhiteSpace(videoId) || !encounteredIds.Add(videoId)) continue;

                    var channelId = videoData.ChannelId;
                    if (string.IsNullOrWhiteSpace(channelId) || !channelId.StartsWith("UC"))
                        channelId = FallbackChannelId;

                    var author = videoData.Author ?? "YouTube";
                    var title = videoData.Title ?? "";
                    var duration = videoData.Duration;

                    var bestThumb = videoData.Thumbnails
                         .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                         .TryGetWithHighestResolution()?.Url
                         ?? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

                    // IMPROVED: Comprehensive music detection
                    bool isMusic = DetermineIfMusic(
                        isMusicContext,
                        videoData.IsMusicItem,
                        videoData.IsOfficialArtist,
                        title,
                        author,
                        duration
                    );

                    batchItems.Add(new TrackInfo
                    {
                        Id = $"yt_{videoId}",
                        Title = title,
                        Author = author,
                        ChannelId = channelId,
                        Duration = duration ?? TimeSpan.Zero,
                        ThumbnailUrl = bestThumb,
                        IsOfficialArtist = videoData.IsOfficialArtist,
                        IsMusic = isMusic,
                        Url = $"https://www.youtube.com/watch?v={videoId}"
                    });
                }
            }

            if (searchFilter is SearchFilter.None or SearchFilter.Playlist or SearchFilter.MusicPlaylist)
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
                        Id = $"yt_{playlistId}",
                        YoutubeId = playlistId,
                        StoredName = playlistData.Title ?? "Unknown Playlist",
                        Author = playlistData.Author,
                        ThumbnailUrl = bestThumb,
                        SyncMode = PlaylistSyncMode.CloudPublic
                    });
                }
            }

            if (batchItems.Count > 0) yield return Batch.Create(batchItems);
            continuationToken = searchResults.ContinuationToken;

        } while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    /// <summary>
    /// Comprehensive music detection combining multiple signals.
    /// </summary>
    private static bool DetermineIfMusic(
        bool isMusicContext,
        bool isMusicItem,
        bool isOfficialArtist,
        string title,
        string author,
        TimeSpan? duration)
    {
        // 1. Explicit music context from API
        if (isMusicContext || isMusicItem) return true;

        // 2. Official artist badge
        if (isOfficialArtist) return true;

        // 3. Topic channels and VEVO
        if (author.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ||
            author.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 4. Title contains music keywords
        if (ContainsMusicKeywords(title)) return true;

        // 5. Duration heuristic (typical song length)
        if (duration.HasValue)
        {
            var mins = duration.Value.TotalMinutes;
            // Typical song: 2-6 minutes
            if (mins >= 2 && mins <= 6 && !ContainsNonMusicKeywords(title))
            {
                return true;
            }
        }

        // 6. Check for non-music patterns
        if (ContainsNonMusicKeywords(title)) return false;

        // 7. Default for general search: likely not music
        return false;
    }

    private static bool ContainsMusicKeywords(string title)
    {
        ReadOnlySpan<string> keywords =
        [
            "official video", "official music", "official audio", "music video",
            "lyrics", "lyric video", "(audio)", "[audio]", "ft.", "feat.",
            "official mv", "m/v", "visualizer", "acoustic version", "remix",
            "cover", "live performance", "official lyric", "audio only",
            "slowed", "reverb", "nightcore", "extended", "instrumental"
        ];

        foreach (var keyword in keywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ContainsNonMusicKeywords(string title)
    {
        ReadOnlySpan<string> keywords =
        [
            "tutorial", "how to", "review", "unboxing", "gameplay", "walkthrough",
            "podcast", "interview", "news", "trailer", "teaser", "behind the scenes",
            "making of", "reaction", "compilation", "best of", "highlights",
            "episode", "part ", "chapter", "lecture", "course", "documentary"
        ];

        foreach (var keyword in keywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public IAsyncEnumerable<ISearchResult> GetResultsAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.None, ct).FlattenAsync();

    public IAsyncEnumerable<TrackInfo> GetVideosAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.Video, ct).FlattenAsync().OfTypeAsync<TrackInfo>();

    public IAsyncEnumerable<Playlist> GetPlaylistsAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.Playlist, ct).FlattenAsync().OfTypeAsync<Playlist>();
}