using System.Runtime.CompilerServices;
using LMP.Core.Models;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos;

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
            // RemoteCount = response.Count,
            ThumbnailUrl = bestThumb,
            SyncMode = PlaylistSyncMode.CloudPublic
        };
    }

    public async IAsyncEnumerable<Batch<TrackInfo>> GetVideoBatchesAsync(
        PlaylistId playlistId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var encounteredIds = new HashSet<VideoId>();
        var lastVideoId = default(VideoId?);
        var lastVideoIndex = 0;
        var visitorData = default(string?);

        do
        {
            var response = await _controller.GetPlaylistNextResponseAsync(
                playlistId,
                lastVideoId,
                lastVideoIndex,
                visitorData,
                cancellationToken
            );

            var batch = new List<TrackInfo>();

            foreach (var videoData in response.Videos)
            {
                var videoId = videoData.Id;
                if (videoId == null) continue;

                var vIdStruct = new VideoId(videoId);
                
                lastVideoId = vIdStruct;
                lastVideoIndex = videoData.Index ?? 0;

                if (!encounteredIds.Add(vIdStruct)) continue;

                var title = videoData.Title ?? "";
                var author = videoData.Author ?? "Unknown";
                
                var bestThumb = videoData.Thumbnails
                    .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
                    .TryGetWithHighestResolution()?.Url 
                    ?? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

                // IMPROVED: Better music detection heuristics
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

            if (batch.Count == 0) break;

            yield return Batch.Create(batch);

            visitorData ??= response.VisitorData;

        } while (true);
    }

    /// <summary>
    /// Heuristic-based music detection since YouTube API doesn't provide this info.
    /// </summary>
    private static bool DetectIfMusic(string title, string author, TimeSpan? duration)
    {
        // 1. Topic channels and VEVO are always music
        if (author.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ||
            author.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Duration heuristic: Most songs are 1:30 - 10:00
        if (duration.HasValue)
        {
            var mins = duration.Value.TotalMinutes;
            // Very short (< 1 min) or very long (> 15 min) are likely NOT music
            if (mins < 1 || mins > 15)
            {
                // But could still be music if title suggests it
                return ContainsMusicKeywords(title);
            }
            
            // Typical song length (2-6 min) - lean towards music
            if (mins >= 2 && mins <= 6)
            {
                return true;
            }
        }

        // 3. Title keyword analysis
        if (ContainsMusicKeywords(title))
        {
            return true;
        }

        // 4. Check for common non-music patterns
        if (ContainsNonMusicKeywords(title))
        {
            return false;
        }

        // 5. Default: Consider content from music playlists as potentially music
        // This gives benefit of the doubt for playlist context
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