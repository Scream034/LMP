using System.Net;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
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
/// 
/// <para><b>ВАЖНО — Битрейт vs Ключ кэша:</b></para>
/// <para><see cref="BuildCacheKey"/> использует **нормализованный** битрейт (134→128)
/// для группировки близких битрейтов в один cache bucket.
/// НО все методы возвращают **реальный** битрейт (134) для корректного отображения
/// в UI и логах.</para>
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

    /// <summary>
    /// Обновляет конфигурацию стриминга.
    /// </summary>
    public static void SetStreamingConfig(StreamingConfig config)
    {
        _currentConfig = config ?? throw new ArgumentNullException(nameof(config));
        Log.Info($"[AudioSourceFactory] Config updated: " +
                 $"align={config.RequestAlignmentBytes / 1024}KB, " +
                 $"request={config.MinRequestSizeBytes / 1024}-{config.MaxRequestSizeBytes / 1024}KB, " +
                 $"targetBuffer={config.TargetBufferMs}ms, " +
                 $"concurrent={config.MaxConcurrentDownloads}");
    }

    /// <summary>
    /// Обновляет конфигурацию из профиля интернета.
    /// </summary>
    public static void ApplyInternetProfile(InternetProfile profile)
    {
        SetStreamingConfig(StreamingProfiles.GetConfig(profile));
    }

    /// <summary>
    /// Строит уникальный ключ кэша: trackId + формат + **нормализованный** битрейт.
    /// 
    /// <para><b>ЕДИНСТВЕННЫЙ ИСТОЧНИК ИСТИНЫ</b> для генерации cache key.
    /// Используется в <see cref="CreateAsync"/> и <see cref="AudioCacheManager"/>.</para>
    /// 
    /// <para><b>Почему нормализация:</b></para>
    /// <para>YouTube возвращает битрейты вроде 127, 134, 131 kbps для одного формата.
    /// Без нормализации один трек дублировался бы в кэше с ключами:
    /// <c>track_WebM_127</c>, <c>track_WebM_134</c>, <c>track_WebM_131</c>.
    /// Нормализация (см. <see cref="NormalizeBitrate"/>) группирует
    /// 127-134 → 128, экономя ~60% места в кэше.</para>
    /// </summary>
    /// <param name="trackId">ID трека.</param>
    /// <param name="format">Формат контейнера (WebM, Mp4, etc).</param>
    /// <param name="bitrate">Реальный битрейт в kbps (например, 134).</param>
    /// <returns>
    /// Нормализованный ключ кэша, например: <c>yt_o5hD5w2kE_I_WebM_128</c>
    /// (134 kbps нормализован → 128).
    /// </returns>
    public static string BuildCacheKey(string trackId, AudioFormat format, int bitrate)
    {
        // BuildCacheKey использует NormalizeBitrate для группировки.
        // НО возвращаемый битрейт из CreateAsync — РЕАЛЬНЫЙ (не нормализованный).
        int normalizedBitrate = NormalizeBitrate(bitrate);
        return $"{trackId}_{format}_{normalizedBitrate}";
    }

    /// <summary>
    /// Ищет полностью закэшированный трек любого формата/битрейта.
    /// </summary>
    /// <returns>
    /// Кортеж (Path, CacheEntry) где CacheEntry.Bitrate — **реальный** битрейт
    /// из метаданных кэша (например, 134), не нормализованный.
    /// </returns>
    public static (string Path, AudioCacheEntry Entry)? FindAnyCachedTrack(string trackId)
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
    public static async Task<IAudioSource> CreateAsync(
          string url,
          HttpClient httpClient,
          Func<CancellationToken, Task<string?>>? urlAcquirer = null,
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

        if (string.IsNullOrEmpty(url))
        {
            var cached = FindAnyCachedTrack(trackId);
            if (cached != null)
            {
                Log.Info($"[AudioSourceFactory] Using cached: {trackId} " +
                         $"({cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps)");
                return new LocalFileSource(
                    cached.Value.Path,
                    cached.Value.Entry.TotalSize,
                    trackId,
                    _globalCacheManager,
                    cached.Value.Entry.CacheKey);
            }

            int bootstrapBytes = Math.Min(
                config.InitialPrebufferBytes,
                int.MaxValue);

            var startupEntry = _globalCacheManager.FindBestStartupCache(trackId, bootstrapBytes);
            if (startupEntry != null)
            {
                Log.Info($"[AudioSourceFactory] Partial-cache bootstrap: {trackId} " +
                         $"({startupEntry.Format}/{startupEntry.Bitrate}kbps, " +
                         $"prefix={startupEntry.GetContiguousDownloadedBytesFrom(0) / 1024}KB)");

                return new CachingStreamSource(
                    startupEntry.CacheKey,
                    trackId,
                    url: string.Empty,
                    contentLength: startupEntry.TotalSize,
                    format: startupEntry.Format,
                    codec: startupEntry.Codec,
                    bitrate: startupEntry.Bitrate > 0 ? startupEntry.Bitrate : bitrateHint,
                    httpClient: httpClient,
                    cacheManager: _globalCacheManager,
                    config: config,
                    urlAcquirer: urlAcquirer,
                    urlRefresher: urlRefresher);
            }

            throw new ArgumentException("No URL provided and no cache available", nameof(url));
        }

        var format = await DetectFormatAsync(url, httpClient, ct).ConfigureAwait(false);
        if (format == AudioFormat.Unknown)
            throw new NotSupportedException($"Could not detect audio format for: {url}");

        var (contentLength, codec, detectedBitrate) =
            await GetStreamInfoAsync(url, format, httpClient, ct).ConfigureAwait(false);

        int finalBitrate = bitrateHint > 0 ? bitrateHint : detectedBitrate;
        string cacheKey = BuildCacheKey(trackId, format, finalBitrate);

        if (_globalCacheManager.IsFullyCached(cacheKey))
        {
            var cachePath = _globalCacheManager.GetCachePath(cacheKey);
            if (File.Exists(cachePath))
            {
                var exactEntry = _globalCacheManager.GetCacheInfo(cacheKey);
                Log.Info($"[AudioSourceFactory] Exact cache hit: {cacheKey}");
                return new LocalFileSource(
                    cachePath,
                    exactEntry?.TotalSize ?? 0,
                    trackId,
                    _globalCacheManager,
                    cacheKey);
            }
        }

        Log.Info($"[AudioSourceFactory] Streaming {format}/{codec}/{finalBitrate}kbps: {cacheKey}");

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
            config,
            urlAcquirer,
            urlRefresher);
    }

    /// <summary>
    /// Возвращает информацию о кэше для трека.
    /// </summary>
    /// <returns>
    /// <see cref="AudioCacheEntry"/> с **реальным** битрейтом (не нормализованным).
    /// </returns>
    public static AudioCacheEntry? GetCacheInfo(string trackId) =>
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
        catch (OperationCanceledException)
        {
            throw;
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
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
            // YouTube передаёт битрейт в битах/сек (например, 134000).
            // Конвертируем в kbps БЕЗ округления: 134000 / 1000 = 134 kbps.
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