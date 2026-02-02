using System.Collections.Concurrent;
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
    private readonly SemaphoreSlim _promoteLock = new(1, 1);

    private readonly ConcurrentDictionary<string, bool> _fullyCachedCache = new();
    private readonly ConcurrentDictionary<string, bool> _promotedCache = new();

    /// <summary>
    /// Событие, вызываемое при завершении кэширования формата.
    /// Параметры: trackId, container, bitrate, isDownloaded
    /// </summary>
    public event Action<string, string, int, bool>? OnFormatCached;

    public StreamCacheManager(LibraryService library)
    {
        _library = library;
        _ = Task.Run(CleanupOldCacheAsync);
    }

    public static string GetCachePath(string cacheId)
    {
        var safeId = GetSafeFileName(cacheId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.cache");
    }

    public static string GetMetaPath(string cacheId)
    {
        var safeId = GetSafeFileName(cacheId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.meta");
    }

    public static StreamCacheMetadata? TryGetMetadata(string cacheId)
    {
        var metaPath = GetMetaPath(cacheId);
        if (!File.Exists(metaPath)) return null;
        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize(json, AppJsonContext.DefaultCompact.StreamCacheMetadata);
        }
        catch { return null; }
    }

    public static StreamCacheMetadata LoadOrCreateMetadata(string cacheId, string url, long contentLength)
    {
        var meta = TryGetMetadata(cacheId);

        if (meta != null && meta.ContentLength == contentLength)
        {
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(cacheId, meta);
            return meta;
        }

        var newMeta = new StreamCacheMetadata
        {
            TrackId = cacheId,
            SourceUrl = url,
            ContentLength = contentLength,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            RangesJson = "[]"
        };

        var cachePath = GetCachePath(cacheId);
        if (File.Exists(cachePath)) try { File.Delete(cachePath); } catch { }

        SaveMetadata(cacheId, newMeta);
        return newMeta;
    }

    public static void SaveMetadata(string cacheId, StreamCacheMetadata meta)
    {
        try
        {
            var metaPath = GetMetaPath(cacheId);
            var json = JsonSerializer.Serialize(meta, AppJsonContext.DefaultCompact.StreamCacheMetadata);
            File.WriteAllText(metaPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Fire-and-forget версия promote с уведомлением.
    /// Используется из всех мест, где нужно промоутить кэш.
    /// </summary>
    public void TriggerPromoteWithNotification(string cacheId, string? originalTrackId = null)
    {
        string trackId = originalTrackId ?? ExtractTrackId(cacheId);
        
        var meta = TryGetMetadata(cacheId);
        string container = meta?.Container ?? "";
        int bitrate = meta?.Bitrate ?? 0;
        
        _fullyCachedCache[cacheId] = true;
        
        Task.Run(async () =>
        {
            bool success = await PromoteCacheToDownloadInternalAsync(cacheId, trackId);
            NotifyFormatCached(trackId, container, bitrate, success);
        });
    }

    /// <summary>
    /// Внутренний метод promote. Возвращает true если файл успешно скопирован.
    /// </summary>
    private async Task<bool> PromoteCacheToDownloadInternalAsync(string cacheId, string trackId)
    {
        if (_promotedCache.TryGetValue(cacheId, out bool done) && done)
        {
            Log.Debug($"[CachePromote] {cacheId} already promoted, skipping.");
            return false;
        }

        if (!await _promoteLock.WaitAsync(100))
            return false;

        try
        {
            if (_promotedCache.TryGetValue(cacheId, out done) && done)
                return false;

            var track = await _library.GetTrackAsync(trackId);
            if (track == null)
            {
                Log.Warn($"[CachePromote] Track {trackId} not found in library");
                return false;
            }

            var meta = TryGetMetadata(cacheId);
            if (meta == null)
            {
                Log.Warn($"[CachePromote] No metadata for cache {cacheId}");
                return false;
            }

            var cachePath = GetCachePath(cacheId);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[CachePromote] Cache file not found: {cachePath}");
                return false;
            }

            var ranges = RangeMap.Deserialize(meta.RangesJson);
            if (!ranges.IsFullyDownloaded(meta.ContentLength))
            {
                Log.Warn($"[CachePromote] Cache {cacheId} is not fully downloaded");
                return false;
            }

            string ext = !string.IsNullOrEmpty(meta.Container) ? meta.Container : "m4a";
            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string destPath = Path.Combine(G.Folder.Downloads, safeName);

            if (File.Exists(destPath))
            {
                var info = new FileInfo(destPath);
                if (info.Length == meta.ContentLength)
                {
                    await UpdateTrackStatus(track, destPath, meta);
                    _promotedCache[cacheId] = true;
                    Log.Info($"[CachePromote] {trackId} already exists: {safeName}");
                    return true;
                }
                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{meta.Bitrate}kbps.{ext}");
            }

            Log.Info($"[CachePromote] Promoting {trackId} ({cacheId}) to {Path.GetFileName(destPath)}...");

            File.Copy(cachePath, destPath, overwrite: true);

            await UpdateTrackStatus(track, destPath, meta);
            _promotedCache[cacheId] = true;

            Log.Info($"[CachePromote] Success: {Path.GetFileName(destPath)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachePromote] Failed for {trackId}: {ex.Message}");
            return false;
        }
        finally
        {
            _promoteLock.Release();
        }
    }

    /// <summary>
    /// Проверяет, скачан ли конкретный формат для трека.
    /// </summary>
    public bool IsFormatCached(string trackId, string container, int bitrate)
    {
        if (IsFullyCached(trackId))
        {
            var meta = TryGetMetadata(trackId);
            if (meta != null &&
                string.Equals(meta.Container, container, StringComparison.OrdinalIgnoreCase) &&
                meta.Bitrate == bitrate)
            {
                return true;
            }
        }

        string cacheId = $"{trackId}_{container}_{bitrate}";
        return IsFullyCached(cacheId);
    }

    public (string Container, int Bitrate)? GetDownloadedFormat(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null && !string.IsNullOrEmpty(meta.Container))
        {
            var ranges = RangeMap.Deserialize(meta.RangesJson);
            if (ranges.IsFullyDownloaded(meta.ContentLength))
                return (meta.Container, meta.Bitrate);
        }
        return null;
    }

    public string? GetDownloadedContainer(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null)
        {
            var ranges = RangeMap.Deserialize(meta.RangesJson);
            if (ranges.IsFullyDownloaded(meta.ContentLength))
                return meta.Container;
        }
        return null;
    }

    public List<(string Container, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(string, int)>();

        var baseMeta = TryGetMetadata(trackId);
        if (baseMeta != null && !string.IsNullOrEmpty(baseMeta.Container))
        {
            var ranges = RangeMap.Deserialize(baseMeta.RangesJson);
            if (ranges.IsFullyDownloaded(baseMeta.ContentLength))
            {
                result.Add((baseMeta.Container, baseMeta.Bitrate));
            }
        }

        try
        {
            var cacheDir = G.Folder.StreamCache;
            if (!Directory.Exists(cacheDir)) return result;

            var knownContainers = new[] { "webm", "mp4", "m4a", "opus", "ogg" };
            var knownBitrates = new[] { 48, 50, 56, 57, 64, 67, 96, 127, 128, 131, 136, 137, 138, 145, 146, 155, 157, 159, 168, 192, 256, 320 };

            foreach (var container in knownContainers)
            {
                foreach (var bitrate in knownBitrates)
                {
                    string cacheId = $"{trackId}_{container}_{bitrate}";
                    var meta = TryGetMetadata(cacheId);
                    if (meta != null)
                    {
                        var ranges = RangeMap.Deserialize(meta.RangesJson);
                        if (ranges.IsFullyDownloaded(meta.ContentLength))
                        {
                            result.Add((container, bitrate));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[StreamCache] Error scanning cached formats: {ex.Message}");
        }

        return result;
    }

    private static string ExtractTrackId(string cacheId)
    {
        var parts = cacheId.Split('_');
        if (parts.Length >= 3)
        {
            var lastPart = parts[^1];
            var secondLast = parts[^2];

            if (int.TryParse(lastPart, out _) && IsKnownContainer(secondLast))
            {
                return string.Join('_', parts[..^2]);
            }
        }
        return cacheId;
    }

    private static bool IsKnownContainer(string s) =>
        s is "m4a" or "opus" or "webm" or "mp3" or "ogg" or "aac" or "flac" or "mp4";

    private async Task UpdateTrackStatus(TrackInfo track, string path, StreamCacheMetadata meta)
    {
        track.IsDownloaded = true;
        track.LocalPath = path;

        if (!string.IsNullOrEmpty(meta.Container))
            track.PreferredContainer = meta.Container;
        if (meta.Bitrate > 0)
            track.PreferredBitrate = meta.Bitrate;

        await _library.AddOrUpdateTrackAsync(track);
    }

    private void NotifyFormatCached(string trackId, string container, int bitrate, bool isDownloaded)
    {
        try
        {
            Log.Debug($"[StreamCache] NotifyFormatCached: {trackId} {container}/{bitrate}kbps downloaded={isDownloaded}");
            
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                OnFormatCached?.Invoke(trackId, container, bitrate, isDownloaded);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    OnFormatCached?.Invoke(trackId, container, bitrate, isDownloaded));
        }
        catch (Exception ex)
        {
            Log.Warn($"[StreamCache] NotifyFormatCached error: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    public static void UpdateRanges(string cacheId, RangeMap ranges)
    {
        var meta = TryGetMetadata(cacheId);
        if (meta != null)
        {
            meta.RangesJson = ranges.Serialize();
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(cacheId, meta);
        }
    }

    public static void UpdateStreamInfo(string cacheId, string codec, int bitrate, string container)
    {
        var meta = TryGetMetadata(cacheId);
        if (meta != null)
        {
            meta.Codec = codec;
            meta.Bitrate = bitrate;
            meta.Container = container;
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(cacheId, meta);
        }
    }

    public static RangeMap LoadRanges(string cacheId)
    {
        var meta = TryGetMetadata(cacheId);
        return meta != null ? RangeMap.Deserialize(meta.RangesJson) : new RangeMap();
    }

    public bool IsFullyCachedFast(string cacheId)
    {
        return _fullyCachedCache.TryGetValue(cacheId, out bool cached) && cached;
    }

    public bool IsFullyCached(string cacheId)
    {
        if (_fullyCachedCache.TryGetValue(cacheId, out bool cached) && cached)
            return true;

        var metaPath = GetMetaPath(cacheId);
        if (!File.Exists(metaPath)) return false;

        var meta = TryGetMetadata(cacheId);
        if (meta == null) return false;

        var cachePath = GetCachePath(cacheId);
        if (!File.Exists(cachePath)) return false;

        var ranges = RangeMap.Deserialize(meta.RangesJson);
        bool isFull = ranges.IsFullyDownloaded(meta.ContentLength);

        if (isFull)
        {
            _fullyCachedCache[cacheId] = true;
        }

        return isFull;
    }

    public void MarkAsFullyCached(string cacheId)
    {
        _fullyCachedCache[cacheId] = true;
    }

    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        foreach (var track in tracks)
        {
            if (track.IsDownloaded) continue;

            if (IsFullyCached(track.Id))
            {
                track.IsDownloaded = true;

                var meta = TryGetMetadata(track.Id);
                if (meta != null)
                {
                    if (!string.IsNullOrEmpty(meta.Container))
                        track.PreferredContainer = meta.Container;
                    if (meta.Bitrate > 0)
                        track.PreferredBitrate = meta.Bitrate;
                }
            }
        }
    }

    public bool IsPromoted(string cacheId)
    {
        return _promotedCache.TryGetValue(cacheId, out bool promoted) && promoted;
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
            _fullyCachedCache.Clear();
            _promotedCache.Clear();
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
            long maxCacheBytes = (long)_library.Settings.Storage.AudioCacheLimitMb * 1024 * 1024;

            if (totalSize <= maxCacheBytes) return;

            Log.Info($"Stream cache size {totalSize / 1024 / 1024}MB exceeds limit, cleaning...");

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

    private static string GetSafeFileName(string id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(bytes)[..32];
    }

    public void Dispose()
    {
        _cleanupLock.Dispose();
        _promoteLock.Dispose();
        GC.SuppressFinalize(this);
    }
}