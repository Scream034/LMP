
// === ФАЙЛ: Core/Services/StreamCacheManager.cs ===
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LMP.Core.Models;

namespace LMP.Core.Services;

public class StreamCacheMetadata
{
    public string TrackId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public long ContentLength { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public string RangesJson { get; set; } = "[]";
    public string Codec { get; set; } = "";
    public int Bitrate { get; set; }
    public string Container { get; set; } = "";
}

public class StreamCacheManager : IDisposable
{
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    public StreamCacheManager(LibraryService library)
    {
        _library = library;
        _ = Task.Run(CleanupOldCacheAsync);
    }

    public static string GetCachePath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.cache");
    }

    public static string GetMetaPath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.meta");
    }

    public StreamCacheMetadata? TryGetMetadata(string trackId)
    {
        var metaPath = GetMetaPath(trackId);
        if (!File.Exists(metaPath)) return null;
        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<StreamCacheMetadata>(json);
        }
        catch { return null; }
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

        var newMeta = new StreamCacheMetadata
        {
            TrackId = trackId,
            SourceUrl = url,
            ContentLength = contentLength,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            RangesJson = "[]"
        };

        var cachePath = GetCachePath(trackId);
        if (File.Exists(cachePath)) try { File.Delete(cachePath); } catch { }

        SaveMetadata(trackId, newMeta);
        return newMeta;
    }

    public static void SaveMetadata(string trackId, StreamCacheMetadata meta)
    {
        try
        {
            var metaPath = GetMetaPath(trackId);
            var json = JsonSerializer.Serialize(meta, G.Json.Beautiful);
            File.WriteAllText(metaPath, json);
        }
        catch { }
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

    public void UpdateStreamInfo(string trackId, string codec, int bitrate, string container)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null)
        {
            meta.Codec = codec;
            meta.Bitrate = bitrate;
            meta.Container = container;
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
    
    public async Task ClearAllAsync()
    {
        await _cleanupLock.WaitAsync();
        try
        {
            foreach (var file in Directory.GetFiles(G.Folder.StreamCache))
            {
                try { File.Delete(file); } catch { }
            }
            Log.Info("All stream cache cleared");
        }
        finally { _cleanupLock.Release(); }
    }
    
    public static (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.StreamCache, "*.cache");
            long size = files.Sum(static f => new FileInfo(f).Length);
            return (files.Length, size / 1024 / 1024);
        }
        catch { return (0, 0); }
    }

    private async Task CleanupOldCacheAsync()
    {
        if (!await _cleanupLock.WaitAsync(0)) return;

        try
        {
            var files = Directory.GetFiles(G.Folder.StreamCache, "*.cache")
                .Select(static f => new FileInfo(f))
                .ToList();

            long totalSize = files.Sum(static f => f.Length);
            long maxCacheBytes = (long)_library.Data.Storage.AudioCacheLimitMb * 1024 * 1024;

            if (totalSize <= maxCacheBytes) return;

            Log.Info($"Stream cache size {totalSize / 1024 / 1024}MB exceeds limit {maxCacheBytes / 1024 / 1024}MB, cleaning...");

            var metaFiles = files
                .Select(static f => new
                {
                    CacheFile = f,
                    MetaFile = new FileInfo(Path.ChangeExtension(f.FullName, ".meta")),
                    LastAccess = GetLastAccessTime(Path.ChangeExtension(f.FullName, ".meta"))
                })
                .OrderBy(static x => x.LastAccess)
                .ToList();

            long targetSize = maxCacheBytes * 70 / 100;
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
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(trackId));
        return Convert.ToHexString(bytes)[..32];
    }

    public void Dispose() => _cleanupLock.Dispose();
}