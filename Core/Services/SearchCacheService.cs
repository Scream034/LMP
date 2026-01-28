using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Models;
using LMP.Core.Youtube.Search;

namespace LMP.Core.Services;

public class CachedSearchResult
{
    public string Query { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = [];
}

public class SearchCacheService
{
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
        // Очистка старого кэша при старте
        _ = Task.Run(CleanupOldCacheAsync);
    }

    /// <summary>
    /// Получить из кэша (память → диск)
    /// </summary>
    public async Task<List<TrackInfo>?> GetAsync(string query, SearchFilter filter, int minCount = 10)
    {
        var key = GetCacheKey(query, filter);
        var ttl = CacheTtl;

        // 1. Memory Check
        if (_memoryCache.TryGetValue(key, out var memResult))
        {
            var age = DateTime.UtcNow - memResult.CachedAt;
            if (age < ttl && memResult.Tracks.Count >= minCount)
            {
                TouchLru(key);
                return memResult.Tracks;
            }
            if (age >= ttl) RemoveFromMemory(key);
        }

        // 2. Disk Check
        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(G.Folder.SearchCache, $"{key}.json");
            if (!File.Exists(filePath)) return null;

            var json = await File.ReadAllTextAsync(filePath);
            var cached = JsonSerializer.Deserialize<CachedSearchResult>(json);

            if (cached == null) return null;

            var age = DateTime.UtcNow - cached.CachedAt;
            if (age > ttl)
            {
                File.Delete(filePath);
                return null;
            }

            // Валидация фильтра (на всякий случай, если кэш был без фильтра)
            if (cached.Tracks.Count < minCount) return null;

            AddToMemoryCache(key, cached);
            return cached.Tracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Read error: {ex.Message}");
            return null;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Сохранить в кэш (память + диск)
    /// </summary>
    public async Task SetAsync(string query, SearchFilter filter, List<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;

        var key = GetCacheKey(query, filter);
        var cached = new CachedSearchResult
        {
            Query = query,
            CachedAt = DateTime.UtcNow,
            Tracks = tracks
        };

        AddToMemoryCache(key, cached);

        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(G.Folder.SearchCache, $"{key}.json");
            var json = JsonSerializer.Serialize(cached, G.Json.Compact);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch { /* ignore write errors */ }
        finally { _lock.Release(); }
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
            var files = Directory.GetFiles(G.Folder.SearchCache, "*.json")
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

    private static string GetCacheKey(string query, SearchFilter filter)
    {
        var rawKey = $"{query.ToLowerInvariant().Trim()}|{filter}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Инвалидирует кэш для конкретного запроса
    /// </summary>
    public void InvalidateQuery(string query, SearchFilter filter)
    {
        var key = GetCacheKey(query, filter);
        RemoveFromMemory(key);
        try
        {
            var filePath = Path.Combine(G.Folder.SearchCache, $"{key}.json");
            if (File.Exists(filePath)) File.Delete(filePath);
        }
        catch { }
    }

    private void RemoveFromMemory(string key)
    {
        lock (_memoryCache)
        {
            _memoryCache.Remove(key);
            _lruOrder.Remove(key);
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
            foreach (var file in Directory.GetFiles(G.Folder.SearchCache, "*.json"))
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
        var files = Directory.GetFiles(G.Folder.SearchCache, "*.json");
        long size = files.Sum(static f => new FileInfo(f).Length);
        int ttl = (int)CacheTtl.TotalMinutes;
        return (memCount, files.Length, size, ttl);
    }
}

