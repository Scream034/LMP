using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace MyLiteMusicPlayer.Core.Services;

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

    // Блокировка файлов по ключу для предотвращения IOException
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

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
    /// Получить семафор для конкретного файла
    /// </summary>
    private SemaphoreSlim GetFileLock(string key)
    {
        return _fileLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
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

        // 2. Проверяем диск (С БЛОКИРОВКОЙ)
        var diskPath = GetDiskPath(key);
        if (File.Exists(diskPath))
        {
            var fileLock = GetFileLock(key);
            try
            {
                // Пытаемся быстро получить доступ к файлу
                if (await fileLock.WaitAsync(100, ct))
                {
                    try
                    {
                        if (File.Exists(diskPath)) // Проверяем снова после лока
                        {
                            return await Task.Run(() =>
                            {
                                using var stream = File.OpenRead(diskPath);
                                var bmp = Bitmap.DecodeToWidth(stream, 300);
                                // Кэшируем в память, чтобы реже ходить на диск
                                AddToMemoryCache(key, bmp);
                                return bmp;
                            });
                        }
                    }
                    finally
                    {
                        fileLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                // Повреждённый файл — удаляем, если ошибка не ввода-вывода блокировки
                if (ex is not IOException)
                {
                    try { File.Delete(diskPath); } catch { }
                }
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
            .Select(url => EnsureCachedAsync(url, ct)); // Используем EnsureCachedAsync вместо GetImageAsync чтобы не декодировать Bitmap зря

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

        var fileLock = GetFileLock(key);

        // Ждем очередь на загрузку
        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            // Блокируем конкретный файл
            await fileLock.WaitAsync(ct);
            try
            {
                // Повторная проверка
                if (File.Exists(diskPath)) return true;

                var bytes = await _http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(diskPath, bytes, ct);

                Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);

                if (_currentDiskCacheBytes > MaxDiskCacheMb * 1024 * 1024)
                {
                    _ = Task.Run(CleanupDiskCacheAsync);
                }

                return true;
            }
            finally
            {
                fileLock.Release();
            }
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
        var fileLock = GetFileLock(key);

        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            // Повторная проверка памяти
            if (_memoryCache.TryGetValue(key, out var cached))
            {
                return cached.Bitmap;
            }

            await fileLock.WaitAsync(ct);
            try
            {
                var diskPath = GetDiskPath(key);
                
                // Проверяем диск еще раз внутри лока (вдруг другой поток уже скачал)
                if (File.Exists(diskPath))
                {
                     using var stream = File.OpenRead(diskPath);
                     return Bitmap.DecodeToWidth(stream, 300);
                }

                var bytes = await _http.GetByteArrayAsync(url, ct);

                // Сохраняем на диск
                await File.WriteAllBytesAsync(diskPath, bytes, ct);
                Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);

                var bmp = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(stream, 300);
                });

                AddToMemoryCache(key, bmp);
                return bmp;
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Download failed for {url}: {ex.Message}");
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
                    // Внимание: мы не диспозим Bitmap здесь жестко, так как он может использоваться в UI
                    // Avalonia сама разберется, или можно оставить Dispose на совести GC для Bitmap
                    // removed.Bitmap?.Dispose(); 
                }
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
            Log.Info($"Disk cache size: {total / 1024 / 1024}MB");
        }
        catch { }
    }

    private async Task CleanupDiskCacheAsync()
    {
        try
        {
            Log.Info($"Cleaning up disk cache...");

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

                var key = Path.GetFileNameWithoutExtension(file.Name);
                var fileLock = GetFileLock(key);
                
                // Пробуем удалить с блокировкой
                if (await fileLock.WaitAsync(0))
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deleted += size;
                        // Удаляем лок из словаря, раз файл удален
                        _fileLocks.TryRemove(key, out _);
                    }
                    catch { }
                    finally
                    {
                        fileLock.Release();
                    }
                }
            }

            Interlocked.Add(ref _currentDiskCacheBytes, -deleted);
            Log.Info($"Deleted {deleted / 1024 / 1024}MB");
        }
        catch { }
    }

    private static string GetCacheKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
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
            // Здесь тоже аккуратно с Dispose, если картинка сейчас на экране
            _memoryCache.Clear();
            _lruOrder.Clear();
        }
        Log.Info($"Memory cache cleared");
    }

    public void ClearAllCache()
    {
        ClearMemoryCache();

        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder))
            {
                try { File.Delete(file); } catch { }
            }
            _currentDiskCacheBytes = 0;
        }
        catch { }

        Log.Info($"All cache cleared");
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
        foreach(var lok in _fileLocks.Values) lok.Dispose();
        GC.SuppressFinalize(this);
    }

    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }
}
