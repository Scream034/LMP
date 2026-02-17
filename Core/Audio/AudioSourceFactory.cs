using System.Net;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

public static class AudioSourceFactory
{
    private static AudioCacheManager? _globalCacheManager;

    public static void InitializeGlobalCache(AudioCacheManager cacheManager)
    {
        _globalCacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        Log.Info("[AudioSourceFactory] Global cache initialized");
    }

    public static AudioCacheManager? GlobalCache => _globalCacheManager;

    /// <summary>
    /// Строит уникальный ключ кэша: trackId + формат + нормализованный битрейт.
    /// </summary>
    public static string BuildCacheKey(string trackId, AudioFormat format, int bitrate)
    {
        int normalizedBitrate = NormalizeBitrate(bitrate);
        return $"{trackId}_{format}_{normalizedBitrate}";
    }

    /// <summary>
    /// Ищет полностью закэшированный трек любого формата/битрейта.
    /// </summary>
    public static (string Path, CacheEntry Entry)? FindAnyCachedTrack(string trackId)
    {
        if (_globalCacheManager == null) return null;

        var entry = _globalCacheManager.FindBestCache(trackId);
        if (entry == null) return null;

        var path = _globalCacheManager.GetCachePath(entry.CacheKey);
        if (!File.Exists(path)) return null;

        return (path, entry);
    }

