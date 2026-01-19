// Services/ImageCacheService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Кэширование изображений на диск + память
/// </summary>
public class ImageCacheService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _cacheFolder;
    private readonly SemaphoreSlim _downloadSemaphore = new(5); // Макс 5 параллельных загрузок
    
    // LRU memory cache
    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly object _lruLock = new();
    
    private const int MaxMemoryCacheItems = 100;
    private const int MaxDiskCacheMb = 200;
    private long _currentDiskCacheBytes = 0;

    public ImageCacheService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "ImageCache");
        Directory.CreateDirectory(_cacheFolder);

        // Подсчёт текущего размера кэша
        _ = Task.Run(CalculateDiskCacheSizeAsync);
    }

    /// <summary>
    /// Получить изображение (память → диск → интернет)
    /// </summary>
    public async Task<Bitmap?> GetImageAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var key = GetCacheKey(url);

        // 1. Проверяем память
        if (_memoryCache.TryGetValue(key, out var cached))
        {
            TouchLru(key);
            return cached.Bitmap;
        }

        // 2. Проверяем диск
        var diskPath = GetDiskPath(key);
        if (File.Exists(diskPath))
        {
            try
            {
                var bitmap = new Bitmap(diskPath);
                AddToMemoryCache(key, bitmap);
                return bitmap;
            }
            catch
            {
                // Повреждённый файл — удаляем
                try { File.Delete(diskPath); } catch { }
            }
        }

        // 3. Загружаем из интернета
        return await DownloadAndCacheAsync(url, key, ct);
    }

    /// <summary>
    /// Предзагрузка списка изображений (для плавного скролла)
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        var tasks = urls
            .Where(url => !string.IsNullOrEmpty(url))
            .Where(url => !_memoryCache.ContainsKey(GetCacheKey(url)))
            .Take(20) // Не больше 20 за раз
            .Select(url => GetImageAsync(url, ct));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Получить путь к кэшированному файлу (для AsyncImageLoader)
    /// </summary>
    public string? GetCachedPath(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        
        var key = GetCacheKey(url);
        var diskPath = GetDiskPath(key);
        
        return File.Exists(diskPath) ? diskPath : null;
    }

    /// <summary>
    /// Кэшировать изображение заранее (без загрузки в память)
    /// </summary>
    public async Task<bool> EnsureCachedAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return false;

        var key = GetCacheKey(url);
        var diskPath = GetDiskPath(key);

        if (File.Exists(diskPath)) return true;

        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            // Повторная проверка после получения семафора
            if (File.Exists(diskPath)) return true;

            var bytes = await _http.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(diskPath, bytes, ct);
            
            Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);
            
            // Проверяем лимит
            if (_currentDiskCacheBytes > MaxDiskCacheMb * 1024 * 1024)
            {
                _ = Task.Run(CleanupDiskCacheAsync);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private async Task<Bitmap?> DownloadAndCacheAsync(string url, string key, CancellationToken ct)
    {
        await _downloadSemaphore.WaitAsync(ct);
        
        try
        {
            // Повторная проверка после получения семафора
            if (_memoryCache.TryGetValue(key, out var cached))
            {
                return cached.Bitmap;
            }

            var bytes = await _http.GetByteArrayAsync(url, ct);
            var diskPath = GetDiskPath(key);

            // Сохраняем на диск
            await File.WriteAllBytesAsync(diskPath, bytes, ct);
            Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);

            // Создаём Bitmap
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            AddToMemoryCache(key, bitmap);

            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageCache] Download failed: {ex.Message}");
            return null;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        lock (_lruLock)
        {
            // Удаляем старые если превышен лимит
            while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                
                if (_memoryCache.TryRemove(oldest, out var removed))
                {
                    removed.Bitmap?.Dispose();
                }
            }

            _memoryCache[key] = new CachedImage { Bitmap = bitmap, CachedAt = DateTime.UtcNow };
            _lruOrder.AddFirst(key);
        }
    }

    private void TouchLru(string key)
    {
        lock (_lruLock)
        {
            _lruOrder.Remove(key);
            _lruOrder.AddFirst(key);
        }
    }

    private async Task CalculateDiskCacheSizeAsync()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder);
            long total = 0;
            foreach (var file in files)
            {
                total += new FileInfo(file).Length;
            }
            Interlocked.Exchange(ref _currentDiskCacheBytes, total);
            Debug.WriteLine($"[ImageCache] Disk cache size: {total / 1024 / 1024}MB");
        }
        catch { }
    }

    private async Task CleanupDiskCacheAsync()
    {
        try
        {
            Debug.WriteLine($"[ImageCache] Cleaning up disk cache...");
            
            var files = Directory.GetFiles(_cacheFolder)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastAccessTime)
                .ToList();

            long targetSize = MaxDiskCacheMb * 1024 * 1024 / 2; // Удаляем половину
            long deleted = 0;

            foreach (var file in files)
            {
                if (_currentDiskCacheBytes - deleted <= targetSize)
                    break;

                try
                {
                    var size = file.Length;
                    file.Delete();
                    deleted += size;
                }
                catch { }
            }

            Interlocked.Add(ref _currentDiskCacheBytes, -deleted);
            Debug.WriteLine($"[ImageCache] Deleted {deleted / 1024 / 1024}MB");
        }
        catch { }
    }

    private static string GetCacheKey(string url)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..32];
    }

    private string GetDiskPath(string key)
    {
        return Path.Combine(_cacheFolder, $"{key}.jpg");
    }

    public void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            foreach (var cached in _memoryCache.Values)
            {
                cached.Bitmap?.Dispose();
            }
            _memoryCache.Clear();
            _lruOrder.Clear();
        }
        Debug.WriteLine($"[ImageCache] Memory cache cleared");
    }

    public void ClearAllCache()
    {
        ClearMemoryCache();

        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder))
            {
                File.Delete(file);
            }
            _currentDiskCacheBytes = 0;
        }
        catch { }

        Debug.WriteLine($"[ImageCache] All cache cleared");
    }

    public (int MemoryItems, long DiskSizeMb) GetStats()
    {
        return (_memoryCache.Count, _currentDiskCacheBytes / 1024 / 1024);
    }

    public void Dispose()
    {
        ClearMemoryCache();
        _downloadSemaphore.Dispose();
        _http.Dispose();
    }

    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }
}