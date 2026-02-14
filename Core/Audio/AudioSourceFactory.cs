// Core/Audio/AudioSourceFactory.cs

using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;

namespace LMP.Core.Audio;

/// <summary>
/// Форматы аудио контейнеров.
/// </summary>
public enum AudioFormat
{
    Unknown,
    WebM,
    Mp4,
    Hls,
    Ogg,
    Raw
}

/// <summary>
/// Фабрика источников аудио.
/// </summary>
public static class AudioSourceFactory
{
    /// <summary>
    /// Определяет формат по URL.
    /// </summary>
    public static AudioFormat DetectFormat(string url)
    {
        var lower = url.ToLowerInvariant();
        
        if (lower.Contains(".m3u8"))
            return AudioFormat.Hls;
        
        if (lower.Contains(".webm"))
            return AudioFormat.WebM;
        if (lower.Contains(".m4a") || lower.Contains(".mp4") || lower.Contains(".aac"))
            return AudioFormat.Mp4;
        if (lower.Contains(".ogg") || lower.Contains(".opus"))
            return AudioFormat.Ogg;
        
        if (lower.Contains("mime=audio%2fwebm") || lower.Contains("mime=audio/webm"))
            return AudioFormat.WebM;
        if (lower.Contains("mime=audio%2fmp4") || lower.Contains("mime=audio/mp4"))
            return AudioFormat.Mp4;
        
        if (TryParseItag(url, out int itag))
            return GetFormatByItag(itag);
        
        return AudioFormat.Unknown;
    }
    
    /// <summary>
    /// Определяет формат по Content-Type.
    /// </summary>
    public static AudioFormat DetectFormatByContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return AudioFormat.Unknown;
        
        var lower = contentType.ToLowerInvariant();
        
        if (lower.Contains("webm")) return AudioFormat.WebM;
        if (lower.Contains("mp4") || lower.Contains("m4a") || lower.Contains("aac")) return AudioFormat.Mp4;
        if (lower.Contains("ogg") || lower.Contains("opus")) return AudioFormat.Ogg;
        if (lower.Contains("mpegurl") || lower.Contains("m3u")) return AudioFormat.Hls;
        
        return AudioFormat.Unknown;
    }
    
    /// <summary>
    /// Определяет формат по magic bytes.
    /// </summary>
    public static AudioFormat DetectFormatByMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
            return AudioFormat.Unknown;
        
        // WebM/Matroska: 1A 45 DF A3
        if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
            return AudioFormat.WebM;
        
        // MP4/M4A: ....ftyp
        if (header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p')
            return AudioFormat.Mp4;
        
        // Ogg: OggS
        if (header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S')
            return AudioFormat.Ogg;
        
        // ADTS AAC: FF Fx
        if (header[0] == 0xFF && (header[1] & 0xF0) == 0xF0)
            return AudioFormat.Raw;
        
        return AudioFormat.Unknown;
    }
    
    /// <summary>
    /// Определяет кодек по формату.
    /// </summary>
    public static AudioCodec GetCodecForFormat(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.WebM => AudioCodec.Opus,
            AudioFormat.Mp4 => AudioCodec.Aac,
            AudioFormat.Hls => AudioCodec.Aac,
            AudioFormat.Ogg => AudioCodec.Opus,
            _ => AudioCodec.Unknown
        };
    }
    
    /// <summary>
    /// Создаёт источник для URL (без кэширования).
    /// </summary>
    public static async Task<IAudioSource> CreateAsync(
        string url,
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        string? trackId = null,
        CancellationToken ct = default)
    {
        var format = DetectFormat(url);
        
        if (format == AudioFormat.Unknown)
        {
            format = await DetectFormatByHeadAsync(url, httpClient, ct);
        }
        
        Log.Debug($"[AudioSourceFactory] Format: {format}");
        
        if (format == AudioFormat.Hls)
        {
            return new HlsStreamSource(url, httpClient, urlRefresher);
        }
        
        // Для не-HLS нужен размер контента
        long contentLength = await GetContentLengthAsync(url, httpClient, ct);
        
        if (contentLength <= 0)
        {
            contentLength = 50 * 1024 * 1024; // Assume max 50MB
            Log.Warn("[AudioSourceFactory] Unknown content length, assuming 50MB");
        }
        
        return new UniversalStreamSource(
            trackId ?? Guid.NewGuid().ToString(),
            url,
            contentLength,
            GetCodecForFormat(format),
            httpClient,
            urlRefresher);
    }
    
    /// <summary>
    /// Создаёт источник с кэшированием.
    /// </summary>
    public static async Task<IAudioSource> CreateWithCacheAsync(
        string url,
        string trackId,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        CancellationToken ct = default)
    {
        // Проверяем полный кэш
        if (cacheManager.IsFullyCached(trackId))
        {
            Log.Info($"[AudioSourceFactory] Using cached: {trackId}");
            var cachedPath = cacheManager.GetCachePath(trackId);
            return new LocalFileSource(cachedPath);
        }
        
        var format = DetectFormat(url);
        
        if (format == AudioFormat.Unknown)
        {
            format = await DetectFormatByHeadAsync(url, httpClient, ct);
        }
        
        if (format == AudioFormat.Hls)
        {
            // HLS пока без кэширования
            return new HlsStreamSource(url, httpClient, urlRefresher);
        }
        
        long contentLength = await GetContentLengthAsync(url, httpClient, ct);
        
        if (contentLength <= 0)
        {
            contentLength = 50 * 1024 * 1024;
        }
        
        return new CachingStreamSource(
            trackId,
            url,
            contentLength,
            format,
            httpClient,
            cacheManager,
            urlRefresher);
    }
    
    #region Private
    
    private static async Task<AudioFormat> DetectFormatByHeadAsync(
        string url, HttpClient httpClient, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            return DetectFormatByContentType(contentType);
        }
        catch
        {
            return AudioFormat.Unknown;
        }
    }
    
    private static async Task<long> GetContentLengthAsync(string url, HttpClient httpClient, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch
        {
            return -1;
        }
    }
    
    private static bool TryParseItag(string url, out int itag)
    {
        itag = 0;
        var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]itag=(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out itag);
    }
    
    private static AudioFormat GetFormatByItag(int itag)
    {
        return itag switch
        {
            249 or 250 or 251 => AudioFormat.WebM, // Opus
            139 or 140 or 141 or 256 or 258 or 325 or 328 => AudioFormat.Mp4, // AAC
            _ => AudioFormat.Unknown
        };
    }
    
    #endregion
}