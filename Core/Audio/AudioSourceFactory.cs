using System.Net;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Models;
using LMP.Core.Services;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Фабрика аудио источников. Определяет формат, проверяет кэш,
/// создаёт подходящий <see cref="IAudioSource"/>.
/// 
/// <para><b>Приоритет источников:</b></para>
/// <list type="number">
///   <item>Полный дисковый кэш → <see cref="LocalFileSource"/></item>
///   <item>Частичный кэш или онлайн → <see cref="CachingStreamSource"/></item>
/// </list>
/// </summary>
public static class AudioSourceFactory
{
    private static AudioCacheManager? _globalCacheManager;
    private static volatile StreamingConfig _currentConfig = StreamingProfiles.Medium;

    /// <summary>
    /// Инициализирует глобальный кэш-менеджер.
    /// </summary>
    public static void InitializeGlobalCache(AudioCacheManager cacheManager)
    {
        _globalCacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        Log.Info("[AudioSourceFactory] Global cache initialized");
    }

    /// <summary>Глобальный кэш-менеджер.</summary>
    public static AudioCacheManager? GlobalCache => _globalCacheManager;

    /// <summary>Текущая конфигурация стриминга.</summary>
    public static StreamingConfig CurrentConfig => _currentConfig;

    /// <summary>
    /// Обновляет конфигурацию стриминга.
    /// Влияет на все новые источники (существующие не затрагиваются).
    /// </summary>
    public static void SetStreamingConfig(StreamingConfig config)
    {
        _currentConfig = config ?? throw new ArgumentNullException(nameof(config));
        Log.Info($"[AudioSourceFactory] Config updated: " +
                 $"chunk={config.ChunkSizeBytes / 1024}KB, " +
                 $"concurrent={config.MaxConcurrentDownloads}, " +
                 $"readAhead={config.ReadAheadChunks}");
    }

    /// <summary>
    /// Обновляет конфигурацию из профиля интернета.
    /// </summary>
    public static void ApplyInternetProfile(InternetProfile profile)
    {
        SetStreamingConfig(StreamingProfiles.GetConfig(profile));
    }

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
    /// <param name="url">URL потока. Пустой = искать в кэше по trackId.</param>
    /// <param name="httpClient">HTTP клиент для загрузки.</param>
    /// <param name="urlRefresher">Callback обновления протухшего URL.</param>
    /// <param name="trackId">ID трека.</param>
    /// <param name="bitrateHint">Битрейт (kbps). 0 = определить автоматически.</param>
    /// <param name="config">Конфигурация стриминга. null = текущий глобальный профиль.</param>
    /// <param name="ct">Токен отмены.</param>
    public static async Task<IAudioSource> CreateAsync(
        string url,
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        string? trackId = null,
        int bitrateHint = 0,
        StreamingConfig? config = null,
        CancellationToken ct = default)
    {
        if (_globalCacheManager == null)
        {
            throw new InvalidOperationException(
                "AudioSourceFactory.InitializeGlobalCache() must be called before creating sources");
        }

        config ??= _currentConfig;
        trackId ??= GenerateTrackIdFromUrl(url);

        // ── Пустой URL → ищем кэш ──
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

        // ── Определяем формат ──
        var format = await DetectFormatAsync(url, httpClient, ct);
        if (format == AudioFormat.Unknown)
            throw new NotSupportedException($"Could not detect audio format for: {url}");

        // ── HLS (deprecated) ──
        if (format == AudioFormat.Hls)
        {
            Log.Warn("[AudioSourceFactory] HLS format detected — this is deprecated");
#pragma warning disable CS0618
            return new HlsStreamSource(url, httpClient, trackId);
#pragma warning restore CS0618
        }

        // ── Метаданные потока ──
        var (contentLength, codec, detectedBitrate) =
            await GetStreamInfoAsync(url, format, httpClient, ct);

        int finalBitrate = bitrateHint > 0 ? bitrateHint : detectedBitrate;
        string cacheKey = BuildCacheKey(trackId, format, finalBitrate);

        // ── Проверяем точный кэш ──
        if (_globalCacheManager.IsFullyCached(cacheKey))
        {
            var cachePath = _globalCacheManager.GetCachePath(cacheKey);
            if (File.Exists(cachePath))
            {
                Log.Info($"[AudioSourceFactory] Exact cache hit: {cacheKey}");
                return new LocalFileSource(cachePath);
            }
        }

        Log.Info($"[AudioSourceFactory] Streaming {format}/{codec}/{finalBitrate}kbps: {cacheKey}");

        return new CachingStreamSource(
            cacheKey, trackId, url, contentLength, format, codec,
            finalBitrate, httpClient, _globalCacheManager, config, urlRefresher);
    }

