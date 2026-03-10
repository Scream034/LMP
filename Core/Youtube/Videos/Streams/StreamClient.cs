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

    /// <summary>
    /// Callback для проверки авторизации.
    /// </summary>
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

    /// <summary>
    /// Резолвит старый CipherManifest — нужен только для SignatureTimestamp.
    /// Сама дешифровка sig идёт через SigCipherDecryptor.
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

            var url = streamData.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            // ═══ DIAGNOSTIC: исходный URL из player response ═══
            Log.Debug($"[StreamClient] itag={itag} raw URL (first 200): " +
                      $"{url}");

            // ════════════════════════════════════════════════════════
            // SIGNATURE DECRYPTION
            // ════════════════════════════════════════════════════════
            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                Log.Debug($"[StreamClient] itag={itag} needs signature decryption " +
                          $"(sig length={streamData.Signature.Length}, " +
                          $"sigParam={streamData.SignatureParameter ?? "sig"})");

                try
                {
                    var decryptedSig = await _sigCipherDecryptor.DecipherAsync(
                        streamData.Signature,
                        cancellationToken
                    );

                    var sigParam = streamData.SignatureParameter ?? "sig";
                    url = UrlEx.SetQueryParameter(url, sigParam, decryptedSig);

                    Log.Debug($"[StreamClient] itag={itag} sig decrypted: " +
                              $"'{streamData.Signature}' → " +
                              $"'{decryptedSig}'");
                }
                catch (Exception ex)
                {
                    Log.Error($"[StreamClient] itag={itag} sig decryption failed: {ex.Message}");
                    continue;
                }
            }
            else
            {
                // ═══ DIAGNOSTIC: sig уже в URL, проверяем что он есть ═══
                var existingSig = UrlEx.TryGetQueryParameterValue(url, "sig");
                if (existingSig is not null)
                {
                    Log.Debug($"[StreamClient] itag={itag} sig already in URL " +
                              $"(length={existingSig.Length}, " +
                              $"ends={existingSig})");
                }
                else
                {
                    Log.Warn($"[StreamClient] itag={itag} NO sig in URL and no signatureCipher!");
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

                    // ═══ DIAGNOSTIC: n-token before/after ═══
                    Log.Debug($"[StreamClient] itag={itag} n-token: " +
                              $"'{nToken}' → '{decryptedN}'");
                }
                catch (Exception ex)
                {
                    // ═══ CRITICAL: n-token НЕ расшифрован → 403 ═══
                    Log.Warn($"[StreamClient] itag={itag} n-token failed: {ex.Message}");
                    Log.Warn($"[StreamClient] ⚠ URL will have ENCRYPTED n-token → expect 403!");
                }
            }

            // Cleanup
            url = UrlEx.RemoveQueryParameter(url, "ump");
            url = UrlEx.RemoveQueryParameter(url, "alr");
            url = UrlEx.RemoveQueryParameter(url, "srfvp");
            url = UrlEx.RemoveQueryParameter(url, "pot");

            // ═══ DIAGNOSTIC: финальный URL после всех трансформаций ═══
            Log.Debug($"[StreamClient] itag={itag} FINAL URL (first 400): " +
                      $"{url}");

            // ═══ DIAGNOSTIC: проверяем ключевые параметры ═══
            var finalN = UrlEx.TryGetQueryParameterValue(url, "n");
            var finalSig = UrlEx.TryGetQueryParameterValue(url, "sig");
            var finalC = UrlEx.TryGetQueryParameterValue(url, "c");
            Log.Debug($"[StreamClient] itag={itag} params: c={finalC}, " +
                      $"n={finalN}, " +
                      $"sig={finalSig}");

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

    #region GetManifestAsync — PRE-INIT decryptors

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

        // ═══ PRE-INIT: загружаем player JS ДО обработки стримов ═══
        // Без этого NTokenDecryptor пытается скачать base.js ОТДЕЛЬНО для каждого itag.
        // Результат из логов: 4 itag = 4 фейла HTTP/2 = 4x "[StreamClient] n-token failed"
        // С pre-init: одна загрузка base.js, потом все itag используют кэш.
        var preInitSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _nTokenDecryptor.EnsureInitializedAsync(cancellationToken);
            preInitSw.Stop();
            Log.Info($"[StreamClient] NToken pre-init OK in {preInitSw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            preInitSw.Stop();
            Log.Warn($"[StreamClient] NToken pre-init failed in {preInitSw.ElapsedMilliseconds}ms: " +
                     $"{ex.Message}");
            Log.Warn("[StreamClient] ⚠ N-tokens will NOT be decrypted → expect 403 on all chunks!");
        }

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

    /// <summary>
    /// Предзагрузка player JS. Один раз для всех itag.
    /// Если фейлит — логируем но не крашим (стримы попробуют без n-token).
    /// </summary>
    private async ValueTask PreInitializeDecryptorsAsync(CancellationToken ct)
    {
        try
        {
            // Это загрузит base.js и закэширует в StreamController
            // NTokenDecryptor тоже должен использовать этот кэш
            await _nTokenDecryptor.EnsureInitializedAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"[StreamClient] Decryptor pre-init failed (will retry per-itag): {ex.Message}");
        }
    }

    #endregion

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