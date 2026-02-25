using LMP.Core.Models;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Youtube.Videos;

public class VideoClient(HttpClient http, NTokenDecryptor nTokenDecryptor, SigCipherDecryptor sigCipherDecryptor,
    Func<bool>? isAuthenticatedCheck = null)
{
    private readonly VideoController _controller = new(http);

    public StreamClient Streams { get; } = new(http, nTokenDecryptor, sigCipherDecryptor, isAuthenticatedCheck);

    public async ValueTask<TrackInfo> GetAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var watchPage = await _controller.GetVideoWatchPageAsync(videoId, cancellationToken);
        var playerResponse = watchPage.PlayerResponse
                             ?? await _controller.GetPlayerResponseAsync(videoId, cancellationToken);

        var title = playerResponse.Title ?? "";
        var channelTitle = playerResponse.Author
                           ?? throw new YoutubeExplodeException("Failed to extract video author.");
        var channelId = playerResponse.ChannelId
                        ?? throw new YoutubeExplodeException("Failed to extract video channel ID.");

        var thumb = playerResponse.Thumbnails
            .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
            .Concat(Thumbnail.GetDefaultSet(videoId))
            .TryGetWithHighestResolution()?.Url;

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
        };
    }

    internal async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        bool isAuth = isAuthenticatedCheck?.Invoke() ?? false;
        var (response, _) = await _controller.GetPlayerResponseWithFallbackAsync(
            videoId, cancellationToken, isAuthenticated: isAuth);
        return response;
    }
}