    /// <summary>
    /// Возвращает информацию о кэше для трека.
    /// </summary>
    public static CacheEntry? GetCacheInfo(string trackId) =>
        _globalCacheManager?.FindBestCache(trackId);

    #region Format Detection

    /// <summary>
    /// Определяет формат по URL (mime-параметры, расширение).
    /// </summary>
    public static AudioFormat DetectFormat(string url)
    {
        if (string.IsNullOrEmpty(url))
            return AudioFormat.Unknown;

        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/hls/", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Hls;

        if (url.Contains("mime=audio%2Fwebm", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;

        if (url.Contains("mime=audio%2Fmp4", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/mp4", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;

        if (url.Contains("mime=audio%2Fogg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/ogg", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;

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

    /// <summary>
    /// Определяет формат: сначала по URL, потом по magic bytes.
    /// </summary>
    public static async Task<AudioFormat> DetectFormatAsync(
        string url, HttpClient httpClient, CancellationToken ct = default)
    {
        var urlFormat = DetectFormat(url);
        if (urlFormat != AudioFormat.Unknown)
            return urlFormat;

        try
        {
            using var request = SharedHttpClient.CreateRangeRequest(url, 0, FormatDetectionHeaderSize - 1);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

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

    /// <summary>
    /// Определяет формат по magic bytes заголовка.
    /// </summary>
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

    /// <summary>
    /// Определяет кодек по формату контейнера.
    /// </summary>
    public static AudioCodec GetCodecForFormat(AudioFormat format) => format switch
    {
        AudioFormat.WebM => AudioCodec.Opus,
        AudioFormat.Ogg => AudioCodec.Opus,
        AudioFormat.Mp4 => AudioCodec.Aac,
        AudioFormat.Hls => AudioCodec.Aac,
        _ => AudioCodec.Unknown
    };

    #endregion

    #region Private Helpers

    private static string GenerateTrackIdFromUrl(string url)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        return "cache_" + Convert.ToHexString(hash)[..16];
    }

    private static async Task<(long ContentLength, AudioCodec Codec, int Bitrate)> GetStreamInfoAsync(
        string url, AudioFormat format, HttpClient httpClient, CancellationToken ct)
    {
        long contentLength = 0;

        if (url.Contains("googlevideo.com/videoplayback"))
        {
            contentLength = ExtractContentLengthFromUrl(url);
        }
        else
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    contentLength = response.Content.Headers.ContentLength ?? 0;
            }
            catch { /* Fallback below */ }
        }

        if (contentLength <= 0)
            contentLength = 100 * 1024 * 1024; // 100MB fallback

        var codec = GetCodecForFormat(format);
        int bitrate = ExtractBitrateFromUrl(url);

        if (bitrate == 0)
            bitrate = codec == AudioCodec.Opus ? 128 : 96;

        return (contentLength, codec, bitrate);
    }

    private static long ExtractContentLengthFromUrl(string url)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            return long.TryParse(query["clen"], out var clen) ? clen : 0;
        }
        catch { return 0; }
    }

    private static int ExtractBitrateFromUrl(string url)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            return int.TryParse(query["bitrate"], out var br) ? br / 1000 : 0;
        }
        catch { return 0; }
    }

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