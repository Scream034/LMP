using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Метаданные кэшированного потока
/// </summary>
public class StreamCacheMetadata
{
    public string TrackId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public long ContentLength { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public string RangesJson { get; set; } = "[]";
}

/// <summary>
/// Управляет файлами кэша стримов.
/// Каждый трек имеет:
/// - .cache файл (сырые аудио-данные)
/// - .meta файл (метаданные + информация о скачанных диапазонах)
/// </summary>
public class StreamCacheManager : IDisposable
{
    private readonly string _cacheFolder;
    private readonly long _maxCacheSizeBytes;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    public StreamCacheManager(long maxCacheSizeMb = 2048) // 2GB по умолчанию
    {
        _maxCacheSizeBytes = maxCacheSizeMb * 1024 * 1024;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "StreamCache");
        Directory.CreateDirectory(_cacheFolder);

        // Фоновая очистка при старте
        _ = Task.Run(CleanupOldCacheAsync);
    }

    /// <summary>
    /// Получает путь к кэш-файлу для трека
    /// </summary>
    public string GetCachePath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(_cacheFolder, $"{safeId}.cache");
    }

    /// <summary>
    /// Получает путь к файлу метаданных
    /// </summary>
    public string GetMetaPath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(_cacheFolder, $"{safeId}.meta");
    }

    /// <summary>
    /// Загружает или создаёт метаданные для трека
    /// </summary>
    public StreamCacheMetadata LoadOrCreateMetadata(string trackId, string url, long contentLength)
    {
        var metaPath = GetMetaPath(trackId);
        
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<StreamCacheMetadata>(json);
                
                if (meta != null && meta.ContentLength == contentLength)
                {
                    meta.LastAccessedAt = DateTime.UtcNow;
                    SaveMetadata(trackId, meta);
                    return meta;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CacheManager] Failed to load metadata: {ex.Message}");
            }
        }

        // Создаём новые метаданные
        var newMeta = new StreamCacheMetadata
        {
            TrackId = trackId,
            SourceUrl = url,
            ContentLength = contentLength,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            RangesJson = "[]"
        };

        // Удаляем старый кэш-файл если существует (размер изменился)
        var cachePath = GetCachePath(trackId);
        if (File.Exists(cachePath))
        {
            try { File.Delete(cachePath); } catch { }
        }

        SaveMetadata(trackId, newMeta);
        return newMeta;
    }

    /// <summary>
    /// Сохраняет метаданные
    /// </summary>
    public void SaveMetadata(string trackId, StreamCacheMetadata meta)
    {
        try
        {
            var metaPath = GetMetaPath(trackId);
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CacheManager] Failed to save metadata: {ex.Message}");
        }
    }

    /// <summary>
    /// Обновляет RangeMap в метаданных
    /// </summary>
    public void UpdateRanges(string trackId, RangeMap ranges)
    {
        var metaPath = GetMetaPath(trackId);
        if (!File.Exists(metaPath)) return;

        try
        {
            var json = File.ReadAllText(metaPath);
            var meta = JsonSerializer.Deserialize<StreamCacheMetadata>(json);
            if (meta != null)
            {
                meta.RangesJson = ranges.Serialize();
                meta.LastAccessedAt = DateTime.UtcNow;
                SaveMetadata(trackId, meta);
            }
        }
        catch { }
    }

    /// <summary>
    /// Загружает RangeMap из метаданных
    /// </summary>
    public RangeMap LoadRanges(string trackId)
    {
        var metaPath = GetMetaPath(trackId);
        if (!File.Exists(metaPath)) return new RangeMap();

        try
        {
            var json = File.ReadAllText(metaPath);
            var meta = JsonSerializer.Deserialize<StreamCacheMetadata>(json);
            if (meta != null)
            {
                return RangeMap.Deserialize(meta.RangesJson);
            }
        }
        catch { }

        return new RangeMap();
    }

    /// <summary>
    /// Проверяет, полностью ли скачан трек
    /// </summary>
    public bool IsFullyCached(string trackId)
    {
        var metaPath = GetMetaPath(trackId);
        var cachePath = GetCachePath(trackId);

        if (!File.Exists(metaPath) || !File.Exists(cachePath)) 
            return false;

        try
        {
            var json = File.ReadAllText(metaPath);
            var meta = JsonSerializer.Deserialize<StreamCacheMetadata>(json);
            if (meta == null) return false;

            var ranges = RangeMap.Deserialize(meta.RangesJson);
            return ranges.IsFullyDownloaded(meta.ContentLength);
        }
        catch { return false; }
    }

    /// <summary>
    /// Очистка старого кэша
    /// </summary>
    private async Task CleanupOldCacheAsync()
    {
        if (!await _cleanupLock.WaitAsync(0)) return;

        try
        {
            var files = Directory.GetFiles(_cacheFolder, "*.cache")
                .Select(f => new FileInfo(f))
                .ToList();

            long totalSize = files.Sum(f => f.Length);
            
            if (totalSize <= _maxCacheSizeBytes)
                return;

            Debug.WriteLine($"[CacheManager] Cache size {totalSize / 1024 / 1024}MB exceeds limit, cleaning...");

            // Сортируем по времени последнего доступа
            var metaFiles = files
                .Select(f => new
                {
                    CacheFile = f,
                    MetaFile = new FileInfo(Path.ChangeExtension(f.FullName, ".meta")),
                    LastAccess = GetLastAccessTime(Path.ChangeExtension(f.FullName, ".meta"))
                })
                .OrderBy(x => x.LastAccess)
                .ToList();

            long targetSize = _maxCacheSizeBytes * 70 / 100; // Удаляем до 70%
            long deleted = 0;

            foreach (var item in metaFiles)
            {
                if (totalSize - deleted <= targetSize) break;

                try
                {
                    var size = item.CacheFile.Length;
                    item.CacheFile.Delete();
                    if (item.MetaFile.Exists) item.MetaFile.Delete();
                    deleted += size;
                    Debug.WriteLine($"[CacheManager] Deleted {item.CacheFile.Name}");
                }
                catch { }
            }

            Debug.WriteLine($"[CacheManager] Cleaned {deleted / 1024 / 1024}MB");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private DateTime GetLastAccessTime(string metaPath)
    {
        try
        {
            if (File.Exists(metaPath))
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<StreamCacheMetadata>(json);
                return meta?.LastAccessedAt ?? DateTime.MinValue;
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    private static string GetSafeFileName(string trackId)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(trackId));
        return Convert.ToHexString(bytes)[..32];
    }

    /// <summary>
    /// Статистика кэша
    /// </summary>
    public (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder, "*.cache");
            long size = files.Sum(f => new FileInfo(f).Length);
            return (files.Length, size / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Очистить весь кэш
    /// </summary>
    public void ClearAll()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder))
            {
                File.Delete(file);
            }
            Debug.WriteLine("[CacheManager] All cache cleared");
        }
        catch { }
    }

    public void Dispose()
    {
        _cleanupLock.Dispose();
    }
}