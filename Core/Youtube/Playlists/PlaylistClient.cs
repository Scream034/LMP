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
            Id = $"yt_{playlistId.Value}", // Локальный ID с префиксом
            YoutubeId = playlistId.Value,
            StoredName = title,
            Author = channelTitle,
            Description = response.Description,
            RemoteCount = response.Count,
            ThumbnailUrl = bestThumb,
            SyncMode = PlaylistSyncMode.CloudPublic // По умолчанию считаем публичным
        };
    }

    // Возвращает TrackInfo вместо PlaylistVideo
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

                // Для PlaylistNextResponse videoId это строка, преобразуем в структуру для проверки уникальности
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

                var track = new TrackInfo
                {
                    Id = $"yt_{videoId}",
                    Title = title,
                    Author = author,
                    ChannelId = videoData.ChannelId,
                    Duration = videoData.Duration ?? TimeSpan.Zero,
                    ThumbnailUrl = bestThumb,
                    Url = $"https://www.youtube.com/watch?v={videoId}",
                    // Контекст плейлиста можно сохранить, если нужно
                };

                batch.Add(track);
            }

            if (batch.Count == 0) break;

            yield return Batch.Create(batch);

            visitorData ??= response.VisitorData;

        } while (true);
    }

    public IAsyncEnumerable<TrackInfo> GetVideosAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    ) => GetVideoBatchesAsync(playlistId, cancellationToken).FlattenAsync();
}