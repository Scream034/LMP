using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

public sealed class ImageCacheService : IDisposable
{
    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }

    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _downloadSemaphore = new(5);

    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Lock _lruLock = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private long _currentDiskCacheBytes = 0;
    private bool _isDisposed;

    // Счетчик загрузок мимо кэша для триггера GC
    private int _transientLoadCounter = 0;
    private const int GcTriggerThreshold = 50;

    // Получаем лимит динамически
    private int MaxMemoryItems => _library.Settings.Storage.MaxBitmapCacheItems > 0
        ? _library.Settings.Storage.MaxBitmapCacheItems
        : 40;

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        _ = Task.Run(CalculateDiskCacheSizeAsync);
    }

    public async Task<Bitmap?> GetImageAsync(string url, CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        var key = GetCacheKey(url);

        if (_memoryCache.TryGetValue(key, out var cached))
        {
            TouchLru(key);
            return cached.Bitmap;
        }

        return await LoadFromDiskOrNetwork(url, key, ct);
    }

    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;
        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Where(u => !_memoryCache.ContainsKey(GetCacheKey(u)))
            .Take(15)
            .Select(u => EnsureCachedDiskOnlyAsync(u, ct));

        try { await Task.WhenAll(tasks); } catch { }
    }

    public void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }
        // Агрессивная очистка
        GC.Collect(2, GCCollectionMode.Optimized);
        Log.Info("Memory cache cleared.");
    }

    public void EnforceLimits()
    {
        lock (_lruLock)
        {
            int limit = MaxMemoryItems;
            if (_memoryCache.Count > limit)
            {
                Log.Info($"[ImageCache] Enforcing limit: reducing from {_memoryCache.Count} to {limit}");
                while (_memoryCache.Count > limit && _lruOrder.Count > 0)
                {
                    var oldest = _lruOrder.Last!.Value;
                    _lruOrder.RemoveLast();
                    _memoryCache.TryRemove(oldest, out _);
                }
                // Если сильно почистили - дергаем GC
                Task.Run(() => GC.Collect(2, GCCollectionMode.Optimized));
            }
        }
    }

    public async Task ClearAllAsync()
    {
        ClearMemoryCache();

        var files = Directory.GetFiles(G.Folder.ImageCache);
        foreach (var f in files)
        {
            try
            {
                var key = Path.GetFileNameWithoutExtension(f);
                var lockObj = GetFileLock(key);
                await lockObj.WaitAsync();
                try { File.Delete(f); } finally { lockObj.Release(); }
            }
            catch { }
        }
        Interlocked.Exchange(ref _currentDiskCacheBytes, 0);
        Log.Info("Image disk cache cleared.");
    }

    public (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache);
            long totalSize = files.Sum(static f => new FileInfo(f).Length);
            Interlocked.Exchange(ref _currentDiskCacheBytes, totalSize);
            return (files.Length, totalSize / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    private SemaphoreSlim GetFileLock(string key) => _fileLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private async Task<Bitmap?> LoadFromDiskOrNetwork(string url, string key, CancellationToken ct)
    {
        var fileLock = GetFileLock(key);

        try
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                if (_memoryCache.TryGetValue(key, out var cached))
                {
                    TouchLru(key);
                    return cached.Bitmap;
                }

                await fileLock.WaitAsync(ct);
                try
                {
                    var diskPath = GetDiskPath(key);

                    if (!File.Exists(diskPath))
                    {
                        var bytes = await _http.GetByteArrayAsync(url, ct);
                        await File.WriteAllBytesAsync(diskPath, bytes, ct);
                        Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);

                        long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
                        if (_currentDiskCacheBytes > limitBytes)
                        {
                            _ = Task.Run(CleanupDiskCacheAsync, CancellationToken.None);
                        }
                    }

                    if (File.Exists(diskPath))
                    {
                        return await Task.Run(() =>
                        {
                            try
                            {
                                using var stream = File.OpenRead(diskPath);
                                // Декодируем с уменьшенным размером
                                var bmp = Bitmap.DecodeToWidth(stream, 300);
                                AddToMemoryCache(key, bmp);

                                // Если кэш переполнен и мы не добавили (или вытеснили), 
                                // значит это "транзитная" картинка. Увеличиваем счетчик.
                                if (!_memoryCache.ContainsKey(key))
                                {
                                    CheckGcPressure();
                                }

                                return bmp;
                            }
                            catch (Exception)
                            {
                                try { File.Delete(diskPath); } catch { }
                                return null;
                            }
                        }, ct);
                    }
                    return null;
                }
                finally { fileLock.Release(); }
            }
            finally { _downloadSemaphore.Release(); }
        }
        catch { return null; }
    }

    private void CheckGcPressure()
    {
        var count = Interlocked.Increment(ref _transientLoadCounter);
        if (count >= GcTriggerThreshold)
        {
            Interlocked.Exchange(ref _transientLoadCounter, 0);
            // Подсказываем GC, что у нас много мусора (отброшенных битмапов)
            // Не блокируем поток, делаем это фоном, но с низким приоритетом
            Task.Run(() =>
            {
                GC.Collect(2, GCCollectionMode.Optimized, false);
            });
        }
    }

    private async Task EnsureCachedDiskOnlyAsync(string url, CancellationToken ct)
    {
        var key = GetCacheKey(url);
        var diskPath = GetDiskPath(key);
        if (File.Exists(diskPath)) return;
        _ = await LoadFromDiskOrNetwork(url, key, ct);
    }

    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        lock (_lruLock)
        {
            int limit = MaxMemoryItems;

            while (_memoryCache.Count >= limit && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _memoryCache.TryRemove(oldest, out _);
            }

            // Защита: если лимит 0 или очень мал, не добавляем вовсе
            if (limit > 0)
            {
                if (_memoryCache.TryAdd(key, new CachedImage { Bitmap = bitmap, CachedAt = DateTime.UtcNow }))
                {
                    _lruOrder.AddFirst(key);
                }
            }
        }
    }

    private void TouchLru(string key)
    {
        lock (_lruLock)
        {
            if (_lruOrder.Contains(key))
            {
                _lruOrder.Remove(key);
                _lruOrder.AddFirst(key);
            }
        }
    }

    private static string GetCacheKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..32];
    }

    private static string GetDiskPath(string key) => Path.Combine(G.Folder.ImageCache, $"{key}.jpg");

    private async Task CalculateDiskCacheSizeAsync()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache);
            long total = 0;
            foreach (var file in files) total += new FileInfo(file).Length;
            Interlocked.Exchange(ref _currentDiskCacheBytes, total);
        }
        catch { }
    }

    private async Task CleanupDiskCacheAsync()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache)
                .Select(static f => new FileInfo(f))
                .OrderBy(static f => f.LastAccessTime)
                .ToList();

            long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
            long targetSize = limitBytes / 2;
            long deleted = 0;

            foreach (var file in files)
            {
                if (_currentDiskCacheBytes - deleted <= targetSize) break;

                var key = Path.GetFileNameWithoutExtension(file.Name);
                var fileLock = GetFileLock(key);
                if (await fileLock.WaitAsync(0))
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deleted += size;
                        _fileLocks.TryRemove(key, out _);
                    }
                    catch { }
                    finally { fileLock.Release(); }
                }
            }
            Interlocked.Add(ref _currentDiskCacheBytes, -deleted);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        ClearMemoryCache();
        _downloadSemaphore.Dispose();
        _http.Dispose();
        foreach (var l in _fileLocks.Values) l.Dispose();
        _fileLocks.Clear();
        GC.SuppressFinalize(this);
    }
}