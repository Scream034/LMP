// Services/PipedProvider.cs
using System.Diagnostics;
using System.Text.Json;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Быстрый поиск через Piped API (YouTube-прокси)
/// Средняя скорость: 200-400ms vs 2-3sec yt-dlp
/// </summary>
public class PipedProvider
{
    private readonly HttpClient _http;
    private readonly List<PipedInstance> _instances;
    private readonly SemaphoreSlim _instanceLock = new(1, 1);
    private int _currentIndex = 0;

    // Статистика
    public int TotalRequests { get; private set; }
    public int SuccessfulRequests { get; private set; }
    public double AverageResponseMs { get; private set; }

    public PipedProvider()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "LiteMusicPlayer/1.0");

        // Актуальные инстансы (обновляй на https://piped-instances.kavin.rocks/)
        _instances =
        [
            new("https://pipedapi.kavin.rocks"),
            new("https://pipedapi.adminforge.de"),
            new("https://api.piped.yt"),
            new("https://pipedapi.in.projectsegfau.lt"),
            new("https://pipedapi.leptons.xyz"),
            new("https://piped-api.garuber.eu"),
            new("https://pipedapi.drgns.space")
        ];
    }

    /// <summary>
    /// Поиск треков (быстро, без stream URL)
    /// </summary>
    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 50, CancellationToken ct = default)
    {
        TotalRequests++;
        var sw = Stopwatch.StartNew();

        for (int attempt = 0; attempt < _instances.Count; attempt++)
        {
            var instance = await GetBestInstanceAsync();
            
            try
            {
                // Piped search endpoint
                var url = $"{instance.BaseUrl}/search?q={Uri.EscapeDataString(query)}&filter=music_songs";
                
                Debug.WriteLine($"[Piped] Searching '{query}' on {instance.BaseUrl}...");

                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var results = ParseSearchResults(json, maxResults);

                sw.Stop();
                instance.RecordSuccess(sw.ElapsedMilliseconds);
                SuccessfulRequests++;
                UpdateAverageResponseTime(sw.ElapsedMilliseconds);

                Debug.WriteLine($"[Piped] Found {results.Count} tracks in {sw.ElapsedMilliseconds}ms");
                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"[Piped] {instance.BaseUrl} failed: {ex.Message}");
                instance.RecordFailure();
                await RotateInstanceAsync();
            }
        }

        Debug.WriteLine($"[Piped] All instances failed!");
        return [];
    }

    /// <summary>
    /// Получить trending/популярное
    /// </summary>
    public async Task<List<TrackInfo>> GetTrendingAsync(string region = "US", int maxResults = 50, CancellationToken ct = default)
    {
        var instance = await GetBestInstanceAsync();
        
        try
        {
            var url = $"{instance.BaseUrl}/trending?region={region}";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseTrendingResults(json, maxResults);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Piped] Trending failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Получить stream URL для видео (через Piped — без yt-dlp!)
    /// </summary>
    public async Task<string?> GetStreamUrlAsync(string videoId, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var instance = await GetBestInstanceAsync();
            
            try
            {
                var url = $"{instance.BaseUrl}/streams/{videoId}";
                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return ParseStreamUrl(json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"[Piped] GetStream failed: {ex.Message}");
                await RotateInstanceAsync();
            }
        }
        return null;
    }

    private List<TrackInfo> ParseSearchResults(string json, int maxResults)
    {
        var results = new List<TrackInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items))
                return results;

            foreach (var item in items.EnumerateArray().Take(maxResults))
            {
                // Только видео
                if (item.TryGetProperty("type", out var type) && type.GetString() != "stream")
                    continue;

                var videoUrl = item.TryGetProperty("url", out var urlProp) 
                    ? urlProp.GetString() ?? "" 
                    : "";
                
                // Извлекаем videoId из /watch?v=XXXXX
                var videoId = ExtractVideoId(videoUrl);
                if (string.IsNullOrEmpty(videoId)) continue;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "Unknown" : "Unknown";
                var uploader = item.TryGetProperty("uploaderName", out var u) ? u.GetString() ?? "Unknown" : "Unknown";
                var duration = item.TryGetProperty("duration", out var d) ? d.GetInt32() : 0;
                var thumbnail = item.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "";

                // Fallback thumbnail
                if (string.IsNullOrEmpty(thumbnail))
                {
                    thumbnail = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                }

                results.Add(new TrackInfo
                {
                    Id = $"yt_{videoId}",
                    Title = title,
                    Author = uploader,
                    Url = $"https://youtube.com/watch?v={videoId}",
                    Duration = TimeSpan.FromSeconds(duration),
                    ThumbnailUrl = thumbnail,
                    StreamUrl = "" // Будет получен при воспроизведении
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Piped] Parse error: {ex.Message}");
        }

        return results;
    }

    private List<TrackInfo> ParseTrendingResults(string json, int maxResults)
    {
        var results = new List<TrackInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            
            foreach (var item in doc.RootElement.EnumerateArray().Take(maxResults))
            {
                var videoUrl = item.TryGetProperty("url", out var urlProp) 
                    ? urlProp.GetString() ?? "" 
                    : "";
                
                var videoId = ExtractVideoId(videoUrl);
                if (string.IsNullOrEmpty(videoId)) continue;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "Unknown" : "Unknown";
                var uploader = item.TryGetProperty("uploaderName", out var u) ? u.GetString() ?? "Unknown" : "Unknown";
                var duration = item.TryGetProperty("duration", out var d) ? d.GetInt32() : 0;
                var thumbnail = item.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(thumbnail))
                    thumbnail = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

                results.Add(new TrackInfo
                {
                    Id = $"yt_{videoId}",
                    Title = title,
                    Author = uploader,
                    Url = $"https://youtube.com/watch?v={videoId}",
                    Duration = TimeSpan.FromSeconds(duration),
                    ThumbnailUrl = thumbnail
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Piped] ParseTrending error: {ex.Message}");
        }

        return results;
    }

    private string? ParseStreamUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Ищем audio streams
            if (root.TryGetProperty("audioStreams", out var audioStreams))
            {
                // Сортируем по bitrate, берём лучший
                var bestAudio = audioStreams.EnumerateArray()
                    .Where(s => s.TryGetProperty("url", out _))
                    .OrderByDescending(s => s.TryGetProperty("bitrate", out var br) ? br.GetInt32() : 0)
                    .FirstOrDefault();

                if (bestAudio.ValueKind != JsonValueKind.Undefined)
                {
                    return bestAudio.GetProperty("url").GetString();
                }
            }

            // Fallback: HLS stream
            if (root.TryGetProperty("hls", out var hls))
            {
                return hls.GetString();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Piped] ParseStream error: {ex.Message}");
        }

        return null;
    }

    private static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        
        // /watch?v=XXXXXXXXXXX
        var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]v=([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        // /shorts/XXXXXXXXXXX
        match = System.Text.RegularExpressions.Regex.Match(url, @"/shorts/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    private async Task<PipedInstance> GetBestInstanceAsync()
    {
        await _instanceLock.WaitAsync();
        try
        {
            // Сортируем по скорости и надёжности
            var available = _instances
                .Where(i => !i.IsTemporarilyDisabled)
                .OrderBy(i => i.AverageResponseMs)
                .ThenByDescending(i => i.SuccessRate)
                .ToList();

            if (available.Count == 0)
            {
                // Сбрасываем все и пробуем заново
                foreach (var inst in _instances)
                    inst.Reset();
                return _instances[0];
            }

            return available[0];
        }
        finally
        {
            _instanceLock.Release();
        }
    }

    private async Task RotateInstanceAsync()
    {
        await _instanceLock.WaitAsync();
        try
        {
            _currentIndex = (_currentIndex + 1) % _instances.Count;
        }
        finally
        {
            _instanceLock.Release();
        }
    }

    private void UpdateAverageResponseTime(long ms)
    {
        if (SuccessfulRequests == 1)
            AverageResponseMs = ms;
        else
            AverageResponseMs = (AverageResponseMs * (SuccessfulRequests - 1) + ms) / SuccessfulRequests;
    }

    /// <summary>
    /// Проверить здоровье всех инстансов
    /// </summary>
    public async Task<List<(string Url, bool Available, long ResponseMs)>> HealthCheckAsync()
    {
        var results = new List<(string, bool, long)>();

        foreach (var instance in _instances)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(5000);
                using var response = await _http.GetAsync($"{instance.BaseUrl}/healthcheck", cts.Token);
                sw.Stop();
                results.Add((instance.BaseUrl, response.IsSuccessStatusCode, sw.ElapsedMilliseconds));
            }
            catch
            {
                sw.Stop();
                results.Add((instance.BaseUrl, false, sw.ElapsedMilliseconds));
            }
        }

        return results;
    }

    private class PipedInstance
    {
        public string BaseUrl { get; }
        public int SuccessCount { get; private set; }
        public int FailureCount { get; private set; }
        public double AverageResponseMs { get; private set; }
        public DateTime? DisabledUntil { get; private set; }

        public double SuccessRate => SuccessCount + FailureCount > 0 
            ? (double)SuccessCount / (SuccessCount + FailureCount) 
            : 0.5;

        public bool IsTemporarilyDisabled => DisabledUntil.HasValue && DateTime.UtcNow < DisabledUntil.Value;

        public PipedInstance(string baseUrl)
        {
            BaseUrl = baseUrl;
        }

        public void RecordSuccess(long responseMs)
        {
            SuccessCount++;
            DisabledUntil = null;
            
            if (SuccessCount == 1)
                AverageResponseMs = responseMs;
            else
                AverageResponseMs = (AverageResponseMs * (SuccessCount - 1) + responseMs) / SuccessCount;
        }

        public void RecordFailure()
        {
            FailureCount++;
            
            // После 3 неудач — отключаем на 2 минуты
            if (FailureCount >= 3)
            {
                DisabledUntil = DateTime.UtcNow.AddMinutes(2);
            }
        }

        public void Reset()
        {
            FailureCount = 0;
            DisabledUntil = null;
        }
    }
}