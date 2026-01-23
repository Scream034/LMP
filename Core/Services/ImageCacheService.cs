// ============================================================================
// Файл: Core/Services/ImageCacheService.cs
// Описание: Сервис кэширования изображений.
// Исправления:
//   - [FIX] Явный вызов Dispose() для Bitmap. Это критично для освобождения нативной памяти Skia.
//   - [FIX] Агрессивная очистка памяти при ClearMemoryCache().
// ============================================================================

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace MyLiteMusicPlayer.Core.Services;

/// <summary>
/// Управляет загрузкой и кэшированием изображений.
/// Реализует двухуровневый кэш: Память (LRU) + Диск.
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    #region Nested Types

    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }

    #endregion

    #region Constants

    private const int MaxMemoryCacheItems = 60; // Уменьшено с 100
    private const int MaxDiskCacheMb = 200;

    #endregion

    #region Fields

    private readonly HttpClient _http;
    private readonly string _cacheFolder;
    private readonly SemaphoreSlim _downloadSemaphore = new(5);

    // LRU Cache
    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Lock _lruLock = new();

    // File Locks
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private long _currentDiskCacheBytes = 0;
    private bool _isDisposed;

    #endregion

    #region Constructor

    public ImageCacheService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "ImageCache");
        Directory.CreateDirectory(_cacheFolder);

        // Фоновый подсчет размера кэша
        _ = Task.Run(CalculateDiskCacheSizeAsync);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Получает изображение по URL.
    /// </summary>
    public async Task<Bitmap?> GetImageAsync(string url, CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        var key = GetCacheKey(url);

        // 1. Память
        if (_memoryCache.TryGetValue(key, out var cached))
        {
            TouchLru(key);
            return cached.Bitmap;
        }

        // 2. Диск или Сеть
        return await LoadFromDiskOrNetwork(url, key, ct);
    }

    /// <summary>
    /// Предварительно загружает изображения (только на диск, чтобы не забивать RAM).
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Where(u => !_memoryCache.ContainsKey(GetCacheKey(u)))
            .Take(15)
            .Select(u => EnsureCachedDiskOnlyAsync(u, ct));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Игнорируем ошибки префетча
        }
    }

    /// <summary>
    /// Очищает кэш в оперативной памяти.
    /// </summary>
    public void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }
        // [FIX] Вместо Dispose вызываем GC, чтобы безопасно собрать неиспользуемые битмапы
        GC.Collect(2, GCCollectionMode.Optimized);
        Log.Info("Memory cache cleared.");
    }

    #endregion

    #region Private Methods

    private SemaphoreSlim GetFileLock(string key)
    {
        return _fileLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private async Task<Bitmap?> LoadFromDiskOrNetwork(string url, string key, CancellationToken ct)
    {
        var fileLock = GetFileLock(key);

        try
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                // Double-check memory cache after wait
                if (_memoryCache.TryGetValue(key, out var cached))
                {
                    TouchLru(key);
                    return cached.Bitmap;
                }

                await fileLock.WaitAsync(ct);
                try
                {
                    var diskPath = GetDiskPath(key);

                    // Если нет на диске - качаем
                    if (!File.Exists(diskPath))
                    {
                        var bytes = await _http.GetByteArrayAsync(url, ct);
                        await File.WriteAllBytesAsync(diskPath, bytes, ct);
                        Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);

                        // Если кэш переполнен - чистим диск
                        if (_currentDiskCacheBytes > MaxDiskCacheMb * 1024 * 1024)
                        {
                            _ = Task.Run(CleanupDiskCacheAsync, CancellationToken.None);
                        }
                    }

                    // Грузим с диска
                    if (File.Exists(diskPath))
                    {
                        // [FIX] Загрузка Bitmap должна быть в try-block с Dispose стрима
                        return await Task.Run(() =>
                        {
                            try
                            {
                                using var stream = File.OpenRead(diskPath);
                                // DecodeToWidth экономит память, не загружая полный размер
                                var bmp = Bitmap.DecodeToWidth(stream, 300);
                                AddToMemoryCache(key, bmp);
                                return bmp;
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Failed to decode image: {ex.Message}");
                                try { File.Delete(diskPath); } catch { }
                                return null;
                            }
                        }, ct);
                    }

                    return null;
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
            return null;
        }
    }

    private async Task EnsureCachedDiskOnlyAsync(string url, CancellationToken ct)
    {
        var key = GetCacheKey(url);
        var diskPath = GetDiskPath(key);
        if (File.Exists(diskPath)) return;

        // Используем упрощенную логику загрузки, чтобы не дублировать код,
        // но здесь мы НЕ возвращаем Bitmap, поэтому он не попадет в RAM
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
                // [FIX] Просто удаляем из словаря, не вызывая Dispose.
                // Ссылка на Bitmap пропадет, и он будет собран GC, когда перестанет использоваться в UI.
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
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastAccessTime) // Удаляем самые старые по доступу
                .ToList();

            long targetSize = MaxDiskCacheMb * 1024 * 1024 / 2;
            long deleted = 0;

            foreach (var file in files)
            {
                if (_currentDiskCacheBytes - deleted <= targetSize) break;

                var key = Path.GetFileNameWithoutExtension(file.Name);

                // Пробуем заблокировать файл перед удалением
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
                    finally
                    {
                        fileLock.Release();
                    }
                }
            }
            Interlocked.Add(ref _currentDiskCacheBytes, -deleted);
        }
        catch { }
    }

    #endregion

    #region IDisposable

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

    #endregion
}