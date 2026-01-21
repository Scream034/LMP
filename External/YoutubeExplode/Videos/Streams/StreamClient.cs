using System.Runtime.CompilerServices;
using YoutubeExplode.Bridge;
using YoutubeExplode.Bridge.Cipher;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;
using YoutubeExplode.Videos.ClosedCaptions;

namespace YoutubeExplode.Videos.Streams;

public class StreamClient(HttpClient http)
{
    private readonly StreamController _controller = new(http);
    private CipherManifest? _cipherManifest;

    private async ValueTask<CipherManifest> ResolveCipherManifestAsync(CancellationToken cancellationToken)
    {
        if (_cipherManifest is not null) return _cipherManifest;
        var playerSource = await _controller.GetPlayerSourceAsync(cancellationToken);
        return _cipherManifest = playerSource.CipherManifest
            ?? throw new YoutubeExplodeException("Failed to extract cipher manifest.");
    }

    private async IAsyncEnumerable<IStreamInfo> GetAudioStreamInfosAsync(
         IEnumerable<IStreamData> streamDatas,
         [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var streamData in streamDatas)
        {
            var itag = streamData.Itag;
            if (itag is null) continue;

            // ВАЖНО: Opus (WebM) часто идет как "adaptive" поток.
            // Пропускаем видео, но берем аудио.

            // 1. Если это явное видео (есть видео кодек) - пропускаем
            if (!string.IsNullOrWhiteSpace(streamData.VideoCodec)) continue;

            // 2. Если нет аудио кодека - пропускаем (битый поток)
            // Примечание: иногда audioCodec пустой, но MimeType содержит "audio/". 
            // YoutubeExplode обычно парсит это в AudioCodec. 
            if (string.IsNullOrWhiteSpace(streamData.AudioCodec)) continue;

            var url = streamData.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            // Расшифровка сигнатуры
            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                var cipherManifest = await ResolveCipherManifestAsync(cancellationToken);
                url = UrlEx.SetQueryParameter(
                    url,
                    streamData.SignatureParameter ?? "sig",
                    cipherManifest.Decipher(streamData.Signature)
                );
            }

            var contentLength = streamData.ContentLength ?? 0;
            // Opus потоки иногда не имеют ContentLength в заголовке до запроса, 
            // но в манифесте он обычно есть. Если 0 - это подозрительно, но можно попробовать пропустить проверку
            // если трек критически важен. Но обычно 0 = ошибка.
            if (contentLength == 0) continue;

            var container = streamData.Container?.Pipe(s => new Container(s));
            if (container is null) continue;

            var bitrate = streamData.Bitrate?.Pipe(s => new Bitrate(s));
            if (bitrate is null) continue;

            Language? audioLanguage = null;
            if (!string.IsNullOrWhiteSpace(streamData.AudioLanguageCode))
            {
                audioLanguage = new Language(streamData.AudioLanguageCode, streamData.AudioLanguageName ?? "");
            }

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

    // Основной метод получения манифеста
    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        // 1. Получаем PlayerResponse
        PlayerResponse playerResponse;
        try
        {
            playerResponse = await _controller.GetPlayerResponseAsync(videoId, cancellationToken);
        }
        catch (VideoUnplayableException)
        {
            // Fallback with cipher logic (simplified)
            var cipherManifest = await ResolveCipherManifestAsync(cancellationToken);
            playerResponse = await _controller.GetPlayerResponseAsync(videoId, cipherManifest.SignatureTimestamp, cancellationToken);
        }

        if (playerResponse.Streams.Count == 0)
            throw new VideoUnplayableException($"No streams for {videoId}");

        var streams = new List<IStreamInfo>();

        // 2. Извлекаем ТОЛЬКО аудио
        await foreach (var stream in GetAudioStreamInfosAsync(playerResponse.Streams, cancellationToken))
        {
            streams.Add(stream);
        }

        // DASH нам не нужен для аудио в 99% случаев, YouTube отдает opus/aac в adaptiveFormats

        return new StreamManifest(streams);
    }

    // Методы DownloadAsync и GetAsync оставляем как есть или делегируем
    public async ValueTask DownloadAsync(IStreamInfo streamInfo, string filePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var destination = File.Create(filePath);
        using var input = new MediaStream(http, streamInfo);
        await input.InitializeAsync(cancellationToken);
        await input.CopyToAsync(destination, progress, cancellationToken);
    }
}