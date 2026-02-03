using System.Collections.Concurrent;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Качество изображения для декодирования
/// </summary>
public enum ImageQuality
{
    /// <summary>Миниатюры (44-60px элементы)</summary>
    Low = 120,

    /// <summary>Средние обложки (120-180px)</summary>
    Medium = 200,

    /// <summary>Большие обложки (200-300px)</summary>
    High = 400,

    /// <summary>Полноразмерные изображения</summary>
    Ultra = 800
}

public sealed class ImageCacheService : IDisposable
{
    #region Nested Types

    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }

    private class PendingLoad
    {
        public CancellationTokenSource Cts { get; } = new();
        public Task<Bitmap?>? Task { get; set; }
    }

    #endregion

    #region Fields

    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _downloadSemaphore = new(4);

    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Lock _lruLock = new();

    private readonly ConcurrentDictionary<string, PendingLoad> _pendingLoads = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private long _currentDiskCacheBytes = 0;
    private bool _isDisposed;
    private int _loadCounter = 0;
    private const int CleanupInterval = 30;

    #endregion

    #region Properties

    private int MaxMemoryItems => _library.Settings.Storage.MaxBitmapCacheItems > 0
        ? _library.Settings.Storage.MaxBitmapCacheItems
        : 40;

    #endregion

    #region Constructor

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _ = Task.Run(CalculateDiskCacheSizeAsync);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Загружает изображение с указанным качеством.
    /// </summary>
    public async Task<Bitmap?> GetImageAsync(
        string url,
        ImageQuality quality = ImageQuality.Low,
        CancellationToken ct = default)
    {
        return await GetImageAsync(url, (int)quality, ct);
    }

    /// <summary>
    /// Загружает изображение с указанным размером декодирования.
    /// </summary>
    public async Task<Bitmap?> GetImageAsync(
        string url,
        int decodeWidth,
        CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        var key = GetCacheKey(url, decodeWidth);

        // 1. Memory cache
        if (_memoryCache.TryGetValue(key, out var cached))
        {
            TouchLru(key);
            return cached.Bitmap;
        }

        // 2. Already loading
        if (_pendingLoads.TryGetValue(key, out var pending))
        {
            try
            {
                return await (pending.Task ?? Task.FromResult<Bitmap?>(null));
            }
            catch
            {
                return null;
            }
        }

        // 3. Start new load
        var loadEntry = new PendingLoad();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, loadEntry.Cts.Token);

        if (!_pendingLoads.TryAdd(key, loadEntry))
        {
            if (_pendingLoads.TryGetValue(key, out pending))
            {
                try { return await (pending.Task ?? Task.FromResult<Bitmap?>(null)); }
                catch { return null; }
            }
            return null;
        }

        try
        {
            loadEntry.Task = LoadFromDiskOrNetworkAsync(url, key, decodeWidth, linkedCts.Token);
            return await loadEntry.Task;
        }
        finally
        {
            _pendingLoads.TryRemove(key, out _);

            if (Interlocked.Increment(ref _loadCounter) % CleanupInterval == 0)
            {
                _ = Task.Run(PerformMaintenanceAsync, ct);
            }
        }
    }

    public void CancelLoad(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        var key = GetCacheKey(url);

        if (_pendingLoads.TryRemove(key, out var pending))
        {
            try { pending.Cts.Cancel(); }
            catch { }
        }
    }

    public void CancelAllLoads()
    {
        foreach (var kvp in _pendingLoads.ToArray())
        {
            try
            {
                kvp.Value.Cts.Cancel();
                _pendingLoads.TryRemove(kvp.Key, out _);
            }
            catch { }
        }
    }

    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Where(u => !_memoryCache.ContainsKey(GetCacheKey(u)))
            .Take(10)
            .Select(u => EnsureCachedDiskOnlyAsync(u, ct));

        try { await Task.WhenAll(tasks); }
        catch { }
    }

    public void ClearMemoryCache()
    {
        CancelAllLoads();

        lock (_lruLock)
        {
            // ТРЕКИНГ - сбрасываем счётчики
            MemoryDiagnostics.SetBytes("ImageCache.Bitmaps", 0);
            MemoryDiagnostics.SetBytes("ImageCache.Items", 0);

            foreach (var item in _memoryCache.Values)
            {
                item.Bitmap?.Dispose();
            }

            _memoryCache.Clear();
            _lruOrder.Clear();
        }

        // Форсируем очистку Large Object Heap, где живут битмапы
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        Log.Info("[ImageCache] Memory cache cleared and compacted.");
    }

    public void EnforceLimits()
    {
        lock (_lruLock)
        {
            int limit = MaxMemoryItems;
            int removed = 0;

            while (_memoryCache.Count > limit && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _memoryCache.TryRemove(oldest, out _);
                removed++;
            }

            if (removed > 10)
            {
                Log.Info($"[ImageCache] Evicted {removed} items, now at {_memoryCache.Count}");
                Task.Run(() => GC.Collect(1, GCCollectionMode.Optimized));
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
                try { File.Delete(f); }
                finally { lockObj.Release(); }
            }
            catch { }
        }

        Interlocked.Exchange(ref _currentDiskCacheBytes, 0);
        Log.Info("[ImageCache] Disk cache cleared.");
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
        catch { return (0, 0); }
    }

    #endregion

    #region Private Methods

    private async Task<Bitmap?> LoadFromDiskOrNetworkAsync(
      string url,
      string key,
      int decodeWidth,
      CancellationToken ct)
    {
        try
        {
            await _downloadSemaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            // Double-check memory cache inside lock/semaphore
            if (_memoryCache.TryGetValue(key, out var cached))
            {
                TouchLru(key);
                return cached.Bitmap;
            }

            ct.ThrowIfCancellationRequested();

            var diskKey = GetCacheKey(url);
            var fileLock = GetFileLock(diskKey);
            await fileLock.WaitAsync(ct);

            try
            {
                var diskPath = GetDiskPath(diskKey);

                if (!File.Exists(diskPath))
                {
                    ct.ThrowIfCancellationRequested();

                    // ОПТИМИЗАЦИЯ: Скачиваем потоком, без выделения byte[] в RAM
                    // Это снижает нагрузку на GC и LOH
                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (!response.IsSuccessStatusCode)
                        return null;

                    using var networkStream = await response.Content.ReadAsStreamAsync(ct);
                    using var fileStream = new FileStream(diskPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    await networkStream.CopyToAsync(fileStream, ct);

                    // Обновляем размер кэша
                    Interlocked.Add(ref _currentDiskCacheBytes, fileStream.Length);

                    long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
                    if (_currentDiskCacheBytes > limitBytes)
                    {
                        _ = Task.Run(CleanupDiskCacheAsync, CancellationToken.None);
                    }
                }

                ct.ThrowIfCancellationRequested();

                if (File.Exists(diskPath))
                {
                    var bmp = await Task.Run(() =>
                    {
                        try
                        {
                            using var stream = File.OpenRead(diskPath);

                            if (decodeWidth > 0)
                            {
                                // ОПТИМИЗАЦИЯ: HighQuality лучше для обложек, 
                                // LowQuality может давать "лесенки". Разница в памяти нулевая, в CPU - минимальная.
                                return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
                            }
                            else
                            {
                                return new Bitmap(stream);
                            }
                        }
                        catch
                        {
                            try { File.Delete(diskPath); } catch { }
                            return null;
                        }
                    }, ct);

                    if (bmp != null && !ct.IsCancellationRequested)
                    {
                        AddToMemoryCache(key, bmp);
                        return bmp;
                    }
                }

                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageCache] Load failed for {url}: {ex.Message}");
            return null;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private async Task EnsureCachedDiskOnlyAsync(string url, CancellationToken ct)
    {
        var key = GetCacheKey(url);
        var diskPath = GetDiskPath(key);

        if (File.Exists(diskPath)) return;

        try
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                if (File.Exists(diskPath)) return;

                var bytes = await _http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(diskPath, bytes, ct);
                Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }
        catch { }
    }

    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        // Точный расчёт размера
        long pixelSize = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
        long estimatedSize = pixelSize * 4; // RGBA

        // Для LOH tracking (>85KB)
        if (estimatedSize > 85000)
        {
            MemoryDiagnostics.TrackBytes("ImageCache.LOH", estimatedSize);
        }

        lock (_lruLock)
        {
            while (_memoryCache.Count >= MaxMemoryItems && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();

                if (_memoryCache.TryRemove(oldest, out var removed) && removed.Bitmap != null)
                {
                    long oldPixels = (long)removed.Bitmap.PixelSize.Width * removed.Bitmap.PixelSize.Height;
                    long oldSize = oldPixels * 4;

                    MemoryDiagnostics.UntrackBytes("ImageCache.Bitmaps", oldSize);
                    MemoryDiagnostics.UntrackInstance("ImageCache.Items");

                    if (oldSize > 85000)
                    {
                        MemoryDiagnostics.UntrackBytes("ImageCache.LOH", oldSize);
                    }
                }
            }

            if (MaxMemoryItems > 0 && _memoryCache.TryAdd(key, new CachedImage { Bitmap = bitmap, CachedAt = DateTime.UtcNow }))
            {
                _lruOrder.AddFirst(key);
                MemoryDiagnostics.TrackBytes("ImageCache.Bitmaps", estimatedSize);
                MemoryDiagnostics.TrackInstance("ImageCache.Items");
            }
        }
    }

    private void TouchLru(string key)
    {
        lock (_lruLock)
        {
            var node = _lruOrder.Find(key);
            if (node != null)
            {
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(key);
            }
        }
    }

    private async Task PerformMaintenanceAsync()
    {
        EnforceLimits();

        foreach (var kvp in _pendingLoads.ToArray())
        {
            if (kvp.Value.Task?.IsCompleted == true)
            {
                _pendingLoads.TryRemove(kvp.Key, out _);
            }
        }

        var memInfo = GC.GetGCMemoryInfo();
        if (memInfo.MemoryLoadBytes > memInfo.HighMemoryLoadThresholdBytes * 0.8)
        {
            GC.Collect(1, GCCollectionMode.Optimized);
        }

        await Task.CompletedTask;
    }

    private SemaphoreSlim GetFileLock(string key) =>
        _fileLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private static string GetCacheKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..32];
    }

    private static string GetCacheKey(string url, int decodeWidth)
    {
        // Для Low качества используем общий ключ
        if (decodeWidth <= (int)ImageQuality.Low)
            return GetCacheKey(url);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{url}_{decodeWidth}"));
        return Convert.ToHexString(bytes)[..32];
    }

    private static string GetDiskPath(string key) =>
        Path.Combine(G.Folder.ImageCache, $"{key}.jpg");

    private async Task CalculateDiskCacheSizeAsync()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache);
            long total = files.Sum(static f => new FileInfo(f).Length);
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

                if (_memoryCache.ContainsKey(key)) continue;

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
            Log.Info($"[ImageCache] Disk cleanup: removed {deleted / 1024}KB");
        }
        catch { }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        CancelAllLoads();
        ClearMemoryCache();

        _downloadSemaphore.Dispose();
        _http.Dispose();

        foreach (var l in _fileLocks.Values)
            l.Dispose();
        _fileLocks.Clear();

        GC.SuppressFinalize(this);
    }

    #endregion
}