using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Cipher;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;
using LMP.Core.Youtube.Videos.ClosedCaptions;

namespace LMP.Core.Youtube.Videos.Streams;

public class StreamClient
{
    private readonly StreamController _controller;
    private readonly HttpClient _http;
    private readonly INTokenDecryptor _nTokenDecryptor;
    private readonly ISigCipherDecryptor _sigCipherDecryptor;
    private CipherManifest? _cipherManifest;

    public StreamClient(HttpClient http, INTokenDecryptor nTokenDecryptor, ISigCipherDecryptor sigCipherDecryptor)
    {
        _http = http;
        _controller = new StreamController(http);
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
    }

    /// <summary>
    /// Резолвит старый CipherManifest — нужен только для SignatureTimestamp.
    /// Сама дешифровка sig идёт через ISigCipherDecryptor.
    /// </summary>
    private async ValueTask<CipherManifest> ResolveCipherManifestAsync(CancellationToken cancellationToken)
    {
        if (_cipherManifest is not null)
            return _cipherManifest;

        try
        {
            var playerSource = await _controller.GetPlayerSourceAsync(cancellationToken);
            _cipherManifest = playerSource.CipherManifest;

            if (_cipherManifest is null)
            {
                Log.Debug("[StreamClient] CipherManifest not available (expected with new YouTube format)");
                _cipherManifest = new CipherManifest("", []);
            }

            return _cipherManifest;
        }
        catch (Exception ex)
        {
            Log.Debug($"[StreamClient] CipherManifest resolution failed: {ex.Message}");
            _cipherManifest = new CipherManifest("", []);
            return _cipherManifest;
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
                continue;

            var audioCodec = streamData.AudioCodec;
            if (string.IsNullOrWhiteSpace(audioCodec)) continue;

            // URL уже извлечён из signatureCipher.url (если был) через StreamData.Url
            var url = streamData.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            // ════════════════════════════════════════════════════════
            // SIGNATURE DECRYPTION
            // StreamData.Signature = поле "s" из signatureCipher
            // StreamData.SignatureParameter = поле "sp" (обычно "sig")
            // ════════════════════════════════════════════════════════
            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                Log.Debug($"[StreamClient] itag={itag} needs signature decryption " +
                          $"(sig length={streamData.Signature.Length})");

                try
                {
                    var decryptedSig = await _sigCipherDecryptor.DecipherAsync(
                        streamData.Signature,
                        cancellationToken
                    );

                    var sigParam = streamData.SignatureParameter ?? "sig";
                    url = $"{url}&{sigParam}={Uri.EscapeDataString(decryptedSig)}";

                    Log.Debug($"[StreamClient] itag={itag} sig decrypted OK");
                }
                catch (Exception ex)
                {
                    Log.Error($"[StreamClient] itag={itag} sig decryption failed: {ex.Message}");
                    continue; // Пропускаем этот стрим — без подписи не работает
                }
            }

            // ════════════════════════════════════════════════════════
            // N-TOKEN DECRYPTION
            // ════════════════════════════════════════════════════════
            var nToken = UrlEx.TryGetQueryParameterValue(url, "n");
            if (!string.IsNullOrEmpty(nToken))
            {
                try
                {
                    var decryptedN = await _nTokenDecryptor.DecryptAsync(nToken, cancellationToken);
                    url = UrlEx.SetQueryParameter(url, "n", decryptedN);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[StreamClient] itag={itag} n-token failed: {ex.Message}");
                    // Продолжаем — без n-token будет throttling, но работать будет
                }
            }

            // ════════════════════════════════════════════════════════
            // Cleanup YouTube internal parameters
            // ════════════════════════════════════════════════════════
            url = UrlEx.RemoveQueryParameter(url, "ump");
            url = UrlEx.RemoveQueryParameter(url, "alr");
            url = UrlEx.RemoveQueryParameter(url, "srfvp");
            url = UrlEx.RemoveQueryParameter(url, "rbuf");
            url = UrlEx.RemoveQueryParameter(url, "pot");

            // ════════════════════════════════════════════════════════
            // Validation & yield
            // ════════════════════════════════════════════════════════
            var contentLength = streamData.ContentLength ?? 0;
            if (contentLength == 0) continue;

            var container = streamData.Container?.Pipe(static s => new Container(s));
            if (container is null) continue;

            var bitrate = streamData.Bitrate?.Pipe(static s => new Bitrate(s));
            if (bitrate is null) continue;

            Language? audioLanguage = null;
            if (!string.IsNullOrWhiteSpace(streamData.AudioLanguageCode))
            {
                audioLanguage = new Language(
                    streamData.AudioLanguageCode,
                    streamData.AudioLanguageName ?? ""
                );
            }

            yield return new AudioOnlyStreamInfo(
                itag.Value,
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

    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        PlayerResponse playerResponse;

        try
        {
            (playerResponse, _) = await _controller.GetPlayerResponseWithFallbackAsync(
                videoId, cancellationToken);
        }
        catch (VideoUnplayableException)
        {
            // Fallback: пробуем с signatureTimestamp
            var cipherManifest = await ResolveCipherManifestAsync(cancellationToken);
            playerResponse = await _controller.GetPlayerResponseAsync(
                videoId,
                cipherManifest.SignatureTimestamp,
                cancellationToken
            );
        }

        if (!playerResponse.IsPlayable)
            throw new VideoUnplayableException(
                $"Video {videoId} is not playable: {playerResponse.PlayabilityError}");

        var streams = new List<IStreamInfo>();
        await foreach (var stream in GetAudioStreamInfosAsync(
            playerResponse.Streams, cancellationToken))
        {
            streams.Add(stream);
        }

        if (streams.Count == 0)
            throw new VideoUnplayableException($"No audio streams available for {videoId}");

        return new StreamManifest(streams);
    }

    public async ValueTask DownloadAsync(
        IStreamInfo streamInfo,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var destination = File.Create(filePath);
        using var input = new MediaStream(_http, streamInfo);

        await input.InitializeAsync(cancellationToken);
        await input.CopyToAsync(destination, progress, cancellationToken);
    }
}