    /// <summary>
    /// Создаёт аудио источник.
    /// </summary>
    /// <param name="url">URL потока.</param>
    /// <param name="httpClient">HTTP клиент.</param>
    /// <param name="urlRefresher">Функция обновления URL.</param>
    /// <param name="trackId">ID трека.</param>
    /// <param name="bitrateHint">
    /// Подсказка битрейта (kbps) от вызывающего кода.
    /// Используется для точного кэш-ключа вместо угадывания из URL.
    /// 0 = определять автоматически.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    public static async Task<IAudioSource> CreateAsync(
        string url,
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        string? trackId = null,
        int bitrateHint = 0,
        CancellationToken ct = default)
    {
        if (_globalCacheManager == null)
        {
            throw new InvalidOperationException(
                "AudioSourceFactory.InitializeGlobalCache() must be called before creating sources");
        }

        trackId ??= GenerateTrackIdFromUrl(url);

        // Пустой URL — ищем любой кэш для трека
        if (string.IsNullOrEmpty(url))
        {
            var cached = FindAnyCachedTrack(trackId);
            if (cached != null)
            {
                Log.Info($"[AudioSourceFactory] Using cached: {trackId} " +
                        $"({cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps)");
                return new LocalFileSource(cached.Value.Path);
            }
            throw new ArgumentException("No URL provided and no cache available", nameof(url));
        }

        // Определяем формат
        var format = await DetectFormatAsync(url, httpClient, ct);
        if (format == AudioFormat.Unknown)
            throw new NotSupportedException($"Could not detect audio format for: {url}");

        // HLS — не кэшируем
        if (format == AudioFormat.Hls)
        {
            Log.Info($"[AudioSourceFactory] HLS stream (RAM-only): {trackId}");
            return new HlsStreamSource(url, httpClient);
        }

        // Получаем метаданные потока
        var (contentLength, codec, detectedBitrate) = await GetStreamInfoAsync(url, format, httpClient, ct);

        // bitrateHint имеет приоритет над автодетектом
        int finalBitrate = bitrateHint > 0 ? bitrateHint : detectedBitrate;

        // Строим ключ кэша
        string cacheKey = BuildCacheKey(trackId, format, finalBitrate);

        Log.Debug($"[AudioSourceFactory] Bitrate resolution: hint={bitrateHint}, detected={detectedBitrate}, " +
                  $"final={finalBitrate}, normalized={NormalizeBitrate(finalBitrate)}, key={cacheKey}");

        // Проверяем точный кэш (формат + битрейт)
        if (_globalCacheManager.IsFullyCached(cacheKey))
        {
            var cachePath = _globalCacheManager.GetCachePath(cacheKey);
            if (File.Exists(cachePath))
            {
                Log.Info($"[AudioSourceFactory] Exact cache hit: {cacheKey}");
                return new LocalFileSource(cachePath);
            }
        }

        Log.Info($"[AudioSourceFactory] Caching {format}/{codec}/{finalBitrate}kbps: {cacheKey}");

        return new CachingStreamSource(
            cacheKey,
            trackId,
            url,
            contentLength,
            format,
            codec,
            finalBitrate,
            httpClient,
            _globalCacheManager,
            urlRefresher);
    }

    private static string GenerateTrackIdFromUrl(string url)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        return "cache_" + Convert.ToHexString(hash)[..16];
    }

    public static CacheEntry? GetCacheInfo(string trackId)
    {
        return _globalCacheManager?.FindBestCache(trackId);
    }

    #region Format Detection

    public static AudioFormat DetectFormat(string url)
    {
        if (string.IsNullOrEmpty(url))
            return AudioFormat.Unknown;

        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/hls/", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Hls;

        if (url.Contains(".webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;

        if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".m4a", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;

        if (url.Contains(".ogg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".opus", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;

        return AudioFormat.Unknown;
    }

    public static async Task<AudioFormat> DetectFormatAsync(
        string url, HttpClient httpClient, CancellationToken ct = default)
    {
        var urlFormat = DetectFormat(url);
        if (urlFormat != AudioFormat.Unknown)
            return urlFormat;

        try
        {
            using var request = SharedHttpClient.CreateRangeRequest(url, 0, FormatDetectionHeaderSize - 1);
            using var response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.Forbidden)
                return AudioFormat.Unknown;

            response.EnsureSuccessStatusCode();

            var header = await response.Content.ReadAsByteArrayAsync(ct);
            return DetectFormatByMagic(header);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioSourceFactory] Format detection failed: {ex.Message}");
            return AudioFormat.Unknown;
        }
    }

    public static AudioFormat DetectFormatByMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return AudioFormat.Unknown;

        if (header[..4].SequenceEqual(WebMMagic))
            return AudioFormat.WebM;

        if (header.Length >= 8 && header.Slice(4, 4).SequenceEqual(Mp4FtypMagic))
            return AudioFormat.Mp4;

        if (header[..4].SequenceEqual(OggMagic))
            return AudioFormat.Ogg;

        return AudioFormat.Unknown;
    }

    private static async Task<(long ContentLength, AudioCodec Codec, int Bitrate)> GetStreamInfoAsync(
        string url, AudioFormat format, HttpClient httpClient, CancellationToken ct)
    {
        long contentLength = 0;

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await httpClient.SendAsync(headRequest, ct);

            if (headResponse.IsSuccessStatusCode)
                contentLength = headResponse.Content.Headers.ContentLength ?? 0;
        }
        catch { }

        if (contentLength == 0)
        {
            try
            {
                using var rangeRequest = SharedHttpClient.CreateRangeRequest(url, 0, 0);
                using var rangeResponse = await httpClient.SendAsync(rangeRequest, ct);

                if (rangeResponse.Content.Headers.ContentRange?.Length != null)
                    contentLength = rangeResponse.Content.Headers.ContentRange.Length.Value;
            }
            catch
            {
                contentLength = 100 * 1024 * 1024;
            }
        }

        if (contentLength <= 0)
            contentLength = 100 * 1024 * 1024;

        var codec = GetCodecForFormat(format);
        int bitrate = ExtractBitrateFromUrl(url);
        if (bitrate == 0)
            bitrate = codec == AudioCodec.Opus ? 128 : 96;

        return (contentLength, codec, bitrate);
    }

    private static int ExtractBitrateFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            var bitrateStr = query["bitrate"];
            if (!string.IsNullOrEmpty(bitrateStr) && int.TryParse(bitrateStr, out var br))
                return br / 1000;
        }
        catch { }

        return 0;
    }

    public static AudioCodec GetCodecForFormat(AudioFormat format) => format switch
    {
        AudioFormat.WebM => AudioCodec.Opus,
        AudioFormat.Ogg => AudioCodec.Opus,
        AudioFormat.Mp4 => AudioCodec.Aac,
        AudioFormat.Hls => AudioCodec.Aac,
        _ => AudioCodec.Unknown
    };

    #endregion
}

public enum AudioFormat
{
    Unknown = 0,
    WebM = 1,
    Mp4 = 2,
    Ogg = 3,
    Hls = 4
}