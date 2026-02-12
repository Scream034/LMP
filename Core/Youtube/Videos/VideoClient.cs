using LMP.Core.Models;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Youtube.Videos;

public class VideoClient(HttpClient http)
{
    private readonly VideoController _controller = new(http);
    public StreamClient Streams { get; } = new(http);

    public async ValueTask<TrackInfo> GetAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default
    )
    {
        var watchPage = await _controller.GetVideoWatchPageAsync(videoId, cancellationToken);
        var playerResponse = watchPage.PlayerResponse
                             ?? await _controller.GetPlayerResponseAsync(videoId, cancellationToken);

        var title = playerResponse.Title ?? "";
        var channelTitle = playerResponse.Author
                           ?? throw new YoutubeExplodeException("Failed to extract video author.");
        var channelId = playerResponse.ChannelId
                        ?? throw new YoutubeExplodeException("Failed to extract video channel ID.");

        // Получаем лучшее превью
        var thumb = playerResponse.Thumbnails
            .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
            .Concat(Thumbnail.GetDefaultSet(videoId))
            .TryGetWithHighestResolution()?.Url;

        // Создаем TrackInfo
        return new TrackInfo
        {
            Id = $"yt_{videoId.Value}",
            Title = title,
            Author = channelTitle,
            ChannelId = channelId,
            Duration = playerResponse.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb ?? "",
            Url = $"https://www.youtube.com/watch?v={videoId}",
            IsMusic = playerResponse.IsMusic,
            // Дополнительные метаданные можно расширить при необходимости
        };
    }

    /// <summary>
    /// Публичный метод для получения PlayerResponse (используется для HLS fallback).
    /// </summary>
    internal async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var (response, _) = await _controller.GetPlayerResponseWithFallbackAsync(videoId, cancellationToken);
        return response;
    }
}