// === ФАЙЛ: Core/Services/ImageCacheService.cs ===
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Управляет загрузкой и кэшированием изображений.
/// Реализует двухуровневый кэш: Память (LRU) + Диск.
/// С поддержкой динамических лимитов из настроек.
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }

    private const int MaxMemoryCacheItems = 60;
    
    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly string _cacheFolder;
    private readonly SemaphoreSlim _downloadSemaphore = new(5);

    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Lock _lruLock = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private long _currentDiskCacheBytes = 0;
    private bool _isDisposed;

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "ImageCache");
        Directory.CreateDirectory(_cacheFolder);

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
        GC.Collect(2, GCCollectionMode.Optimized);
        Log.Info("Memory cache cleared.");
    }
    
    public async Task ClearAllAsync()
    {
        ClearMemoryCache();
        
        // Очистка диска
        var files = Directory.GetFiles(_cacheFolder);
        foreach (var f in files)
        {
            try 
            {
                // Пытаемся взять лок на файл если он используется
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
            var files = Directory.GetFiles(_cacheFolder);
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
                        
                        // Check limit
                        long limitBytes = (long)_library.Data.Storage.ImageCacheLimitMb * 1024 * 1024;
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
                                var bmp = Bitmap.DecodeToWidth(stream, 300);
                                AddToMemoryCache(key, bmp);
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
            while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _memoryCache.TryRemove(oldest, out _);
            }
            if (_memoryCache.TryAdd(key, new CachedImage { Bitmap = bitmap, CachedAt = DateTime.UtcNow }))
            {
                _lruOrder.AddFirst(key);
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

    private string GetDiskPath(string key) => Path.Combine(_cacheFolder, $"{key}.jpg");

    private async Task CalculateDiskCacheSizeAsync()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder);
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
            var files = Directory.GetFiles(_cacheFolder)
                .Select(static f => new FileInfo(f))
                .OrderBy(static f => f.LastAccessTime)
                .ToList();

            long limitBytes = (long)_library.Data.Storage.ImageCacheLimitMb * 1024 * 1024;
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