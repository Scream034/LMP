// Services/SearchCacheService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

public class CachedSearchResult
{
    public string Query { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = new();
}

public class SearchCacheService
{
    private readonly string _cacheFolder;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromHours(2); // Кэш живёт 2 часа
    private readonly int _maxCacheFiles = 50; // Максимум файлов кэша
    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory LRU cache для горячих запросов
    private readonly Dictionary<string, CachedSearchResult> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private const int MaxMemoryCacheItems = 10;

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

        // 1. Проверяем память
        if (_memoryCache.TryGetValue(key, out var memResult))
        {
            if (DateTime.UtcNow - memResult.CachedAt < _cacheTtl && memResult.Tracks.Count >= minCount)
            {
                Debug.WriteLine($"[Cache] Memory HIT for '{query}' ({memResult.Tracks.Count} tracks)");
                TouchLru(key);
                return memResult.Tracks;
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

            // Проверяем TTL
            if (DateTime.UtcNow - cached.CachedAt > _cacheTtl)
            {
                Debug.WriteLine($"[Cache] Disk EXPIRED for '{query}'");
                File.Delete(filePath);
                return null;
            }

            if (cached.Tracks.Count < minCount)
            {
                Debug.WriteLine($"[Cache] Disk has only {cached.Tracks.Count} tracks, need {minCount}");
                return null;
            }

            Debug.WriteLine($"[Cache] Disk HIT for '{query}' ({cached.Tracks.Count} tracks)");

            // Добавляем в память
            AddToMemoryCache(key, cached);

            return cached.Tracks;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Cache] Read error: {ex.Message}");
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
            Debug.WriteLine($"[Cache] Saved '{query}' to disk ({tracks.Count} tracks)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Cache] Write error: {ex.Message}");
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
        return cached?.Take(count).ToList() ?? new List<TrackInfo>();
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
            var files = Directory.GetFiles(_cacheFolder, "*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            // Удаляем старые файлы
            foreach (var file in files.Skip(_maxCacheFiles))
            {
                file.Delete();
                Debug.WriteLine($"[Cache] Deleted old cache: {file.Name}");
            }

            // Удаляем просроченные
            foreach (var file in files.Take(_maxCacheFiles))
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > _cacheTtl)
                {
                    file.Delete();
                }
            }
        }
        catch { }

        await Task.CompletedTask;
    }

    private static string GetCacheKey(string query)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(query.ToLowerInvariant().Trim()));
        return Convert.ToHexString(bytes)[..16]; // Первые 16 символов
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
        }
        catch { }
    }

    /// <summary>
    /// Статистика кэша
    /// </summary>
    public (int MemoryItems, int DiskItems, long DiskSizeBytes) GetStats()
    {
        int memCount = _memoryCache.Count;
        var files = Directory.GetFiles(_cacheFolder, "*.json");
        long size = files.Sum(f => new FileInfo(f).Length);
        return (memCount, files.Length, size);
    }
}