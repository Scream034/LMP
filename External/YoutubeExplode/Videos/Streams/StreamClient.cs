using System.Runtime.CompilerServices;
using YoutubeExplode.Bridge;
using YoutubeExplode.Bridge.Cipher;
using YoutubeExplode.Common;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;
using YoutubeExplode.Videos.ClosedCaptions;

namespace YoutubeExplode.Videos.Streams;

/// <summary>
/// Operations related to media streams of YouTube videos.
/// </summary>
public class StreamClient(HttpClient http)
{
    private readonly StreamController _controller = new(http);
    private CipherManifest? _cipherManifest;

    private async ValueTask<CipherManifest> ResolveCipherManifestAsync(
        CancellationToken cancellationToken
    )
    {
        if (_cipherManifest is not null)
            return _cipherManifest;

        var playerSource = await _controller.GetPlayerSourceAsync(cancellationToken);

        return _cipherManifest =
            playerSource.CipherManifest
            ?? throw new YoutubeExplodeException("Failed to extract the cipher manifest.");
    }

    // ИСПРАВЛЕНИЕ: Добавляем проверку content-length для надёжности
    private async IAsyncEnumerable<IStreamInfo> GetStreamInfosAsync(
        IEnumerable<IStreamData> streamDatas,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var streamData in streamDatas)
        {
            var itag = streamData.Itag;
            if (itag is null) continue;

            var url = streamData.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            // Handle cipher-protected streams
            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                var cipherManifest = await ResolveCipherManifestAsync(cancellationToken);
                url = UrlEx.SetQueryParameter(
                    url,
                    streamData.SignatureParameter ?? "sig",
                    cipherManifest.Decipher(streamData.Signature)
                );
            }

            // Используем content length из метаданных
            var contentLength = streamData.ContentLength ?? 0;

            // КРИТИЧНО: Если content-length = 0, это битый stream — пропускаем
            if (contentLength == 0)
            {
                Log.Info($"Skipping stream {itag} - no content length");
                continue;
            }

            var container = streamData.Container?.Pipe(s => new Container(s));
            if (container is null) continue;

            var bitrate = streamData.Bitrate?.Pipe(s => new Bitrate(s));
            if (bitrate is null) continue;

            var audioLanguage = !string.IsNullOrWhiteSpace(streamData.AudioLanguageCode)
                ? new Language(
                    streamData.AudioLanguageCode,
                    streamData.AudioLanguageName ?? streamData.AudioLanguageCode
                )
                : (Language?)null;

