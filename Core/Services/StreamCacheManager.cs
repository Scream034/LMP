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
    private readonly SemaphoreSlim _promoteLock = new(1, 1);
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    private readonly ConcurrentDictionary<string, bool> _fullyCachedCache = new();
    private readonly ConcurrentDictionary<string, bool> _promotedCache = new();

    /// <summary>
    /// Событие для UI — формат закэширован.
    /// </summary>
    public event Action<string, string, int, bool>? OnFormatCached;

    public StreamCacheManager(LibraryService library)
    {
        _library = library;
        _ = Task.Run(CleanupOldCacheAsync);
    }

    // ГЛАВНЫЙ МЕТОД: Промоут кэша

    public void TriggerPromoteWithNotification(string cacheId, string? originalTrackId = null)
    {
        string trackId = originalTrackId ?? ExtractTrackId(cacheId);
        _fullyCachedCache[cacheId] = true;

        Task.Run(async () =>
        {
            try
            {
                var result = await PromoteCacheAsync(cacheId, trackId);

                if (result.Success)
                    Log.Info($"[CachePromote] ✓ {trackId}: {result.Container}/{result.Bitrate}kbps");

                NotifyFormatCached(trackId, result.Container, result.Bitrate, result.Success);
            }
            catch (Exception ex)
            {
                Log.Error($"[CachePromote] ✗ {trackId}: {ex.Message}");
            }
        });
    }

    private record PromoteResult(bool Success, string Container, int Bitrate, string? LocalPath);

    private async Task<PromoteResult> PromoteCacheAsync(string cacheId, string trackId)
    {
        Log.Debug($"[CachePromote] Starting for cacheId={cacheId}, trackId={trackId}");

        if (_promotedCache.TryGetValue(cacheId, out bool done) && done)
        {
            Log.Debug($"[CachePromote] Already promoted: {cacheId}");
            return new PromoteResult(false, "", 0, null);
        }

        if (!await _promoteLock.WaitAsync(100))
        {
            Log.Debug($"[CachePromote] Lock timeout for {cacheId}");
            return new PromoteResult(false, "", 0, null);
        }

        try
        {
            if (_promotedCache.TryGetValue(cacheId, out done) && done)
                return new PromoteResult(false, "", 0, null);

            var track = await _library.GetTrackAsync(trackId);
            if (track == null)
            {
                Log.Warn($"[CachePromote] Track not found: {trackId}");
                return new PromoteResult(false, "", 0, null);
            }

            var meta = TryGetMetadata(cacheId);
            if (meta == null)
            {
                Log.Warn($"[CachePromote] No metadata for: {cacheId}");
                return new PromoteResult(false, "", 0, null);
            }

            Log.Debug($"[CachePromote] Metadata: Container={meta.Container}, Bitrate={meta.Bitrate}, Size={meta.ContentLength}");

            var cachePath = GetCachePath(cacheId);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[CachePromote] Cache file not found: {cachePath}");
                return new PromoteResult(false, meta.Container, meta.Bitrate, null);
            }

            var ranges = RangeMap.Deserialize(meta.RangesJson);
            if (!ranges.IsFullyDownloaded(meta.ContentLength))
            {
                Log.Warn($"[CachePromote] Not fully downloaded: {cacheId}");
                return new PromoteResult(false, meta.Container, meta.Bitrate, null);
            }

            // НОВОЕ: Проверяем лимит Downloads
            await EnsureDownloadLimitAsync(meta.ContentLength);

            string ext = !string.IsNullOrEmpty(meta.Container) ? meta.Container : "m4a";
            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string destPath = Path.Combine(G.Folder.Downloads, safeName);

            if (File.Exists(destPath))
            {
                var info = new FileInfo(destPath);
                if (info.Length == meta.ContentLength)
                {
                    Log.Debug($"[CachePromote] File already exists with same size: {safeName}");
                    track.MarkAsDownloaded(destPath, meta.Container, meta.Bitrate);
                    await _library.AddOrUpdateTrackAsync(track);
                    _promotedCache[cacheId] = true;
                    return new PromoteResult(true, meta.Container, meta.Bitrate, destPath);
                }

                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{meta.Bitrate}kbps.{ext}");
            }

            Log.Info($"[CachePromote] Copying {cachePath} → {destPath}");
            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, meta.Container, meta.Bitrate);
            await _library.AddOrUpdateTrackAsync(track);

            _promotedCache[cacheId] = true;
            return new PromoteResult(true, meta.Container, meta.Bitrate, destPath);
        }
        catch (Exception ex)
        {
            Log.Error($"[CachePromote] Error: {ex.Message}");
            return new PromoteResult(false, "", 0, null);
        }
        finally
        {
            _promoteLock.Release();
        }
    }

    private void NotifyFormatCached(string trackId, string container, int bitrate, bool isDownloaded)
    {
        try
        {
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

    // Публичные методы

    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        foreach (var track in tracks)
        {
            if (track.IsDownloaded) continue;

            if (IsFullyCached(track.Id))
            {
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

    public async Task ClearAllAsync()
    {
        await _cleanupLock.WaitAsync();
        try
        {
            var cacheDir = G.Folder.StreamCache;
            if (!Directory.Exists(cacheDir)) return;

            foreach (var file in Directory.GetFiles(cacheDir))
            {
                try { File.Delete(file); } catch { }
            }

            _fullyCachedCache.Clear();
            _promotedCache.Clear();

            Log.Info("[StreamCache] All cache cleared");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    public static (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var cacheDir = G.Folder.StreamCache;
            if (!Directory.Exists(cacheDir)) return (0, 0);

            var files = Directory.GetFiles(cacheDir, "*.cache");
            long totalSize = files.Sum(f => new FileInfo(f).Length);

            return (files.Length, totalSize / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    public bool IsFullyCached(string cacheId)
    {
        if (_fullyCachedCache.TryGetValue(cacheId, out bool cached) && cached)
            return true;

        var meta = TryGetMetadata(cacheId);
        if (meta == null) return false;

        var cachePath = GetCachePath(cacheId);
        if (!File.Exists(cachePath)) return false;

        var ranges = RangeMap.Deserialize(meta.RangesJson);
        bool isFull = ranges.IsFullyDownloaded(meta.ContentLength);

        if (isFull) _fullyCachedCache[cacheId] = true;
        return isFull;
    }

    public bool IsPromoted(string cacheId) =>
        _promotedCache.TryGetValue(cacheId, out bool promoted) && promoted;

    public bool IsFormatCached(string trackId, string container, int bitrate)
    {
        if (IsFullyCached(trackId))
        {
            var meta = TryGetMetadata(trackId);
            if (meta != null &&
                string.Equals(meta.Container, container, StringComparison.OrdinalIgnoreCase) &&
                meta.Bitrate == bitrate)
                return true;
        }

        string cacheId = $"{trackId}_{container}_{bitrate}";
        return IsFullyCached(cacheId);
    }

    public List<(string Container, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(string, int)>();

        var baseMeta = TryGetMetadata(trackId);
        if (baseMeta != null && !string.IsNullOrEmpty(baseMeta.Container))
        {
            var ranges = RangeMap.Deserialize(baseMeta.RangesJson);
            if (ranges.IsFullyDownloaded(baseMeta.ContentLength))
                result.Add((baseMeta.Container, baseMeta.Bitrate));
        }

        return result;
    }

    // Статические методы работы с метаданными

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
            meta.SourceUrl = url; // Обновляем URL на случай если изменился
            SaveMetadata(cacheId, meta);
            return meta;
        }

        // Если метаданные есть но размер изменился, сохраняем codec/bitrate
        string existingCodec = meta?.Codec ?? "";
        int existingBitrate = meta?.Bitrate ?? 0;
        string existingContainer = meta?.Container ?? "";

        var newMeta = new StreamCacheMetadata
        {
            TrackId = cacheId,
            SourceUrl = url,
            ContentLength = contentLength,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            RangesJson = "[]",
            // Сохраняем предыдущие значения если были
            Codec = existingCodec,
            Bitrate = existingBitrate,
            Container = existingContainer
        };

        var cachePath = GetCachePath(cacheId);
        if (File.Exists(cachePath))
        {
            try { File.Delete(cachePath); } catch { }
        }

        SaveMetadata(cacheId, newMeta);
        Log.Debug($"[StreamCache] Created metadata for {cacheId}: {contentLength} bytes");

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

        if (meta == null)
        {
            // Создаём новые метаданные если их нет
            meta = new StreamCacheMetadata
            {
                TrackId = cacheId,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                RangesJson = "[]"
            };
        }

        meta.Codec = codec;
        meta.Bitrate = bitrate;
        meta.Container = container;
        meta.LastAccessedAt = DateTime.UtcNow;

        SaveMetadata(cacheId, meta);

        Log.Debug($"[StreamCache] UpdateStreamInfo: {cacheId} → {codec}/{bitrate}kbps/{container}");
    }

    // Приватные методы

    private static string ExtractTrackId(string cacheId)
    {
        var parts = cacheId.Split('_');
        if (parts.Length >= 3 && int.TryParse(parts[^1], out _) && IsKnownContainer(parts[^2]))
            return string.Join('_', parts[..^2]);
        return cacheId;
    }

    private static bool IsKnownContainer(string s) =>
        s is "m4a" or "opus" or "webm" or "mp3" or "ogg" or "aac" or "flac" or "mp4";

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    private static string GetSafeFileName(string id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(bytes)[..32];
    }

    private async Task CleanupOldCacheAsync()
    {
        if (!await _cleanupLock.WaitAsync(0)) return;
        try
        {
            var cacheDir = G.Folder.StreamCache;
            if (!Directory.Exists(cacheDir)) return;

            var files = Directory.GetFiles(cacheDir, "*.cache")
                .Select(f => new FileInfo(f)).ToList();

            long totalSize = files.Sum(f => f.Length);
            long maxBytes = (long)_library.Settings.Storage.AudioCacheLimitMb * 1024 * 1024;

            if (totalSize <= maxBytes) return;

            Log.Info($"[StreamCache] Cleanup: {totalSize / 1024 / 1024}MB > {maxBytes / 1024 / 1024}MB limit");

            var toDelete = files
                .OrderBy(f => f.LastAccessTime)
                .TakeWhile(f => (totalSize -= f.Length) > maxBytes * 0.7)
                .ToList();

            foreach (var f in toDelete)
            {
                try
                {
                    f.Delete();
                    var metaPath = Path.ChangeExtension(f.FullName, ".meta");
                    if (File.Exists(metaPath)) File.Delete(metaPath);
                }
                catch { }
            }

            Log.Info($"[StreamCache] Deleted {toDelete.Count} files");
        }
        finally { _cleanupLock.Release(); }
    }

    /// <summary>
    /// Проверяет лимит скачанных треков и удаляет старые если нужно.
    /// Вызывается перед промоутом.
    /// </summary>
    private async Task<bool> EnsureDownloadLimitAsync(long newFileSize)
    {
        var limitMb = _library.Settings.Storage.DownloadedTracksLimitMb;
        if (limitMb <= 0) return true; // Без лимита

        var downloadsDir = G.Folder.Downloads;
        if (!Directory.Exists(downloadsDir)) return true;

        try
        {
            var files = Directory.GetFiles(downloadsDir)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastAccessTime) // Старые первыми
                .ToList();

            long totalSize = files.Sum(f => f.Length);
            long limitBytes = (long)limitMb * 1024 * 1024;

            // Если после добавления нового файла превысим лимит — удаляем старые
            long targetSize = limitBytes - newFileSize;
            if (targetSize < 0) targetSize = limitBytes * 70 / 100; // 70% от лимита

            if (totalSize + newFileSize <= limitBytes)
                return true;

            Log.Info($"[Downloads] Cleaning up: {totalSize / 1024 / 1024}MB + {newFileSize / 1024 / 1024}MB > {limitMb}MB limit");

            long deleted = 0;
            foreach (var file in files)
            {
                if (totalSize - deleted + newFileSize <= targetSize)
                    break;

                try
                {
                    var size = file.Length;
                    file.Delete();
                    deleted += size;
                    Log.Debug($"[Downloads] Deleted old file: {file.Name} ({size / 1024}KB)");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Downloads] Failed to delete {file.Name}: {ex.Message}");
                }
            }

            Log.Info($"[Downloads] Freed {deleted / 1024 / 1024}MB");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Downloads] EnsureDownloadLimit error: {ex.Message}");
            return true; // Продолжаем даже если не удалось очистить
        }
    }

    /// <summary>
    /// Возвращает статистику папки Downloads.
    /// </summary>
    public static (int FileCount, long SizeMb) GetDownloadsStats()
    {
        try
        {
            var downloadsDir = G.Folder.Downloads;
            if (!Directory.Exists(downloadsDir)) return (0, 0);

            var files = Directory.GetFiles(downloadsDir);
            long totalSize = files.Sum(f => new FileInfo(f).Length);

            return (files.Length, totalSize / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Очищает все скачанные треки.
    /// </summary>
    public async Task ClearDownloadsAsync()
    {
        try
        {
            var downloadsDir = G.Folder.Downloads;
            if (!Directory.Exists(downloadsDir)) return;

            foreach (var file in Directory.GetFiles(downloadsDir))
            {
                try { File.Delete(file); } catch { }
            }

            Log.Info("[Downloads] All downloaded tracks cleared");
        }
        catch (Exception ex)
        {
            Log.Error($"[Downloads] ClearDownloads error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cleanupLock.Dispose();
        _promoteLock.Dispose();
        GC.SuppressFinalize(this);
    }
}