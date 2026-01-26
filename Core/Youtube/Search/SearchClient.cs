using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Common;
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

        bool isMusicContext = searchFilter == SearchFilter.Music;

        do
        {
            var searchResults = await _controller.GetSearchResponseAsync(searchQuery, searchFilter, continuationToken, cancellationToken);
            var batchItems = new List<ISearchResult>();

            // Videos
            if (searchFilter is SearchFilter.None or SearchFilter.Video or SearchFilter.Music)
            {
                foreach (var videoData in searchResults.Videos)
                {
                    var videoId = videoData.Id;
                    if (string.IsNullOrWhiteSpace(videoId) || !encounteredIds.Add(videoId)) continue;

                    var channelId = videoData.ChannelId;
                    if (string.IsNullOrWhiteSpace(channelId) || channelId.Length != 24 || !channelId.StartsWith("UC"))
                    {
                        channelId = FallbackChannelId;
                    }
                    
                    var author = new Author(channelId, videoData.Author ?? "YouTube");
                    var thumbnails = videoData.Thumbnails
                        .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                        .Concat(Thumbnail.GetDefaultSet(videoId))
                        .ToArray();

                    bool isMusic = videoData.IsMusicItem || isMusicContext;

                    // Создаем VideoSearchResult (который теперь TrackInfo)
                    batchItems.Add(new VideoSearchResult(
                        videoId, 
                        videoData.Title ?? "", 
                        author, 
                        videoData.Duration,
                        thumbnails, 
                        videoData.IsOfficialArtist, 
                        videoData.IsShort,
                        isMusic
                    ));
                }
            }

            // Playlists
            if (searchFilter is SearchFilter.None or SearchFilter.Playlist or SearchFilter.Music)
            {
                foreach (var playlistData in searchResults.Playlists)
                {
                    var playlistId = playlistData.Id;
                    if (string.IsNullOrWhiteSpace(playlistId) || !encounteredIds.Add(playlistId)) continue;
                    
                    Author? author = null;
                    var channelId = playlistData.ChannelId;
                    if (!string.IsNullOrWhiteSpace(channelId) && channelId.Length == 24 && channelId.StartsWith("UC"))
                    {
                        author = new Author(channelId, playlistData.Author ?? "YouTube");
                    }

                    var thumbnails = playlistData.Thumbnails
                        .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                        .ToArray();

                    // Создаем PlaylistSearchResult (который теперь Playlist)
                    batchItems.Add(new PlaylistSearchResult(
                        playlistId, 
                        playlistData.Title ?? "", 
                        author, 
                        thumbnails
                    ));
                }
            }
            
            // Channels
            if (searchFilter is SearchFilter.None or SearchFilter.Channel)
            {
                foreach (var channelData in searchResults.Channels)
                {
                    var channelId = channelData.Id;
                    if (string.IsNullOrWhiteSpace(channelId) || !encounteredIds.Add(channelId)) continue;
                    
                    var thumbnails = channelData.Thumbnails
                        .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                        .ToArray();
                        
                    batchItems.Add(new ChannelSearchResult(
                        channelId, 
                        channelData.Title ?? "", 
                        thumbnails
                    ));
                }
            }

            if (batchItems.Count > 0)
                yield return Batch.Create(batchItems);

            continuationToken = searchResults.ContinuationToken;

        } while (!string.IsNullOrWhiteSpace(continuationToken));
    }
    
    public IAsyncEnumerable<ISearchResult> GetResultsAsync(string searchQuery, CancellationToken ct = default) => 
        GetResultBatchesAsync(searchQuery, SearchFilter.None, ct).FlattenAsync();

    // Возвращает поток VideoSearchResult (совместим с TrackInfo)
    public IAsyncEnumerable<VideoSearchResult> GetVideosAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.Video, ct).FlattenAsync().OfTypeAsync<VideoSearchResult>();

    // Возвращает поток PlaylistSearchResult (совместим с Playlist)
    public IAsyncEnumerable<PlaylistSearchResult> GetPlaylistsAsync(string searchQuery, CancellationToken ct = default) =>
        GetResultBatchesAsync(searchQuery, SearchFilter.Playlist, ct).FlattenAsync().OfTypeAsync<PlaylistSearchResult>();
}