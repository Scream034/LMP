using LMP.Core.Models;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Предоставляет методы для получения информации о видео YouTube.
/// </summary>
public sealed class VideoClient
{
    private readonly VideoController _controller;

    public StreamClient Streams { get; }

    public VideoClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null)
    {
        _controller = new VideoController(http, sigCipherDecryptor.PlayerManager);
        Streams = new StreamClient(http, nTokenDecryptor, sigCipherDecryptor, isAuthenticatedCheck);
    }

    /// <summary>
    /// Возвращает метаданные трека по его идентификатору.
    /// </summary>
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

        // Извлечение превью в один zero-alloc вызов без аллокации LINQ цепочек
        var thumb = YoutubeClientUtils.ThumbnailResolver.GetBestUrl(playerResponse.Thumbnails, videoId.Value);

        return new TrackInfo
        {
            Id = $"yt_{videoId.Value}",
            Title = title,
            Author = channelTitle,
            ChannelId = channelId,
            Duration = playerResponse.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb,
            Url = $"https://www.youtube.com/watch?v={videoId}",
            IsMusic = playerResponse.IsMusic,
        };
    }

    internal async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        bool isAuth = VideoController.IsInCooldown;
        var (response, _) = await _controller.GetPlayerResponseWithFallbackAsync(
            videoId, cancellationToken, isAuthenticated: isAuth);
        return response;
    }

    internal ValueTask<PlayerResponse> GetPlayerResponseWithClientAsync(
        VideoId videoId,
        string clientName,
        CancellationToken cancellationToken = default)
    {
        return _controller.GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);
    }
}