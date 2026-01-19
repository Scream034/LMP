// Services/YtDlpPool.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Пул для быстрого получения stream URL через yt-dlp
/// Использует параллельные запросы и переиспользование результатов
/// </summary>
public class YtDlpPool : IDisposable
{
    private readonly string _ytdlpPath;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<string, CachedStreamUrl> _urlCache = new();
    private readonly TimeSpan _urlCacheTtl = TimeSpan.FromMinutes(30); // URL живут ~30 мин

    private long _totalRequests = 0;
    private long _cacheHits = 0;
    private long _totalTimeMs = 0;

    public int MaxConcurrentRequests { get; }
    public double CacheHitRate => _totalRequests > 0 ? (double)_cacheHits / _totalRequests : 0;
    public double AverageRequestTimeMs => _totalRequests > 0 ? (double)_totalTimeMs / (_totalRequests - _cacheHits) : 0;

    public YtDlpPool(string ytdlpPath, int maxConcurrent = 3)
    {
        _ytdlpPath = ytdlpPath;
        MaxConcurrentRequests = maxConcurrent;
        _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        // Периодическая очистка кэша
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                CleanExpiredCache();
            }
        });
    }

    /// <summary>
    /// Получить stream URL (с кэшированием)
    /// </summary>
    public async Task<string?> GetStreamUrlAsync(string videoId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalRequests);

        // Проверяем кэш
        if (_urlCache.TryGetValue(videoId, out var cached) && !cached.IsExpired)
        {
            Interlocked.Increment(ref _cacheHits);
            Debug.WriteLine($"[YtDlpPool] Cache HIT for {videoId}");
            return cached.Url;
        }

        // Ждём слот
        await _semaphore.WaitAsync(ct);
        var sw = Stopwatch.StartNew();

        try
        {
            // Двойная проверка после получения слота
            if (_urlCache.TryGetValue(videoId, out cached) && !cached.IsExpired)
            {
                Interlocked.Increment(ref _cacheHits);
                return cached.Url;
            }

            var url = $"https://youtube.com/watch?v={videoId}";
            var streamUrl = await ExecuteYtDlpAsync(url, ct);

            if (!string.IsNullOrEmpty(streamUrl))
            {
                _urlCache[videoId] = new CachedStreamUrl
                {
                    Url = streamUrl,
                    CachedAt = DateTime.UtcNow
                };
            }

            sw.Stop();
            Interlocked.Add(ref _totalTimeMs, sw.ElapsedMilliseconds);
            Debug.WriteLine($"[YtDlpPool] Got URL for {videoId} in {sw.ElapsedMilliseconds}ms");

            return streamUrl;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Предзагрузка URL для списка видео (параллельно)
    /// </summary>
    public async Task PrefetchUrlsAsync(IEnumerable<string> videoIds, CancellationToken ct = default)
    {
        var tasks = videoIds
            .Where(id => !_urlCache.ContainsKey(id) || _urlCache[id].IsExpired)
            .Take(MaxConcurrentRequests * 2) // Не больше 2x от пула
            .Select(id => GetStreamUrlAsync(id, ct));

        await Task.WhenAll(tasks);
    }

    private async Task<string?> ExecuteYtDlpAsync(string url, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytdlpPath,
                Arguments = $"-f bestaudio --get-url --no-warnings --no-playlist \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15)); // Таймаут 15 сек

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw;
            }

            var output = await outputTask;
            var streamUrl = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

            return string.IsNullOrEmpty(streamUrl) ? null : streamUrl;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpPool] Error: {ex.Message}");
            return null;
        }
    }

    private void CleanExpiredCache()
    {
        var expiredKeys = _urlCache
            .Where(kv => kv.Value.IsExpired)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _urlCache.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            Debug.WriteLine($"[YtDlpPool] Cleaned {expiredKeys.Count} expired URLs");
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private class CachedStreamUrl
    {
        public string Url { get; set; } = "";
        public DateTime CachedAt { get; set; }
        public bool IsExpired => DateTime.UtcNow - CachedAt > TimeSpan.FromMinutes(30);
    }
}