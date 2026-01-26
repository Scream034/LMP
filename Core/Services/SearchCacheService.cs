using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Models;

namespace LMP.Core.Services;

public class CachedSearchResult
{
    public string Query { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = [];
}

public class SearchCacheService
{
    private readonly string _cacheFolder;
    private readonly int _maxCacheFiles = 50;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory LRU cache для горячих запросов
    private readonly Dictionary<string, CachedSearchResult> _memoryCache = [];
    private readonly LinkedList<string> _lruOrder = new();
    private const int MaxMemoryCacheItems = 10;

    // Ленивый доступ к LibraryService (избегаем циклических зависимостей при DI)
    private LibraryService? _libService;
    private LibraryService LibService => _libService ??= Program.Services.GetRequiredService<LibraryService>();

    /// <summary>
    /// TTL из настроек пользователя (в минутах), по умолчанию 60
    /// </summary>
    private TimeSpan CacheTtl => TimeSpan.FromMinutes(
        LibService.Data.SearchCacheTtlMinutes > 0 
            ? LibService.Data.SearchCacheTtlMinutes 
            : 60);

    public SearchCacheService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "SearchCache");
        Directory.CreateDirectory(_cacheFolder);

        // Очистка старого кэша при старте
        _ = Task.Run(CleanupOldCacheAsync);
    }

    /// <summary>
    /// Получить из кэша (память → диск)
    /// </summary>
    public async Task<List<TrackInfo>?> GetAsync(string query, int minCount = 10)
    {
        var key = GetCacheKey(query);
        var ttl = CacheTtl; // Читаем TTL один раз для консистентности

        // 1. Проверяем память
        if (_memoryCache.TryGetValue(key, out var memResult))
        {
            var age = DateTime.UtcNow - memResult.CachedAt;
            
            if (age < ttl && memResult.Tracks.Count >= minCount)
            {
                Log.Info($"[SearchCache] Memory HIT for '{query}' ({memResult.Tracks.Count} tracks, age: {age.TotalMinutes:F0}min, ttl: {ttl.TotalMinutes}min)");
                TouchLru(key);
                return memResult.Tracks;
            }
            
            if (age >= ttl)
            {
                Log.Info($"[SearchCache] Memory EXPIRED for '{query}' (age: {age.TotalMinutes:F0}min > ttl: {ttl.TotalMinutes}min)");
                lock (_memoryCache)
                {
                    _memoryCache.Remove(key);
                    _lruOrder.Remove(key);
                }
            }
        }

        // 2. Проверяем диск
        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_cacheFolder, $"{key}.json");
            if (!File.Exists(filePath)) return null;

            var json = await File.ReadAllTextAsync(filePath);
            var cached = JsonSerializer.Deserialize<CachedSearchResult>(json);

            if (cached == null) return null;

            var age = DateTime.UtcNow - cached.CachedAt;

            // Проверяем TTL
            if (age > ttl)
            {
                Log.Info($"[SearchCache] Disk EXPIRED for '{query}' (age: {age.TotalMinutes:F0}min > ttl: {ttl.TotalMinutes}min)");
                File.Delete(filePath);
                return null;
            }

            if (cached.Tracks.Count < minCount)
            {
                Log.Info($"[SearchCache] Disk has only {cached.Tracks.Count} tracks, need {minCount}");
                return null;
            }

            Log.Info($"[SearchCache] Disk HIT for '{query}' ({cached.Tracks.Count} tracks, age: {age.TotalMinutes:F0}min)");

            // Добавляем в память
            AddToMemoryCache(key, cached);

            return cached.Tracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Read error: {ex.Message}");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Сохранить в кэш (память + диск)
    /// </summary>
    public async Task SetAsync(string query, List<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;

        var key = GetCacheKey(query);
        var cached = new CachedSearchResult
        {
            Query = query,
            CachedAt = DateTime.UtcNow,
            Tracks = tracks
        };

        // 1. В память
        AddToMemoryCache(key, cached);

        // 2. На диск (асинхронно)
        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_cacheFolder, $"{key}.json");
            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(filePath, json);
            Log.Info($"[SearchCache] Saved '{query}' to disk ({tracks.Count} tracks)");
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Write error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Получить частичные данные для быстрого отображения
    /// </summary>
    public async Task<List<TrackInfo>> GetPartialAsync(string query, int count)
    {
        var cached = await GetAsync(query, minCount: 1);
        return cached?.Take(count).ToList() ?? [];
    }

    private void AddToMemoryCache(string key, CachedSearchResult result)
    {
        lock (_memoryCache)
        {
            if (_memoryCache.ContainsKey(key))
            {
                _memoryCache[key] = result;
                TouchLru(key);
            }
            else
            {
                // Удаляем старые если превышен лимит
                while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
                {
                    var oldest = _lruOrder.Last!.Value;
                    _lruOrder.RemoveLast();
                    _memoryCache.Remove(oldest);
                }

                _memoryCache[key] = result;
                _lruOrder.AddFirst(key);
            }
        }
    }

    private void TouchLru(string key)
    {
        lock (_memoryCache)
        {
            _lruOrder.Remove(key);
            _lruOrder.AddFirst(key);
        }
    }

    private async Task CleanupOldCacheAsync()
    {
        try
        {
            var ttl = CacheTtl;
            var files = Directory.GetFiles(_cacheFolder, "*.json")
                .Select(static f => new FileInfo(f))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ToList();

            int deletedCount = 0;

            // Удаляем старые файлы (превышение лимита)
            foreach (var file in files.Skip(_maxCacheFiles))
            {
                file.Delete();
                deletedCount++;
                Log.Info($"[SearchCache] Deleted excess cache: {file.Name}");
            }

            // Удаляем просроченные по TTL
            foreach (var file in files.Take(_maxCacheFiles))
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > ttl)
                {
                    file.Delete();
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                Log.Info($"[SearchCache] Cleanup: deleted {deletedCount} files (ttl: {ttl.TotalMinutes}min)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Cleanup error: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private static string GetCacheKey(string query)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(query.ToLowerInvariant().Trim()));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Инвалидирует кэш для конкретного запроса
    /// </summary>
    public void InvalidateQuery(string query)
    {
        var key = GetCacheKey(query);

        lock (_memoryCache)
        {
            _memoryCache.Remove(key);
            _lruOrder.Remove(key);
        }

        try
        {
            var filePath = Path.Combine(_cacheFolder, $"{key}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log.Info($"[SearchCache] Invalidated '{query}'");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Invalidate error: {ex.Message}");
        }
    }

    public void ClearAll()
    {
        lock (_memoryCache)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }

        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder, "*.json"))
            {
                File.Delete(file);
            }
            Log.Info("[SearchCache] Cleared all cache");
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Clear error: {ex.Message}");
        }
    }

    /// <summary>
    /// Принудительная очистка просроченных записей
    /// Вызывается при изменении TTL в настройках
    /// </summary>
    public async Task CleanupExpiredAsync()
    {
        await CleanupOldCacheAsync();
        
        // Также чистим память
        var ttl = CacheTtl;
        var now = DateTime.UtcNow;
        
        lock (_memoryCache)
        {
            var expiredKeys = _memoryCache
                .Where(kv => now - kv.Value.CachedAt > ttl)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _memoryCache.Remove(key);
                _lruOrder.Remove(key);
            }

            if (expiredKeys.Count > 0)
            {
                Log.Info($"[SearchCache] Cleaned {expiredKeys.Count} expired memory entries");
            }
        }
    }

    /// <summary>
    /// Статистика кэша
    /// </summary>
    public (int MemoryItems, int DiskItems, long DiskSizeBytes, int TtlMinutes) GetStats()
    {
        int memCount = _memoryCache.Count;
        var files = Directory.GetFiles(_cacheFolder, "*.json");
        long size = files.Sum(static f => new FileInfo(f).Length);
        int ttl = (int)CacheTtl.TotalMinutes;
        return (memCount, files.Length, size, ttl);
    }
}

