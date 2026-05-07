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

public sealed class StreamClient
{
    private readonly StreamController _controller;
    private readonly HttpClient _http;
    private readonly NTokenDecryptor _nTokenDecryptor;
    private readonly SigCipherDecryptor _sigCipherDecryptor;
    private CipherManifest? _cipherManifest;
    private readonly Func<bool>? _isAuthenticatedCheck;

    public StreamClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null)
    {
        _http = http;
        _controller = new StreamController(http);
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
        _isAuthenticatedCheck = isAuthenticatedCheck;
    }

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
                _cipherManifest = new CipherManifest("",[]);
            }

            return _cipherManifest;
        }
        catch (Exception ex)
        {
            Log.Debug($"[StreamClient] CipherManifest resolution failed: {ex.Message}");
            _cipherManifest = new CipherManifest("",[]);
            return _cipherManifest;
        }
    }

    private async IAsyncEnumerable<IStreamInfo> GetAudioStreamInfosAsync(
         IEnumerable<IStreamData> streamDatas,[EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Локальный кеш необходимости расшифровки: проверяем HEAD-запросом только один раз
        bool? isNTokenDecryptionRequired = null;

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

            var url = streamData.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            Log.Debug($"[StreamClient] itag={itag} raw URL (first 200): " +
                      $"{url[..Math.Min(url.Length, 200)]}");

            // ════════════════════════════════════════════════════════
            // SIGNATURE DECRYPTION
            // ════════════════════════════════════════════════════════
            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                Log.Debug($"[StreamClient] itag={itag} needs signature decryption");

                try
                {
                    var decryptedSig = await _sigCipherDecryptor.DecipherAsync(
                        streamData.Signature,
                        cancellationToken
                    );

                    var sigParam = streamData.SignatureParameter ?? "sig";
                    url = UrlEx.SetQueryParameter(url, sigParam, decryptedSig);

                    Log.Debug($"[StreamClient] itag={itag} sig decrypted");
                }
                catch (Exception ex)
                {
                    Log.Error($"[StreamClient] itag={itag} sig decryption failed: {ex.Message}");
                    continue;
                }
            }

            // ════════════════════════════════════════════════════════
            // N-TOKEN DECRYPTION (С ПРОВЕРКОЙ HEAD)
            // ════════════════════════════════════════════════════════
            var nToken = UrlEx.TryGetQueryParameterValue(url, "n");
            if (!string.IsNullOrEmpty(nToken))
            {
                if (isNTokenDecryptionRequired == null)
                {
                    try
                    {
                        // Проверяем, работает ли URL "как есть" (например, клиент ANDROID_VR).
                        using var headResponse = await _http.HeadAsync(url, cancellationToken);
                        if (headResponse.IsSuccessStatusCode)
                        {
                            isNTokenDecryptionRequired = false;
                            Log.Debug($"[StreamClient] itag={itag} HEAD check OK, skipping n-token decryption globally");
                        }
                        else
                        {
                            isNTokenDecryptionRequired = true;
                            Log.Debug($"[StreamClient] itag={itag} HEAD check returned {headResponse.StatusCode}, needs decryption");
                        }
                    }
                    catch (Exception ex)
                    {
                        isNTokenDecryptionRequired = true;
                        Log.Debug($"[StreamClient] HEAD check failed: {ex.Message}, will try to decrypt");
                    }
                }

                if (isNTokenDecryptionRequired == true)
                {
                    try
                    {
                        var decryptedN = await _nTokenDecryptor.DecryptAsync(nToken, cancellationToken);
                        url = UrlEx.SetQueryParameter(url, "n", decryptedN);

                        Log.Debug($"[StreamClient] itag={itag} n-token: '{nToken}' → '{decryptedN}'");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[StreamClient] itag={itag} n-token failed: {ex.Message}");
                        Log.Warn($"[StreamClient] ⚠ URL will have ENCRYPTED n-token → expect 403!");
                    }
                }
            }

            // Cleanup
            url = UrlEx.RemoveQueryParameter(url, "ump");
            url = UrlEx.RemoveQueryParameter(url, "alr");
            url = UrlEx.RemoveQueryParameter(url, "srfvp");
            url = UrlEx.RemoveQueryParameter(url, "pot");

            Log.Debug($"[StreamClient] itag={itag} FINAL URL ready.");

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
        bool isAuth = _isAuthenticatedCheck?.Invoke() ?? false;

        try
        {
            (playerResponse, _) = await _controller.GetPlayerResponseWithFallbackAsync(
                videoId, cancellationToken, isAuthenticated: isAuth);
        }
        catch (VideoUnplayableException)
        {
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