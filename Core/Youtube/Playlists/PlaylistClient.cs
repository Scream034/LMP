using System.Runtime.CompilerServices;
using LMP.Core.Models;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Playlists;

public class PlaylistClient(HttpClient http)
{
    private readonly PlaylistController _controller = new(http);

    public async ValueTask<Playlist> GetAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await _controller.GetPlaylistResponseAsync(playlistId, cancellationToken);

        var title = response.Title ?? throw new YoutubeExplodeException("Failed to extract playlist title.");
        var channelTitle = response.Author;

        var bestThumb = response.Thumbnails
            .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
            .TryGetWithHighestResolution()?.Url;

        return new Playlist
        {
            Id = $"yt_{playlistId.Value}",
            YoutubeId = playlistId.Value,
            StoredName = title,
            Author = channelTitle,
            Description = response.Description,
            ThumbnailUrl = bestThumb,
            SyncMode = PlaylistSyncMode.CloudPublic
        };
    }

    public async IAsyncEnumerable<Batch<TrackInfo>> GetVideoBatchesAsync(
        PlaylistId playlistId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var encounteredIds = new HashSet<string>();
        string? continuationToken = null;
        string? visitorData = null;
        bool isFirstRequest = true;

        do
        {
            List<PlaylistVideoData> videos;

            if (isFirstRequest)
            {
                // Первый запрос через browse
                var browseResponse = await _controller.GetPlaylistBrowseResponseAsync(
                    playlistId,
                    cancellationToken
                );

                videos = [.. browseResponse.Videos];
                continuationToken = browseResponse.ContinuationToken;
                visitorData = browseResponse.VisitorData;
                isFirstRequest = false;

                Log.Debug($"[PlaylistClient] Initial browse: {videos.Count} videos, has continuation: {continuationToken != null}");
            }
            else if (!string.IsNullOrEmpty(continuationToken))
            {
                // Последующие запросы через continuation
                var contResponse = await _controller.GetPlaylistContinuationAsync(
                    continuationToken,
                    visitorData,
                    cancellationToken
                );

                videos = [.. contResponse.Videos];
                continuationToken = contResponse.ContinuationToken;
                visitorData ??= contResponse.VisitorData;

                Log.Debug($"[PlaylistClient] Continuation: {videos.Count} videos, has more: {continuationToken != null}");
            }
            else
            {
                break;
            }

            var batch = new List<TrackInfo>();

            foreach (var videoData in videos)
            {
                var videoId = videoData.Id;
                if (string.IsNullOrEmpty(videoId)) continue;

                if (!encounteredIds.Add(videoId)) continue;

                var title = videoData.Title ?? "";
                var author = videoData.Author ?? "Unknown";

                var bestThumb = videoData.Thumbnails
                    .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                    .TryGetWithHighestResolution()?.Url
                    ?? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

                bool isMusic = DetectIfMusic(title, author, videoData.Duration);

                var track = new TrackInfo
                {
                    Id = $"yt_{videoId}",
                    Title = title,
                    Author = author,
                    ChannelId = videoData.ChannelId,
                    Duration = videoData.Duration ?? TimeSpan.Zero,
                    ThumbnailUrl = bestThumb,
                    Url = $"https://www.youtube.com/watch?v={videoId}",
                    IsMusic = isMusic
                };

                batch.Add(track);
            }

            if (batch.Count > 0)
            {
                yield return Batch.Create(batch);
            }

            // Если нет continuation и batch пустой - выходим
            if (string.IsNullOrEmpty(continuationToken) && batch.Count == 0)
            {
                break;
            }

        } while (!string.IsNullOrEmpty(continuationToken));
    }

    /// <summary>
    /// Heuristic-based music detection since YouTube API doesn't provide this info.
    /// </summary>
    private static bool DetectIfMusic(string title, string author, TimeSpan? duration)
    {
        if (author.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ||
            author.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (duration.HasValue)
        {
            var mins = duration.Value.TotalMinutes;
            if (mins < 1 || mins > 15)
            {
                return ContainsMusicKeywords(title);
            }

            if (mins >= 2 && mins <= 6)
            {
                return true;
            }
        }

        if (ContainsMusicKeywords(title))
        {
            return true;
        }

        if (ContainsNonMusicKeywords(title))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsMusicKeywords(string title)
    {
        var keywords = new[]
        {
            "official video", "official music", "official audio", "music video",
            "lyrics", "lyric video", "(audio)", "[audio]", "ft.", "feat.",
            "official mv", "m/v", "visualizer", "acoustic", "remix", "cover",
            "live performance", "official lyric", "audio only"
        };

        return keywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsNonMusicKeywords(string title)
    {
        var keywords = new[]
        {
            "tutorial", "how to", "review", "unboxing", "gameplay", "walkthrough",
            "podcast", "interview", "news", "trailer", "teaser", "behind the scenes",
            "making of", "reaction", "compilation", "best of", "highlights",
            "episode", "ep.", "part ", "chapter", "lecture", "course"
        };

        return keywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public IAsyncEnumerable<TrackInfo> GetVideosAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    ) => GetVideoBatchesAsync(playlistId, cancellationToken).FlattenAsync();
}