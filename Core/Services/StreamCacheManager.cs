using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyLiteMusicPlayer.Core.Models;

namespace MyLiteMusicPlayer.Core.Services;

public class StreamCacheMetadata
{
    public string TrackId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public long ContentLength { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public string RangesJson { get; set; } = "[]";
}

public class StreamCacheManager : IDisposable
{
    private readonly string _cacheFolder;
    private readonly long _maxCacheSizeBytes;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    public StreamCacheManager(long maxCacheSizeMb = 2048)
    {
        _maxCacheSizeBytes = maxCacheSizeMb * 1024 * 1024;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "StreamCache");
        Directory.CreateDirectory(_cacheFolder);
        _ = Task.Run(CleanupOldCacheAsync);
    }

    public string GetCachePath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(_cacheFolder, $"{safeId}.cache");
    }

    public string GetMetaPath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(_cacheFolder, $"{safeId}.meta");
    }

    /// <summary>
    /// Попытка получить метаданные без создания новых.
    /// Используется для проверки наличия кэша перед сетевыми запросами.
    /// </summary>
    public StreamCacheMetadata? TryGetMetadata(string trackId)
    {
        var metaPath = GetMetaPath(trackId);
        if (!File.Exists(metaPath)) return null;

        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<StreamCacheMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    public StreamCacheMetadata LoadOrCreateMetadata(string trackId, string url, long contentLength)
    {
        var meta = TryGetMetadata(trackId);
        
        if (meta != null && meta.ContentLength == contentLength)
        {
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
            return meta;
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

        // Сброс кэша при несовпадении размеров
        var cachePath = GetCachePath(trackId);
        if (File.Exists(cachePath)) try { File.Delete(cachePath); } catch { }

        SaveMetadata(trackId, newMeta);
        return newMeta;
    }

    public void SaveMetadata(string trackId, StreamCacheMetadata meta)
    {
        try
        {
            var metaPath = GetMetaPath(trackId);
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch (Exception ex) { Log.Info($"Failed to save metadata: {ex.Message}"); }
    }

    public void UpdateRanges(string trackId, RangeMap ranges)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null)
        {
            meta.RangesJson = ranges.Serialize();
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
        }
    }

    public RangeMap LoadRanges(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        return meta != null ? RangeMap.Deserialize(meta.RangesJson) : new RangeMap();
    }

    public bool IsFullyCached(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        if (meta == null) return false;
        
        if (!File.Exists(GetCachePath(trackId))) return false;

        var ranges = RangeMap.Deserialize(meta.RangesJson);
        return ranges.IsFullyDownloaded(meta.ContentLength);
    }

    private async Task CleanupOldCacheAsync()
    {
        if (!await _cleanupLock.WaitAsync(0)) return;

        try
        {
            var files = Directory.GetFiles(_cacheFolder, "*.cache")
                .Select(f => new FileInfo(f))
                .ToList();

            long totalSize = files.Sum(f => f.Length);
            
            if (totalSize <= _maxCacheSizeBytes) return;

            Log.Info($"Cache size {totalSize / 1024 / 1024}MB exceeds limit, cleaning...");

            var metaFiles = files
                .Select(f => new
                {
                    CacheFile = f,
                    MetaFile = new FileInfo(Path.ChangeExtension(f.FullName, ".meta")),
                    LastAccess = GetLastAccessTime(Path.ChangeExtension(f.FullName, ".meta"))
                })
                .OrderBy(x => x.LastAccess)
                .ToList();

            long targetSize = _maxCacheSizeBytes * 70 / 100;
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
                }
                catch { }
            }
            Log.Info($"Cleaned {deleted / 1024 / 1024}MB");
        }
        finally { _cleanupLock.Release(); }
    }

    private static DateTime GetLastAccessTime(string metaPath)
    {
        try
        {
            if (File.Exists(metaPath))
            {
                // Читаем только начало файла для скорости или используем FileInfo CreationTime как фоллбек, 
                // но здесь у нас LastAccessedAt внутри JSON.
                // Для простоты читаем весь, это редкая операция (при очистке)
                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("LastAccessedAt", out var prop) && prop.TryGetDateTime(out var dt))
                    return dt;
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

    public (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder, "*.cache");
            long size = files.Sum(f => new FileInfo(f).Length);
            return (files.Length, size / 1024 / 1024);
        }
        catch { return (0, 0); }
    }

    public void ClearAll()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder)) File.Delete(file);
            Log.Info("All cache cleared");
        }
        catch { }
    }

    public void Dispose() => _cleanupLock.Dispose();
}