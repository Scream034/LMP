using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Media.Imaging;
using LMP.Core.Models;

namespace LMP.Core.Services;

public enum ImageQuality
{
    Low = 120,
    Medium = 200,
    High = 400,
    Ultra = 800
}

/// <summary>
/// Ультра-оптимизированный сервис кэширования изображений.
/// Использует паттерн Direct-to-Disk и атомарные операции ФС для минимизации GC аллокаций.
/// 
/// <para><b>Оптимизации v2:</b></para>
/// <list type="bullet">
///   <item>FNV-1a хеш вместо SHA256 для ключей кэша (zero-alloc, ~20x быстрее)</item>
///   <item>O(1) LRU через Dictionary + LinkedList вместо O(N) Find()</item>
///   <item>Lazy&lt;Task&gt; для дедупликации загрузок без race condition</item>
///   <item>Устранён O(N*M) поиск в CleanupDiskCacheAsync</item>
/// </list>
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _downloadSemaphore = new(6);

    private readonly ConcurrentDictionary<string, RefCountedBitmap> _memoryCache = new();

    /// <summary>
    /// LRU: LinkedList хранит порядок использования, Dictionary обеспечивает O(1) доступ к узлам.
    /// </summary>
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new();
    private readonly Lock _lruLock = new();

    /// <summary>
    /// Дедупликация параллельных загрузок одного URL.
    /// Lazy гарантирует, что фабрика вызовется ровно один раз даже при конкурентном доступе.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _pendingLoads = new();

    private long _currentDiskCacheBytes;
    private long _currentMemoryCacheBytes;
    private bool _isDisposed;
    private int _loadCounter;
    private const int CleanupInterval = 50;

    private int MaxMemoryItems => _library.Settings.Storage.MaxBitmapCacheItems > 0
        ? _library.Settings.Storage.MaxBitmapCacheItems : 50;

    private long MaxMemoryBytes => MaxMemoryItems * 200 * 200 * 4;

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LMP/1.0" } }
        };

        if (!Directory.Exists(G.Folder.ImageCache))
            Directory.CreateDirectory(G.Folder.ImageCache);

        _ = Task.Run(InitializeDiskCacheAsync);
    }

    public async Task<Bitmap?> GetImageAsync(string url, ImageQuality quality = ImageQuality.Low, CancellationToken ct = default)
        => await GetImageAsync(url, (int)quality, ct);

    public async Task<Bitmap?> GetImageAsync(string url, int decodeWidth, CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        var memoryKey = GetMemoryCacheKey(url, decodeWidth);

        // 1. Быстрый путь: Memory Cache — O(1) lookup + O(1) LRU touch
        if (_memoryCache.TryGetValue(memoryKey, out var refCounted))
        {
            if (refCounted.AddRef())
            {
                TouchLru(memoryKey);
                return refCounted.Bitmap;
            }
            // RefCount упал до 0 — удаляем мёртвую запись
            _memoryCache.TryRemove(memoryKey, out _);
        }

        // 2. Дедупликация через Lazy<Task> — гарантирует один вызов фабрики
        //    даже при конкурентном GetOrAdd от нескольких потоков.
        var lazyTask = _pendingLoads.GetOrAdd(memoryKey,
            _ => new Lazy<Task<Bitmap?>>(() => LoadImageInternalAsync(url, memoryKey, decodeWidth, ct)));

        try
        {
            var result = await lazyTask.Value;

            // AddRef для вызывающего — LoadImageInternalAsync уже добавил 1 ref при вставке в кэш,
            // но каждый последующий потребитель из pending тоже должен получить свой ref.
            if (result != null && _memoryCache.TryGetValue(memoryKey, out refCounted))
                refCounted.AddRef();

            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingLoads.TryRemove(memoryKey, out _);

            if (Interlocked.Increment(ref _loadCounter) % CleanupInterval == 0)
                _ = Task.Run(PerformMaintenanceAsync, CancellationToken.None);
        }
    }

    /// <summary>
    /// Предзагружает изображения на диск без декодирования в оперативную память.
    /// Полезно для предварительного кэширования обложек (например, топ-10 результатов поиска).
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct()
            .Where(u => !File.Exists(Path.Combine(G.Folder.ImageCache, GetDiskCacheKey(u))))
            .Take(8)
            .Select(u => EnsureDiskCachedAsync(u, ct));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            /* Ошибки prefetch тихо игнорируются, так как это фоновая оптимизация */
        }
    }

    /// <summary>
    /// Скачивает файл на диск, если его там еще нет. В RAM ничего не задерживается.
    /// </summary>
    private async Task EnsureDiskCachedAsync(string url, CancellationToken ct)
    {
        var diskKey = GetDiskCacheKey(url);
        var diskPath = Path.Combine(G.Folder.ImageCache, diskKey);

        if (File.Exists(diskPath)) return;

        try
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                if (File.Exists(diskPath)) return;
                await DownloadDirectToDiskAsync(url, diskPath, ct);
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }
        catch
        {
            // Ошибки загрузки в фоне игнорируются
        }
    }

    private async Task<Bitmap?> LoadImageInternalAsync(string url, string memoryKey, int decodeWidth, CancellationToken ct)
    {
        var diskKey = GetDiskCacheKey(url);
        var diskPath = Path.Combine(G.Folder.ImageCache, diskKey);

        // Если файла нет на диске — качаем напрямую в файл
        if (!File.Exists(diskPath))
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                if (!File.Exists(diskPath))
                {
                    await DownloadDirectToDiskAsync(url, diskPath, ct);
                }
            }
            catch { return null; }
            finally { _downloadSemaphore.Release(); }
        }

        if (!File.Exists(diskPath)) return null;

        // Декодируем из файла силами Avalonia (C++ backend, zero C# alloc)
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(diskPath);
                return decodeWidth > 0
                    ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
                    : new Bitmap(stream);
            }
            catch (Exception ex)
            {
                Log.Debug($"[ImageCache] Decode failed: {ex.Message}");
                try { File.Delete(diskPath); } catch { }
                return null;
            }
        }, ct);

        if (bitmap != null && !ct.IsCancellationRequested)
        {
            AddToMemoryCache(memoryKey, bitmap);
            return bitmap;
        }

        return null;
    }

    /// <summary>
    /// Скачивание напрямую в файл без MemoryStream.
    /// Использует атомарное переименование (.tmp → final) для потокобезопасности.
    /// </summary>
    private async Task DownloadDirectToDiskAsync(string url, string finalPath, CancellationToken ct)
    {
        string tmpPath = finalPath + ".tmp";

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return;

            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await using (var networkStream = await response.Content.ReadAsStreamAsync(ct))
            {
                await networkStream.CopyToAsync(fs, ct);
            }

            File.Move(tmpPath, finalPath, true);
            Interlocked.Add(ref _currentDiskCacheBytes, new FileInfo(finalPath).Length);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Добавляет bitmap в memory cache с LRU eviction.
    /// O(1) вставка и поиск благодаря _lruIndex.
    /// </summary>
    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        var refCounted = new RefCountedBitmap(bitmap);
        long estimatedBytes = refCounted.EstimatedBytes;

        lock (_lruLock)
        {
            // Eviction: удаляем самые старые, пока не влезет
            while ((_memoryCache.Count >= MaxMemoryItems || _currentMemoryCacheBytes + estimatedBytes > MaxMemoryBytes)
                   && _lruOrder.Count > 0)
            {
                var oldestNode = _lruOrder.Last!;
                var oldestKey = oldestNode.Value;
                _lruOrder.RemoveLast();
                _lruIndex.Remove(oldestKey);

                if (_memoryCache.TryRemove(oldestKey, out var removed))
                {
                    Interlocked.Add(ref _currentMemoryCacheBytes, -removed.EstimatedBytes);
                    removed.Release();
                }
            }

            if (_memoryCache.TryAdd(key, refCounted))
            {
                var node = _lruOrder.AddFirst(key);
                _lruIndex[key] = node;
                Interlocked.Add(ref _currentMemoryCacheBytes, estimatedBytes);
            }
            else
            {
                refCounted.Release();
            }
        }
    }

    public void ReleaseBitmap(string url, int decodeWidth)
    {
        var key = GetMemoryCacheKey(url, decodeWidth);
        if (_memoryCache.TryGetValue(key, out var refCounted))
        {
            refCounted.Release();
        }
    }

    /// <summary>
    /// Перемещает ключ в начало LRU. O(1) благодаря _lruIndex.
    /// </summary>
    private void TouchLru(string key)
    {
        lock (_lruLock)
        {
            if (_lruIndex.TryGetValue(key, out var node))
            {
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(node);
            }
        }
    }

    private async Task PerformMaintenanceAsync()
    {
        var memInfo = GC.GetGCMemoryInfo();
        if (memInfo.MemoryLoadBytes > memInfo.HighMemoryLoadThresholdBytes * 0.85)
        {
            lock (_lruLock)
            {
                int toRemove = _memoryCache.Count / 2;
                for (int i = 0; i < toRemove && _lruOrder.Count > 0; i++)
                {
                    var oldestNode = _lruOrder.Last!;
                    var oldestKey = oldestNode.Value;
                    _lruOrder.RemoveLast();
                    _lruIndex.Remove(oldestKey);

                    if (_memoryCache.TryRemove(oldestKey, out var refCounted))
                    {
                        Interlocked.Add(ref _currentMemoryCacheBytes, -refCounted.EstimatedBytes);
                        refCounted.Release();
                    }
                }
            }
        }

        long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
        if (_currentDiskCacheBytes > limitBytes)
        {
            await CleanupDiskCacheAsync(limitBytes);
        }
    }

    /// <summary>
    /// Очистка дискового кэша с учётом времени последнего доступа.
    /// Файлы моложе 5 минут не удаляются (могут быть в memory cache).
    /// </summary>
    private async Task CleanupDiskCacheAsync(long limitBytes)
    {
        await Task.Run(() =>
        {
            try
            {
                var files = new DirectoryInfo(G.Folder.ImageCache).GetFiles()
                    .Where(f => !f.Extension.EndsWith(".tmp"))
                    .OrderBy(f => f.LastAccessTimeUtc)
                    .ToList();

                var cutoff = DateTime.UtcNow.AddMinutes(-5);
                long targetSize = (long)(limitBytes * 0.7);
                long deletedBytes = 0;

                foreach (var file in files)
                {
                    if (_currentDiskCacheBytes - deletedBytes <= targetSize) break;

                    // Не удаляем недавно использованные файлы — они могут быть в memory cache
                    if (file.LastAccessTimeUtc > cutoff) continue;

                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deletedBytes += size;
                    }
                    catch { }
                }

                if (deletedBytes > 0)
                    Interlocked.Add(ref _currentDiskCacheBytes, -deletedBytes);
            }
            catch { }
        });
    }

    public void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            foreach (var r in _memoryCache.Values) r.Release();
            _memoryCache.Clear();
            _lruOrder.Clear();
            _lruIndex.Clear();
            _currentMemoryCacheBytes = 0;
        }
    }

    public async Task ClearDiskCacheAsync()
    {
        ClearMemoryCache();
        await Task.Run(() =>
        {
            foreach (var f in Directory.GetFiles(G.Folder.ImageCache))
            {
                try { File.Delete(f); } catch { }
            }
            _currentDiskCacheBytes = 0;
        });
    }

    private async Task InitializeDiskCacheAsync()
    {
        try
        {
            long total = new DirectoryInfo(G.Folder.ImageCache)
                .EnumerateFiles()
                .Sum(f => f.Length);
            Interlocked.Exchange(ref _currentDiskCacheBytes, total);
        }
        catch { }
    }

    #region Fast Hash (FNV-1a 64-bit)

    /// <summary>
    /// FNV-1a 64-bit хеш для строки. Zero-alloc, ~20x быстрее SHA256.
    /// Для кэш-ключей криптостойкость не нужна — важна скорость и низкая коллизионность.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeFnv1aHash(ReadOnlySpan<char> data)
    {
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        ulong hash = FnvOffsetBasis;

        foreach (char c in data)
        {
            // Обрабатываем оба байта char для корректной работы с Unicode
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// Disk cache key: hash(url) → 16 hex символов.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetDiskCacheKey(string url)
    {
        var hash = ComputeFnv1aHash(url.AsSpan());
        return hash.ToString("X16");
    }

    /// <summary>
    /// Memory cache key: hash(url + "_" + normalizedWidth) → 16 hex символов.
    /// Нормализация ширины уменьшает количество уникальных ключей.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetMemoryCacheKey(string url, int decodeWidth)
    {
        var normalizedWidth = decodeWidth switch { <= 120 => 120, <= 200 => 200, <= 400 => 400, _ => 800 };

        // Вычисляем хеш инкрементально, без создания промежуточной строки
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        ulong hash = FnvOffsetBasis;

        // Хешируем URL
        foreach (char c in url)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        // Хешируем разделитель "_"
        hash ^= (byte)'_';
        hash *= FnvPrime;

        // Хешируем ширину (3-4 цифры)
        Span<char> widthChars = stackalloc char[4];
        normalizedWidth.TryFormat(widthChars, out int widthLen);
        for (int i = 0; i < widthLen; i++)
        {
            hash ^= (byte)widthChars[i];
            hash *= FnvPrime;
        }

        return hash.ToString("X16");
    }

    #endregion

    public (int MemoryItems, long MemoryMb, int DiskFiles, long DiskMb) GetStats() =>
        (_memoryCache.Count, _currentMemoryCacheBytes / 1024 / 1024, 0, _currentDiskCacheBytes / 1024 / 1024);

    public void EnforceLimits() => _ = PerformMaintenanceAsync();

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        ClearMemoryCache();
        _downloadSemaphore.Dispose();
        _http.Dispose();
    }
}