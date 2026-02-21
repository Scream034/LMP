using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Cipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils.Extensions;
using LMP.Core.Youtube.Videos.ClosedCaptions;

namespace LMP.Core.Youtube.Videos.Streams;

public class StreamClient(HttpClient http)
{
    private readonly StreamController _controller = new(http);
    private CipherManifest? _cipherManifest;

    private async ValueTask<CipherManifest> ResolveCipherManifestAsync(CancellationToken cancellationToken)
    {
        if (_cipherManifest is not null) return _cipherManifest;

        try
        {
            var playerSource = await _controller.GetPlayerSourceAsync(cancellationToken);
            _cipherManifest = playerSource.CipherManifest;

            if (_cipherManifest == null)
            {
                Log.Warn("[StreamClient] CipherManifest is null from player source");
            }
            else
            {
                Log.Info($"[StreamClient] CipherManifest loaded: {_cipherManifest.Operations.Count} operations, sts={_cipherManifest.SignatureTimestamp}");
                foreach (var op in _cipherManifest.Operations)
                {
                    Log.Debug($"[StreamClient]   - {op}");
                }
            }

            return _cipherManifest
                ?? throw new YoutubeExplodeException("Failed to extract cipher manifest.");
        }
        catch (Exception ex)
        {
            Log.Error($"[StreamClient] Failed to resolve cipher manifest: {ex.Message}");
            throw;
        }
    }

    private async IAsyncEnumerable<IStreamInfo> GetAudioStreamInfosAsync(
     IEnumerable<IStreamData> streamDatas,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var streamData in streamDatas)
        {
            var itag = streamData.Itag;
            if (itag is null) continue;

            var mimeType = streamData.MimeType;
            if (string.IsNullOrEmpty(mimeType) ||
                !mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var audioCodec = streamData.AudioCodec;
            if (string.IsNullOrWhiteSpace(audioCodec))
                continue;

            // ✅ Получаем URL (может быть null если только signatureCipher)
            var url = streamData.Url;

            // ✅ КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ: Расшифровываем подпись ЕСЛИ она есть
            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    Log.Warn($"[StreamClient] itag={itag} has signature but no base URL, skipping");
                    continue;
                }

                try
                {
                    var cipherManifest = await ResolveCipherManifestAsync(cancellationToken);
                    var decryptedSig = cipherManifest.Decipher(streamData.Signature);
                    var sigParam = streamData.SignatureParameter ?? "sig";

                    // ✅ ИСПРАВЛЕНИЕ: "Хирургически" добавляем подпись как строку,
                    // не перекодируя остальной URL, чтобы сохранить регистр кодировки.
                    url = $"{url}&{sigParam}={Uri.EscapeDataString(decryptedSig)}";

                    Log.Debug($"[StreamClient] itag={itag} signature decrypted, param={sigParam}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[StreamClient] itag={itag} cipher failed: {ex.Message}");
                    continue;
                }
            }

            // ✅ Проверяем URL после всех манипуляций
            if (string.IsNullOrWhiteSpace(url))
            {
                Log.Debug($"[StreamClient] itag={itag} has no URL (signatureCipher not resolved?)");
                continue;
            }

            var contentLength = streamData.ContentLength ?? 0;
            if (contentLength == 0)
                continue;

            var container = streamData.Container?.Pipe(static s => new Container(s));
            if (container is null)
                continue;

            var bitrate = streamData.Bitrate?.Pipe(static s => new Bitrate(s));
            if (bitrate is null)
                continue;

            Language? audioLanguage = null;
            if (!string.IsNullOrWhiteSpace(streamData.AudioLanguageCode))
            {
                audioLanguage = new Language(
                    streamData.AudioLanguageCode,
                    streamData.AudioLanguageName ?? ""
                );
            }

            yield return new AudioOnlyStreamInfo(
                url,
                container.Value,
                new FileSize(contentLength),
                bitrate.Value,
                audioCodec,
                audioLanguage,
                streamData.IsAudioLanguageDefault
            );
        }
    }

    /// <summary>
    /// Получает манифест потоков с автоматическим fallback на разные клиенты.
    /// </summary>
    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        // Используем multi-client fallback
        PlayerResponse playerResponse;
        string usedClient;

        try
        {
            (playerResponse, usedClient) = await _controller.GetPlayerResponseWithFallbackAsync(
                videoId,
                cancellationToken
            );
        }
        catch (VideoUnplayableException)
        {
            // Последняя попытка с cipher (для age-restricted)
            Log.Warn($"[StreamClient] [{videoId}] All clients failed, trying with cipher...");

            try
            {
                var cipherManifest = await ResolveCipherManifestAsync(cancellationToken);
                playerResponse = await _controller.GetPlayerResponseAsync(
                    videoId,
                    cipherManifest.SignatureTimestamp,
                    cancellationToken
                );
                usedClient = "TVHTML5+Cipher";

                LogPlayerResponse(videoId, playerResponse, usedClient);
            }
            catch (Exception ex)
            {
                Log.Error($"[StreamClient] [{videoId}] Final fallback also failed: {ex.Message}");
                throw new VideoUnplayableException($"Video {videoId} is not available through any client.");
            }
        }

        if (!playerResponse.IsPlayable)
        {
            throw new VideoUnplayableException(
                $"Video {videoId} is not playable: {playerResponse.PlayabilityError}"
            );
        }

        var streamCount = playerResponse.Streams.Count();

        if (streamCount == 0)
        {
            throw new VideoUnplayableException(
                $"No streams for {videoId} (client: {usedClient}): {playerResponse.PlayabilityError}"
            );
        }

        Log.Debug($"[StreamClient] [{videoId}] Found {streamCount} streams via {usedClient}");

        var streams = new List<IStreamInfo>(streamCount);

        await foreach (var stream in GetAudioStreamInfosAsync(playerResponse.Streams, cancellationToken))
        {
            streams.Add(stream);
        }

        if (streams.Count == 0)
        {
            Log.Warn($"[StreamClient] [{videoId}] Had {streamCount} streams but 0 audio-only!");

            // Логируем что пришло
            int idx = 0;
            foreach (var s in playerResponse.Streams.Take(5))
            {
                Log.Debug($"[StreamClient] [{videoId}] Stream[{idx}]: container={s.Container}, audio={s.AudioCodec}, video={s.VideoCodec}");
                idx++;
            }

            throw new VideoUnplayableException($"No audio streams available for {videoId}");
        }

        Log.Info($"[StreamClient] [{videoId}] Extracted {streams.Count} audio streams via {usedClient}");

        return new StreamManifest(streams);
    }

    private static void LogPlayerResponse(VideoId videoId, PlayerResponse response, string attempt)
    {
        var isPlayable = response.IsPlayable;
        var error = response.PlayabilityError ?? "(none)";
        var streamCount = response.Streams.Count();

        if (isPlayable)
        {
            Log.Debug($"[StreamClient] [{videoId}] {attempt}: OK, streams={streamCount}");
        }
        else
        {
            Log.Warn($"[StreamClient] [{videoId}] {attempt}: NOT PLAYABLE - {error}");
        }
    }

    public async ValueTask DownloadAsync(
        IStreamInfo streamInfo,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var destination = File.Create(filePath);
        using var input = new MediaStream(http, streamInfo);

        await input.InitializeAsync(cancellationToken);
        await input.CopyToAsync(destination, progress, cancellationToken);
    }
}