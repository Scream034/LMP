using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Videos.ClosedCaptions;
using LMP.Core.Helpers;

namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Обеспечивает высокопроизводительный доступ к медиа-потокам YouTube видео.
/// </summary>
public sealed class StreamClient
{
    private readonly VideoController _controller;
    private readonly HttpClient _http;
    private readonly NTokenDecryptor _nTokenDecryptor;
    private readonly SigCipherDecryptor _sigCipherDecryptor;
    private readonly PlayerContextManager _playerContextManager;
    private readonly Func<bool>? _isAuthenticatedCheck;
    private CipherManifest? _cipherManifest;

    /// <summary>
    /// Создает экземпляр StreamClient с внедрением необходимых зависимостей.
    /// </summary>
    /// <param name="http">Экземпляр HTTP-клиента.</param>
    /// <param name="nTokenDecryptor">Провайдер расшифровки N-Token.</param>
    /// <param name="sigCipherDecryptor">Провайдер расшифровки подписи.</param>
    /// <param name="isAuthenticatedCheck">Callback проверки авторизации.</param>
    public StreamClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null)
    {
        _http = http;
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
        _isAuthenticatedCheck = isAuthenticatedCheck;
        _playerContextManager = sigCipherDecryptor.PlayerManager;
        _controller = new VideoController(http, _playerContextManager);
    }

    /// <summary>
    /// Извлекает signatureTimestamp из единого кэша <see cref="PlayerContextManager"/>.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Вычисленный манифест шифрования плеера.</returns>
    private async ValueTask<CipherManifest> ResolveCipherManifestAsync(CancellationToken cancellationToken)
    {
        if (_cipherManifest is not null)
            return _cipherManifest;

        try
        {
            var context = await _playerContextManager.GetOrLoadAsync(cancellationToken).ConfigureAwait(false);

            // context.Sts — единственный надёжный источник STS
            // Идентичная причина что и в VideoController.ResolveSignatureTimestampAsync.
            var sts = context.Sts;

            if (string.IsNullOrEmpty(sts) && !string.IsNullOrEmpty(context.BaseJs))
                sts = YoutubeAstSolver.ExtractSts(context.BaseJs);

            _cipherManifest = new CipherManifest(sts ?? "");
            return _cipherManifest;
        }
        catch (Exception ex)
        {
            Log.Debug($"[StreamClient] CipherManifest resolution failed: {ex.Message}");
            _cipherManifest = new CipherManifest("");
            return _cipherManifest;
        }
    }

    /// <summary>
    /// Асинхронно генерирует последовательность доступных аудиопотоков для трека.
    /// </summary>
    /// <param name="videoId">ID видео на YouTube.</param>
    /// <param name="streamDatas">Коллекция сырых данных о потоках.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Асинхронный итератор элементов потоков.</returns>
    private async IAsyncEnumerable<IStreamInfo> GetAudioStreamInfosAsync(
      VideoId videoId,
      IEnumerable<IStreamData> streamDatas,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        bool? isNTokenDecryptionRequired = null;

        foreach (var streamData in streamDatas)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                Log.Debug($"[StreamClient] itag={itag} needs signature decryption");

                try
                {
                    var decryptedSig = await _sigCipherDecryptor.DecipherAsync(
                        streamData.Signature,
                        cancellationToken).ConfigureAwait(false);

                    var sigParam = streamData.SignatureParameter ?? "sig";
                    url = UrlEx.SetQueryParameter(url, sigParam, decryptedSig);

                    Log.Debug($"[StreamClient] itag={itag} sig decrypted");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error($"[StreamClient] itag={itag} sig decryption failed: {ex.Message}");
                    continue;
                }
            }

            var nToken = UrlEx.TryGetQueryParameterValue(url, "n");
            bool hasEncryptedNToken = false;

            if (!string.IsNullOrEmpty(nToken))
            {
                if (isNTokenDecryptionRequired == null)
                {
                    try
                    {
                        using var headResponse = await _http.HeadAsync(url, cancellationToken).ConfigureAwait(false);
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
                    catch (OperationCanceledException)
                    {
                        throw;
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
                        var decryptedN = await _nTokenDecryptor.DecryptAsync(
                            nToken,
                            contextId: videoId.Value,
                            ct: cancellationToken).ConfigureAwait(false);

                        hasEncryptedNToken = string.Equals(decryptedN, nToken, StringComparison.Ordinal);
                        url = UrlEx.SetQueryParameter(url, "n", decryptedN);

                        Log.Debug($"[StreamClient] itag={itag} n-token: '{nToken}' → '{decryptedN}'");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        hasEncryptedNToken = true;
                        Log.Warn($"[StreamClient] itag={itag} n-token failed: {ex.Message}");
                        Log.Warn($"[StreamClient] ⚠ URL will have ENCRYPTED n-token → expect 403!");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

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
                    streamData.AudioLanguageName ?? "");
            }

            yield return new AudioOnlyStreamInfo(
                itag.Value,
                url,
                container.Value,
                new FileSize(contentLength),
                bitrate.Value,
                audioCodec,
                audioLanguage,
                streamData.IsAudioLanguageDefault,
                hasEncryptedNToken,
                streamData.LoudnessDb);
        }
    }

    /// <summary>
    /// Асинхронно получает манифест доступных потоков для указанного видео-идентификатора.
    /// </summary>
    /// <param name="videoId">Уникальный ID видео.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат манифеста воспроизводимых аудио-потоков.</returns>
    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        PlayerResponse playerResponse;
        bool isAuth = _isAuthenticatedCheck?.Invoke() ?? false;

        try
        {
            (playerResponse, _) = await _controller.GetPlayerResponseWithFallbackAsync(
                videoId, cancellationToken, isAuthenticated: isAuth).ConfigureAwait(false);
        }
        catch (VideoUnplayableException)
        {
            var cipherManifest = await ResolveCipherManifestAsync(cancellationToken).ConfigureAwait(false);
            playerResponse = await _controller.GetPlayerResponseAsync(
                videoId,
                cipherManifest.SignatureTimestamp,
                cancellationToken
            ).ConfigureAwait(false);
        }

        if (!playerResponse.IsPlayable)
            throw new VideoUnplayableException(
                $"Video {videoId} is not playable: {playerResponse.PlayabilityError}");

        var streams = new List<IStreamInfo>();
        await foreach (var stream in GetAudioStreamInfosAsync(videoId, playerResponse.Streams, cancellationToken).ConfigureAwait(false))
        {
            streams.Add(stream);
        }

        if (streams.Count == 0)
            throw new VideoUnplayableException($"No audio streams available for {videoId}");

        return new StreamManifest(streams);
    }

    /// <summary>
    /// Выполняет непосредственную загрузку аудио-потока в указанный локальный путь на диске.
    /// </summary>
    /// <param name="streamInfo">Метаданные потока.</param>
    /// <param name="filePath">Выходной путь записи файла.</param>
    /// <param name="progress">Callback отображения прогресса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Асинхронная задача.</returns>
    public async ValueTask DownloadAsync(
        IStreamInfo streamInfo,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var destination = File.Create(filePath);
        using var input = new MediaStream(_http, streamInfo);

        await input.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await input.CopyToAsync(destination, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Инвалидирует закэшированный CipherManifest и signatureTimestamp контроллера.
    /// </summary>
    /// <remarks>
    /// Единая точка сброса всех stale STS-данных при 403-recovery.
    /// Вызывается из <see cref="YoutubeProvider.RefreshStreamUrlAsync"/> при <c>forceRefresh=true</c>.
    /// Без этого вызова <see cref="ResolveCipherManifestAsync"/> и
    /// <see cref="VideoController.ResolveSignatureTimestampAsync"/> вернут stale пустой STS,
    /// приводя к бесконечному 403-циклу.
    /// </remarks>
    public void InvalidateCipherManifest()
    {
        _cipherManifest = null;
        _controller.InvalidateSignatureTimestamp();
        Log.Debug("[StreamClient] CipherManifest and STS invalidated");
    }
}