            // Muxed or video-only stream
            if (!string.IsNullOrWhiteSpace(streamData.VideoCodec))
            {
                var framerate = streamData.VideoFramerate ?? 24;

                var videoQuality = !string.IsNullOrWhiteSpace(streamData.VideoQualityLabel)
                    ? VideoQuality.FromLabel(streamData.VideoQualityLabel, framerate)
                    : VideoQuality.FromItag(itag.Value, framerate);

                var videoResolution =
                    streamData.VideoWidth is not null && streamData.VideoHeight is not null
                        ? new Resolution(streamData.VideoWidth.Value, streamData.VideoHeight.Value)
                        : videoQuality.GetDefaultVideoResolution();

                // Muxed
                if (!string.IsNullOrWhiteSpace(streamData.AudioCodec))
                {
                    yield return new MuxedStreamInfo(
                        url,
                        container.Value,
                        new FileSize(contentLength),
                        bitrate.Value,
                        streamData.AudioCodec,
                        audioLanguage,
                        streamData.IsAudioLanguageDefault,
                        streamData.VideoCodec,
                        videoQuality,
                        videoResolution
                    );
                }
                // Video-only
                else
                {
                    yield return new VideoOnlyStreamInfo(
                        url,
                        container.Value,
                        new FileSize(contentLength),
                        bitrate.Value,
                        streamData.VideoCodec,
                        videoQuality,
                        videoResolution
                    );
                }
            }
            // Audio-only
            else if (!string.IsNullOrWhiteSpace(streamData.AudioCodec))
            {
                yield return new AudioOnlyStreamInfo(
                    url,
                    container.Value,
                    new FileSize(contentLength),
                    bitrate.Value,
                    streamData.AudioCodec,
                    audioLanguage,
                    streamData.IsAudioLanguageDefault
                );
            }
        }
    }

    private async ValueTask<IReadOnlyList<IStreamInfo>> GetStreamInfosAsync(
        VideoId videoId,
        PlayerResponse playerResponse,
        CancellationToken cancellationToken = default
    )
    {
        Log.Info($"GetStreamInfosAsync for player response: {videoId}");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Video is pay-to-play
        if (!string.IsNullOrWhiteSpace(playerResponse.PreviewVideoId))
        {
            throw new VideoRequiresPurchaseException(
                $"Video '{videoId}' requires purchase and cannot be played.",
                playerResponse.PreviewVideoId
            );
        }

        // Video is unplayable
        if (!playerResponse.IsPlayable)
        {
            throw new VideoUnplayableException(
                $"Video '{videoId}' is unplayable. Reason: '{playerResponse.PlayabilityError}'."
            );
        }

        var streamInfos = new List<IStreamInfo>();

        Log.Info($"Extracting streams from player response... ({sw.ElapsedMilliseconds}ms)");
        await foreach (var stream in GetStreamInfosAsync(playerResponse.Streams, cancellationToken))
        {
            streamInfos.Add(stream);
        }
        Log.Info($"Got {streamInfos.Count} streams from player ({sw.ElapsedMilliseconds}ms)");

        // ОПТИМИЗАЦИЯ: Пропускаем DASH manifest для audio-only запросов
        // DASH обычно содержит те же audio streams, но требует дополнительный HTTP запрос
        // Раскомментируй если нужны ВСЕ форматы:
        /*
        if (!string.IsNullOrWhiteSpace(playerResponse.DashManifestUrl))
        {
            try
            {
                Log.Info($"Fetching DASH manifest... ({sw.ElapsedMilliseconds}ms)");
                var dashManifest = await _controller.GetDashManifestAsync(
                    playerResponse.DashManifestUrl,
                    cancellationToken
                );

                await foreach (var stream in GetStreamInfosFastAsync(dashManifest.Streams, cancellationToken))
                {
                    streamInfos.Add(stream);
                }
                Log.Info($"Got {streamInfos.Count} total streams after DASH ({sw.ElapsedMilliseconds}ms)");
            }
            catch (HttpRequestException) { }
        }
        */

        if (!streamInfos.Any())
        {
            throw new VideoUnplayableException(
                $"Video '{videoId}' does not contain any playable streams."
            );
        }

        Log.Info($"GetStreamInfosAsync DONE: {streamInfos.Count} streams in {sw.ElapsedMilliseconds}ms");
        return streamInfos;
    }

    private async ValueTask<IReadOnlyList<IStreamInfo>> GetStreamInfosAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"GetStreamInfosAsync START: {videoId}");

        try
        {
            Log.Info($"Getting player response... ({sw.ElapsedMilliseconds}ms)");
            var playerResponse = await _controller.GetPlayerResponseAsync(
                videoId,
                cancellationToken
            );
            Log.Info($"Player response received ({sw.ElapsedMilliseconds}ms)");

            return await GetStreamInfosAsync(videoId, playerResponse, cancellationToken);
        }
        catch (VideoUnplayableException ex)
            when (ex is not VideoUnavailableException)
        {
            Log.Info($"Retrying with cipher... ({sw.ElapsedMilliseconds}ms)");
            var cipherManifest = await ResolveCipherManifestAsync(cancellationToken);

            var playerResponse = await _controller.GetPlayerResponseAsync(
                videoId,
                cipherManifest.SignatureTimestamp,
                cancellationToken
            );

            return await GetStreamInfosAsync(videoId, playerResponse, cancellationToken);
        }
    }

    /// <summary>
    /// Gets the manifest that lists available streams for the specified video.
    /// </summary>
    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"GetManifestAsync START: {videoId}");

        // ОПТИМИЗАЦИЯ: Уменьшаем retries с 5 до 2
        for (var retriesRemaining = 2; ; retriesRemaining--)
        {
            try
            {
                var streams = await GetStreamInfosAsync(videoId, cancellationToken);
                Log.Info($"GetManifestAsync DONE in {sw.ElapsedMilliseconds}ms");
                return new StreamManifest(streams);
            }
            catch (Exception ex)
                when (ex is HttpRequestException or IOException && retriesRemaining > 0)
            {
                Log.Info($"Retry {retriesRemaining} after error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets the HTTP Live Stream (HLS) manifest URL for the specified video (if it is a livestream).
    /// </summary>
    public async ValueTask<string> GetHttpLiveStreamUrlAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default
    )
    {
        var playerResponse = await _controller.GetPlayerResponseAsync(videoId, cancellationToken);
        if (!playerResponse.IsPlayable)
        {
            throw new VideoUnplayableException(
                $"Video '{videoId}' is unplayable. Reason: '{playerResponse.PlayabilityError}'."
            );
        }

        if (string.IsNullOrWhiteSpace(playerResponse.HlsManifestUrl))
        {
            throw new YoutubeExplodeException(
                $"Failed to extract the HTTP Live Stream manifest URL. Video '{videoId}' is likely not a live stream."
            );
        }

        return playerResponse.HlsManifestUrl;
    }

    /// <summary>
    /// Gets the stream identified by the specified metadata.
    /// </summary>
    public async ValueTask<Stream> GetAsync(
        IStreamInfo streamInfo,
        CancellationToken cancellationToken = default
    )
    {
        var stream = new MediaStream(http, streamInfo);
        await stream.InitializeAsync(cancellationToken);
        return stream;
    }

    /// <summary>
    /// Copies the stream identified by the specified metadata to the specified stream.
    /// </summary>
    public async ValueTask CopyToAsync(
        IStreamInfo streamInfo,
        Stream destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        using var input = await GetAsync(streamInfo, cancellationToken);
        await input.CopyToAsync(destination, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads the stream identified by the specified metadata to the specified file.
    /// </summary>
    public async ValueTask DownloadAsync(
        IStreamInfo streamInfo,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        using var destination = File.Create(filePath);
        await CopyToAsync(streamInfo, destination, progress, cancellationToken);
    }
}