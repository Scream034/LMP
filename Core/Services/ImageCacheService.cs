using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace LMP.Core.Services;

/// <summary>
/// Качество изображения для декодирования в память
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
    #region Constants

    /// <summary>Максимальный размер изображения на диске (px). Больше не нужно для обложек.</summary>
    private const int MaxDiskImageSize = 400;

    /// <summary>Качество WebP (0-100). 75-80 оптимально для обложек.</summary>
    private const int WebPQuality = 78;

    /// <summary>Размер буфера для загрузки (используем ArrayPool)</summary>
    private const int DownloadBufferSize = 81920; // 80KB

    /// <summary>Минимальный размер для сжатия. Меньшие файлы не трогаем.</summary>
    private const int MinSizeForCompression = 10 * 1024; // 10KB

    #endregion

    #region Nested Types

    private sealed class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public long EstimatedBytes { get; set; }
        public DateTime CachedAt { get; set; }
    }

    private sealed class PendingLoad : IDisposable
    {
        public CancellationTokenSource Cts { get; } = new();
        public Task<Bitmap?>? Task { get; set; }

        public void Dispose() => Cts.Dispose();
    }

    #endregion

    #region Fields

    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _downloadSemaphore = new(4);
    private readonly SemaphoreSlim _processSemaphore = new(2); // Для CPU-bound операций

    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Lock _lruLock = new();

    private readonly ConcurrentDictionary<string, PendingLoad> _pendingLoads = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private long _currentDiskCacheBytes;
    private long _currentMemoryCacheBytes;
    private bool _isDisposed;
    private int _loadCounter;
    private const int CleanupInterval = 25;

    // Переиспользуемые буферы для уменьшения давления на GC
    private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;

    #endregion

    #region Properties

    private int MaxMemoryItems => _library.Settings.Storage.MaxBitmapCacheItems > 0
        ? _library.Settings.Storage.MaxBitmapCacheItems
        : 50;

    /// <summary>Максимальный размер memory-кэша в байтах (примерно)</summary>
    private long MaxMemoryBytes => MaxMemoryItems * 200 * 200 * 4; // ~160KB на картинку

    #endregion

    #region Constructor

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" }
            }
        };

        // Считаем размер кэша в фоне
        _ = Task.Run(InitializeDiskCacheAsync);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Загружает изображение с указанным качеством.
    /// </summary>
    public Task<Bitmap?> GetImageAsync(
        string url,
        ImageQuality quality = ImageQuality.Low,
        CancellationToken ct = default)
        => GetImageAsync(url, (int)quality, ct);

    /// <summary>
    /// Загружает изображение с указанным размером декодирования.
    /// </summary>
    public async Task<Bitmap?> GetImageAsync(
        string url,
        int decodeWidth,
        CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        // Ключ включает размер для разных качеств одной картинки
        var memoryKey = GetMemoryCacheKey(url, decodeWidth);

        // 1. Memory cache (быстрый путь)
        if (_memoryCache.TryGetValue(memoryKey, out var cached))
        {
            TouchLru(memoryKey);
            return cached.Bitmap;
        }

        // 2. Проверяем pending загрузки
        if (_pendingLoads.TryGetValue(memoryKey, out var pending))
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

        // 3. Создаём новую загрузку
        var loadEntry = new PendingLoad();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, loadEntry.Cts.Token);

        if (!_pendingLoads.TryAdd(memoryKey, loadEntry))
        {
            // Кто-то успел раньше
            if (_pendingLoads.TryGetValue(memoryKey, out pending))
            {
                try { return await (pending.Task ?? Task.FromResult<Bitmap?>(null)); }
                catch { return null; }
            }
            return null;
        }

        try
        {
            loadEntry.Task = LoadImageInternalAsync(url, memoryKey, decodeWidth, linkedCts.Token);
            return await loadEntry.Task;
        }
        finally
        {
            _pendingLoads.TryRemove(memoryKey, out var removed);
            removed?.Dispose();

            // Периодическое обслуживание
            if (Interlocked.Increment(ref _loadCounter) % CleanupInterval == 0)
            {
                _ = Task.Run(PerformMaintenanceAsync, CancellationToken.None);
            }
        }
    }

    public void CancelLoad(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        // Отменяем все варианты качества
        foreach (var quality in Enum.GetValues<ImageQuality>())
        {
            var key = GetMemoryCacheKey(url, (int)quality);
            if (_pendingLoads.TryRemove(key, out var pending))
            {
                try { pending.Cts.Cancel(); pending.Dispose(); }
                catch { /* ignore */ }
            }
        }
    }

    public void CancelAllLoads()
    {
        foreach (var kvp in _pendingLoads.ToArray())
        {
            try
            {
                kvp.Value.Cts.Cancel();
                _pendingLoads.TryRemove(kvp.Key, out var removed);
                removed?.Dispose();
            }
            catch { /* ignore */ }
        }
    }

    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Where(u => !File.Exists(GetDiskPath(GetDiskCacheKey(u))))
            .Take(8)
            .Select(u => EnsureDiskCachedAsync(u, ct));

        try { await Task.WhenAll(tasks); }
        catch { /* ignore prefetch errors */ }
    }

    public void ClearMemoryCache()
    {
        CancelAllLoads();

        lock (_lruLock)
        {
            foreach (var item in _memoryCache.Values)
            {
                item.Bitmap?.Dispose();
            }

            _memoryCache.Clear();
            _lruOrder.Clear();
            Interlocked.Exchange(ref _currentMemoryCacheBytes, 0);
        }

        // Обновляем диагностику
        MemoryDiagnostics.SetBytes("ImageCache.Memory", 0);
        MemoryDiagnostics.SetBytes("ImageCache.Items", 0);

        // Компактим LOH где живут битмапы
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        Log.Info("[ImageCache] Memory cache cleared and LOH compacted.");
    }

    public void EnforceLimits()
    {
        lock (_lruLock)
        {
            int removed = 0;
            long freedBytes = 0;

            // Удаляем по количеству ИЛИ по памяти
            while ((_memoryCache.Count > MaxMemoryItems || _currentMemoryCacheBytes > MaxMemoryBytes)
                   && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();

                if (_memoryCache.TryRemove(oldest, out var item))
                {
                    freedBytes += item.EstimatedBytes;
                    item.Bitmap?.Dispose();
                    removed++;
                }
            }

            if (removed > 0)
            {
                Interlocked.Add(ref _currentMemoryCacheBytes, -freedBytes);
                MemoryDiagnostics.SetBytes("ImageCache.Memory", _currentMemoryCacheBytes);
            }

            if (removed > 5)
            {
                Log.Debug($"[ImageCache] Evicted {removed} items ({freedBytes / 1024}KB), now at {_memoryCache.Count}");
            }
        }
    }

    public async Task ClearDiskCacheAsync()
    {
        ClearMemoryCache();

        var files = Directory.GetFiles(G.Folder.ImageCache);
        int deleted = 0;

        foreach (var f in files)
        {
            try
            {
                var key = Path.GetFileNameWithoutExtension(f);
                var fileLock = GetFileLock(key);

                if (await fileLock.WaitAsync(100))
                {
                    try
                    {
                        File.Delete(f);
                        deleted++;
                    }
                    finally { fileLock.Release(); }
                }
            }
            catch { /* ignore */ }
        }

        Interlocked.Exchange(ref _currentDiskCacheBytes, 0);
        Log.Info($"[ImageCache] Disk cache cleared: {deleted} files.");
    }

    public (int MemoryItems, long MemoryMb, int DiskFiles, long DiskMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache);
            long diskSize = files.Sum(static f => new FileInfo(f).Length);
            Interlocked.Exchange(ref _currentDiskCacheBytes, diskSize);

            return (
                _memoryCache.Count,
                _currentMemoryCacheBytes / 1024 / 1024,
                files.Length,
                diskSize / 1024 / 1024
            );
        }
        catch
        {
            return (_memoryCache.Count, _currentMemoryCacheBytes / 1024 / 1024, 0, 0);
        }
    }

    #endregion

    #region Private Methods - Core Loading

    private async Task<Bitmap?> LoadImageInternalAsync(
        string url,
        string memoryKey,
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
            // Double-check после получения семафора
            if (_memoryCache.TryGetValue(memoryKey, out var cached))
            {
                TouchLru(memoryKey);
                return cached.Bitmap;
            }

            ct.ThrowIfCancellationRequested();

            var diskKey = GetDiskCacheKey(url);
            var diskPath = GetDiskPath(diskKey);
            var fileLock = GetFileLock(diskKey);

            await fileLock.WaitAsync(ct);
            try
            {
                // Скачиваем если нет на диске
                if (!File.Exists(diskPath))
                {
                    ct.ThrowIfCancellationRequested();
                    await DownloadAndProcessImageAsync(url, diskPath, ct);
                }

                ct.ThrowIfCancellationRequested();

                // Декодируем в Bitmap
                if (File.Exists(diskPath))
                {
                    var bitmap = await DecodeBitmapAsync(diskPath, decodeWidth, ct);

                    if (bitmap != null && !ct.IsCancellationRequested)
                    {
                        AddToMemoryCache(memoryKey, bitmap);
                        return bitmap;
                    }
                }
            }
            finally
            {
                fileLock.Release();
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageCache] Load failed: {ex.Message}");
            return null;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    /// <summary>
    /// Скачивает изображение, конвертирует в WebP с ресайзом и сохраняет.
    /// </summary>
    private async Task DownloadAndProcessImageAsync(string url, string diskPath, CancellationToken ct)
    {
        byte[]? rentedBuffer = null;

        try
        {
            // Скачиваем в память (используем пул буферов)
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"[ImageCache] HTTP {response.StatusCode} for {url}");
                return;
            }

            var contentLength = response.Content.Headers.ContentLength ?? 512 * 1024;
            var estimatedSize = Math.Min((int)contentLength, 2 * 1024 * 1024); // Max 2MB

            using var memoryStream = new MemoryStream(estimatedSize);

            // Копируем с использованием арендованного буфера
            rentedBuffer = BufferPool.Rent(DownloadBufferSize);

            await using var networkStream = await response.Content.ReadAsStreamAsync(ct);

            int bytesRead;
            while ((bytesRead = await networkStream.ReadAsync(rentedBuffer.AsMemory(0, DownloadBufferSize), ct)) > 0)
            {
                await memoryStream.WriteAsync(rentedBuffer.AsMemory(0, bytesRead), ct);

                // Защита от слишком больших файлов
                if (memoryStream.Length > 5 * 1024 * 1024)
                {
                    Log.Warn($"[ImageCache] Image too large, skipping: {url}");
                    return;
                }
            }

            BufferPool.Return(rentedBuffer);
            rentedBuffer = null;

            ct.ThrowIfCancellationRequested();

            // Обрабатываем и сохраняем в WebP
            memoryStream.Position = 0;

            await _processSemaphore.WaitAsync(ct);
            try
            {
                var savedBytes = await ProcessAndSaveAsWebPAsync(memoryStream, diskPath, ct);

                if (savedBytes > 0)
                {
                    Interlocked.Add(ref _currentDiskCacheBytes, savedBytes);

                    // Проверяем лимит диска
                    long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
                    if (_currentDiskCacheBytes > limitBytes)
                    {
                        _ = Task.Run(CleanupDiskCacheAsync, CancellationToken.None);
                    }
                }
            }
            finally
            {
                _processSemaphore.Release();
            }
        }
        finally
        {
            if (rentedBuffer != null)
            {
                BufferPool.Return(rentedBuffer);
            }
        }
    }

    /// <summary>
    /// Конвертирует изображение в WebP с ресайзом до MaxDiskImageSize.
    /// </summary>
    private async Task<long> ProcessAndSaveAsWebPAsync(Stream sourceStream, string diskPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var originalBitmap = SKBitmap.Decode(sourceStream);

                if (originalBitmap == null)
                {
                    Log.Debug("[ImageCache] Failed to decode source image");
                    return 0L;
                }

                var width = originalBitmap.Width;
                var height = originalBitmap.Height;

                SKBitmap bitmapToSave;

                // Ресайз если нужно
                if (width > MaxDiskImageSize || height > MaxDiskImageSize)
                {
                    var scale = Math.Min(
                        (float)MaxDiskImageSize / width,
                        (float)MaxDiskImageSize / height
                    );

                    var newWidth = (int)(width * scale);
                    var newHeight = (int)(height * scale);

                    // Используем высококачественный ресайз
                    bitmapToSave = originalBitmap.Resize(
                        new SKImageInfo(newWidth, newHeight),
                        SKFilterQuality.High
                    );

                    if (bitmapToSave == null)
                    {
                        bitmapToSave = originalBitmap;
                    }
                }
                else
                {
                    bitmapToSave = originalBitmap;
                }

                // Кодируем в WebP
                using var image = SKImage.FromBitmap(bitmapToSave);
                using var data = image.Encode(SKEncodedImageFormat.Webp, WebPQuality);

                if (data == null)
                {
                    Log.Debug("[ImageCache] WebP encoding failed, saving as JPEG");
                    // Fallback на JPEG
                    using var jpegData = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                    if (jpegData == null) return 0L;

                    using var fs = File.Create(diskPath);
                    jpegData.SaveTo(fs);
                    return fs.Length;
                }

                // Сохраняем WebP
                using (var fs = File.Create(diskPath))
                {
                    data.SaveTo(fs);
                }

                // Освобождаем ресайзнутый битмап если создавали
                if (bitmapToSave != originalBitmap)
                {
                    bitmapToSave.Dispose();
                }

                return new FileInfo(diskPath).Length;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ImageCache] Process failed: {ex.Message}");

                // Fallback: сохраняем как есть
                try
                {
                    sourceStream.Position = 0;
                    using var fs = File.Create(diskPath);
                    sourceStream.CopyTo(fs);
                    return fs.Length;
                }
                catch
                {
                    return 0L;
                }
            }
        }, ct);
    }

    /// <summary>
    /// Декодирует Bitmap из файла с указанным размером.
    /// </summary>
    private static async Task<Bitmap?> DecodeBitmapAsync(string path, int decodeWidth, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);

                if (decodeWidth > 0)
                {
                    return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality);
                }

                return new Bitmap(stream);
            }
            catch (Exception ex)
            {
                Log.Debug($"[ImageCache] Decode failed: {ex.Message}");

                // Битый файл - удаляем
                try { File.Delete(path); } catch { }
                return null;
            }
        }, ct);
    }

    /// <summary>
    /// Предзагрузка на диск без декодирования в память.
    /// </summary>
    private async Task EnsureDiskCachedAsync(string url, CancellationToken ct)
    {
        var diskKey = GetDiskCacheKey(url);
        var diskPath = GetDiskPath(diskKey);

        if (File.Exists(diskPath)) return;

        try
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                if (File.Exists(diskPath)) return;

                var fileLock = GetFileLock(diskKey);
                await fileLock.WaitAsync(ct);
                try
                {
                    await DownloadAndProcessImageAsync(url, diskPath, ct);
                }
                finally
                {
                    fileLock.Release();
                }
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }
        catch
        {
            // Prefetch ошибки игнорируем
        }
    }

    #endregion

    #region Private Methods - Cache Management

    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        // Считаем размер
        long pixelCount = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
        long estimatedBytes = pixelCount * 4; // RGBA

        lock (_lruLock)
        {
            // Освобождаем место
            while ((_memoryCache.Count >= MaxMemoryItems || _currentMemoryCacheBytes + estimatedBytes > MaxMemoryBytes)
                   && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();

                if (_memoryCache.TryRemove(oldest, out var removed))
                {
                    Interlocked.Add(ref _currentMemoryCacheBytes, -removed.EstimatedBytes);
                    removed.Bitmap?.Dispose();
                }
            }

            // Добавляем
            var cacheItem = new CachedImage
            {
                Bitmap = bitmap,
                EstimatedBytes = estimatedBytes,
                CachedAt = DateTime.UtcNow
            };

            if (_memoryCache.TryAdd(key, cacheItem))
            {
                _lruOrder.AddFirst(key);
                Interlocked.Add(ref _currentMemoryCacheBytes, estimatedBytes);
            }
        }

        // Обновляем диагностику
        MemoryDiagnostics.SetBytes("ImageCache.Memory", _currentMemoryCacheBytes);
        MemoryDiagnostics.SetBytes("ImageCache.Items", _memoryCache.Count);
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
        // 1. Чистим memory cache
        EnforceLimits();

        // 2. Чистим завершённые pending
        foreach (var kvp in _pendingLoads.ToArray())
        {
            if (kvp.Value.Task?.IsCompleted == true)
            {
                _pendingLoads.TryRemove(kvp.Key, out var removed);
                removed?.Dispose();
            }
        }

        // 3. Чистим неиспользуемые file locks
        foreach (var kvp in _fileLocks.ToArray())
        {
            if (kvp.Value.CurrentCount == 1) // Никто не держит
            {
                _fileLocks.TryRemove(kvp.Key, out _);
            }
        }

        // 4. Проверяем давление памяти
        var memInfo = GC.GetGCMemoryInfo();
        if (memInfo.MemoryLoadBytes > memInfo.HighMemoryLoadThresholdBytes * 0.85)
        {
            Log.Info("[ImageCache] High memory pressure, clearing half of cache");

            lock (_lruLock)
            {
                int toRemove = _memoryCache.Count / 2;
                for (int i = 0; i < toRemove && _lruOrder.Count > 0; i++)
                {
                    var oldest = _lruOrder.Last!.Value;
                    _lruOrder.RemoveLast();

                    if (_memoryCache.TryRemove(oldest, out var item))
                    {
                        Interlocked.Add(ref _currentMemoryCacheBytes, -item.EstimatedBytes);
                        item.Bitmap?.Dispose();
                    }
                }
            }

            GC.Collect(1, GCCollectionMode.Optimized, false);
        }

        await Task.CompletedTask;
    }

    private async Task CleanupDiskCacheAsync()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache)
                .Select(static f => new FileInfo(f))
                .OrderBy(static f => f.LastAccessTimeUtc)
                .ToList();

            if (files.Count == 0) return;

            long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
            long targetSize = (long)(limitBytes * 0.7); // Чистим до 70%
            long deletedBytes = 0;
            int deletedCount = 0;

            foreach (var file in files)
            {
                if (_currentDiskCacheBytes - deletedBytes <= targetSize)
                    break;

                var key = Path.GetFileNameWithoutExtension(file.Name);

                // Не удаляем файлы, которые в памяти
                if (_memoryCache.Keys.Any(k => k.StartsWith(key)))
                    continue;

                var fileLock = GetFileLock(key);
                if (await fileLock.WaitAsync(0))
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deletedBytes += size;
                        deletedCount++;
                        _fileLocks.TryRemove(key, out _);
                    }
                    catch { }
                    finally { fileLock.Release(); }
                }
            }

            if (deletedBytes > 0)
            {
                Interlocked.Add(ref _currentDiskCacheBytes, -deletedBytes);
                Log.Info($"[ImageCache] Disk cleanup: {deletedCount} files, {deletedBytes / 1024}KB freed");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageCache] Disk cleanup failed: {ex.Message}");
        }
    }

    private async Task InitializeDiskCacheAsync()
    {
        try
        {
            if (!Directory.Exists(G.Folder.ImageCache))
            {
                Directory.CreateDirectory(G.Folder.ImageCache);
                return;
            }

            var files = Directory.GetFiles(G.Folder.ImageCache);
            long total = 0;

            foreach (var file in files)
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch { }
            }

            Interlocked.Exchange(ref _currentDiskCacheBytes, total);
            Log.Info($"[ImageCache] Initialized: {files.Length} files, {total / 1024 / 1024}MB");
        }
        catch { }
    }

    #endregion

    #region Private Methods - Helpers

    private SemaphoreSlim GetFileLock(string key) =>
        _fileLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Ключ для диска - только URL (WebP файл общий для всех качеств).
    /// </summary>
    private static string GetDiskCacheKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..32];
    }

    /// <summary>
    /// Ключ для памяти - URL + размер (разные Bitmap для разных размеров).
    /// </summary>
    private static string GetMemoryCacheKey(string url, int decodeWidth)
    {
        // Группируем близкие размеры для экономии памяти
        var normalizedWidth = decodeWidth switch
        {
            <= 120 => 120,
            <= 200 => 200,
            <= 400 => 400,
            _ => 800
        };

        var input = $"{url}_{normalizedWidth}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..32];
    }

    /// <summary>
    /// Путь к файлу на диске (без расширения - может быть webp или jpg).
    /// </summary>
    private static string GetDiskPath(string key) =>
        Path.Combine(G.Folder.ImageCache, key);  // Без расширения!

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        CancelAllLoads();
        ClearMemoryCache();

        _downloadSemaphore.Dispose();
        _processSemaphore.Dispose();
        _http.Dispose();

        foreach (var l in _fileLocks.Values)
        {
            try { l.Dispose(); } catch { }
        }
        _fileLocks.Clear();

        GC.SuppressFinalize(this);
    }

    #endregion
}