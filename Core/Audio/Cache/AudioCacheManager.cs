using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Models;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Cache;

public sealed class AudioCacheManager : IAsyncDisposable, IDisposable
{
    private readonly string _cacheDirectory;
    private readonly long _maxCacheSize;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    
    /// <summary>
    /// Reverse index: trackId → list of cacheKeys.
    /// Ускоряет поиск кэшей трека с O(N) до O(1).
    /// </summary>
    private readonly ConcurrentDictionary<string, List<string>> _trackIndex = new();
    
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly CancellationTokenSource _timerCts = new();
    private readonly Task _autoSaveTask;

    private volatile bool _disposed;

    /// <summary>
    /// Событие: формат трека полностью закэширован.
    /// (trackId, container, bitrate, isDownloaded)
    /// </summary>
    public event Action<string, string, int, bool>? OnFormatCached;

    /// <summary>
    /// Событие: весь кэш очищен.
    /// </summary>
    public event Action? OnCacheCleared;

    public AudioCacheManager(string? cacheDirectory = null, long maxCacheSizeMb = 2048)
    {
        _cacheDirectory = cacheDirectory ?? G.Folder.AudioCache;
        _maxCacheSize = maxCacheSizeMb * 1024 * 1024;

        Directory.CreateDirectory(_cacheDirectory);
        LoadIndex();

        _autoSaveTask = AutoSaveLoopAsync(_timerCts.Token);

        Log.Info($"[AudioCache] Initialized: {_cacheDirectory}, max={maxCacheSizeMb}MB, entries={_entries.Count}");
    }

    #region Public API

    /// <summary>
    /// Проверяет, полностью ли закэширован трек любого формата/битрейта (по trackId).
    /// O(1) благодаря reverse index.
    /// </summary>
    public bool IsTrackFullyCached(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return false;

        if (!_trackIndex.TryGetValue(trackId, out var keys))
            return false;

        foreach (var key in keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.IsComplete)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Возвращает метаданные лучшего полного кэша по trackId.
    /// O(k) где k — количество форматов трека (обычно 1-3).
    /// </summary>
    public CacheEntry? FindBestCacheByTrackId(string trackId) => FindBestCache(trackId);

    /// <summary>
    /// Массовая проверка и обновление IsCached статуса треков.
    /// Оптимизированная версия: O(entries) вместо O(tracks × entries).
    /// </summary>
    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        var trackMap = new Dictionary<string, List<TrackInfo>>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            if (track.IsDownloaded || track.IsCached || string.IsNullOrEmpty(track.Id))
                continue;

            if (!trackMap.TryGetValue(track.Id, out var list))
            {
                list = new List<TrackInfo>(1);
                trackMap[track.Id] = list;
            }
            list.Add(track);
        }

        if (trackMap.Count == 0) return;

        // Используем reverse index для быстрого поиска
        foreach (var (trackId, tracksList) in trackMap)
        {
            if (!_trackIndex.TryGetValue(trackId, out var keys))
                continue;

            CacheEntry? bestEntry = null;

            foreach (var key in keys)
            {
                if (_entries.TryGetValue(key, out var entry) 
                    && entry.IsComplete 
                    && (bestEntry == null || entry.Bitrate > bestEntry.Bitrate))
                {
                    bestEntry = entry;
                }
            }

            if (bestEntry != null)
            {
                foreach (var track in tracksList)
                {
                    track.MarkAsCached(bestEntry.Format.ToString(), bestEntry.Bitrate);
                }
            }
        }
    }

    public bool IsFullyCached(string cacheKey)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry))
            return false;

        return entry.IsComplete;
    }

    public CacheEntry? FindBestCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys))
            return null;

        CacheEntry? best = null;

        foreach (var key in keys)
        {
            if (_entries.TryGetValue(key, out var entry) 
                && entry.IsComplete 
                && (best == null || entry.Bitrate > best.Bitrate))
            {
                best = entry;
            }
        }

        return best;
    }

    public bool HasPartialCache(string cacheKey)
    {
        return _entries.TryGetValue(cacheKey, out var entry) && entry.DownloadedChunks > 0;
    }

    public CacheEntry? GetCacheInfo(string cacheKey)
    {
        return _entries.TryGetValue(cacheKey, out var entry) ? entry : null;
    }

    public string GetCachePath(string cacheKey)
    {
        var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))[..16];
        return Path.Combine(_cacheDirectory, safeId + CacheFileExtension);
    }

    public void Touch(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            entry.LastAccessedAt = DateTime.UtcNow;
        }
    }

    public CacheEntry CreateOrUpdate(
        string cacheKey,
        string trackId,
        string url,
        long totalSize,
        AudioFormat format,
        AudioCodec codec,
        int bitrate = 0,
        long durationMs = -1,
        int chunkSize = ChunkSize)
    {
        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = url,
            TotalSize = totalSize,
            Format = format,
            Codec = codec,
            Bitrate = bitrate,
            DurationMs = durationMs,
            ChunkSize = chunkSize,
            TotalChunks = (int)Math.Ceiling((double)totalSize / chunkSize),
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        entry.OriginalUrl = url;
        entry.LastAccessedAt = DateTime.UtcNow;

        if (bitrate > 0)
            entry.Bitrate = bitrate;

        if (durationMs > 0)
            entry.DurationMs = durationMs;

        // Обновляем reverse index
        AddToTrackIndex(trackId, cacheKey);

        return entry;
    }

    public void MarkComplete(string cacheKey, long? durationMs = null, int? bitrate = null)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            entry.IsComplete = true;
            entry.CompletedAt = DateTime.UtcNow;
            entry.LastAccessedAt = DateTime.UtcNow;

            if (durationMs.HasValue)
                entry.DurationMs = durationMs.Value;

            if (bitrate.HasValue)
                entry.Bitrate = bitrate.Value;

            // Обновляем кэшированный размер файла
            UpdateFileSizeCache(entry);

            Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
            
            _ = SaveIndexAsync();

            // Поднимаем событие для UI
            RaiseFormatCached(entry);
        }
    }

    private void RaiseFormatCached(CacheEntry entry)
    {
        try
        {
            OnFormatCached?.Invoke(
                entry.TrackId,
                entry.Format.ToString(),
                entry.Bitrate,
                false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] OnFormatCached handler error: {ex.Message}");
        }
    }

    public async Task WriteChunkAsync(string cacheKey, int chunkIndex, byte[] data, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry))
            return;

        if (entry.IsComplete)
            return;

        var filePath = GetCachePath(cacheKey);
        long offset = (long)chunkIndex * entry.ChunkSize;

        try
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(data, ct);

            entry.MarkChunkDownloaded(chunkIndex);
            entry.LastAccessedAt = DateTime.UtcNow;

            if (!entry.IsComplete && entry.DownloadedChunks >= entry.TotalChunks)
            {
                entry.IsComplete = true;
                entry.CompletedAt = DateTime.UtcNow;
                UpdateFileSizeCache(entry);
                Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
                RaiseFormatCached(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Write chunk failed: {ex.Message}");
        }
    }

    public async Task<byte[]?> ReadChunkAsync(string cacheKey, int chunkIndex, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry))
            return null;

        if (!entry.IsChunkDownloaded(chunkIndex))
            return null;

        var filePath = GetCachePath(cacheKey);
        if (!File.Exists(filePath))
            return null;

        long offset = (long)chunkIndex * entry.ChunkSize;
        int size = (int)Math.Min(entry.ChunkSize, entry.TotalSize - offset);

        if (size <= 0)
            return null;

        try
        {
            var buffer = new byte[size];

            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);
            int totalRead = 0;

            while (totalRead < size)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(totalRead, size - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            entry.LastAccessedAt = DateTime.UtcNow;

            return totalRead == size ? buffer : null;
        }
        catch
        {
            return null;
        }
    }

    public Stream? OpenCachedStream(string cacheKey)
    {
        if (!IsFullyCached(cacheKey))
            return null;

        var filePath = GetCachePath(cacheKey);
        Touch(cacheKey);

        return new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: CacheFileBufferSize);
    }

    public void RemoveCache(string cacheKey)
    {
        if (_entries.TryRemove(cacheKey, out var entry))
        {
            // Удаляем из reverse index
            RemoveFromTrackIndex(entry.TrackId, cacheKey);

            var filePath = GetCachePath(cacheKey);
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch { }

            _ = SaveIndexAsync();
        }
    }

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var stats = GetStats();
        long totalSize = stats.TotalSizeBytes;

        if (totalSize <= _maxCacheSize)
            return;

        Log.Info($"[AudioCache] Cleanup needed: {totalSize / 1024 / 1024}MB > {_maxCacheSize / 1024 / 1024}MB");

        var entries = _entries.Values
            .OrderBy(e => e.LastAccessedAt)
            .ToList();

        foreach (var entry in entries)
        {
            if (totalSize <= _maxCacheSize * CacheCleanupThreshold)
                break;

            totalSize -= entry.ActualFileSize;
            RemoveCache(entry.CacheKey);
        }

        Log.Info($"[AudioCache] Cleanup complete, new size: {totalSize / 1024 / 1024}MB");
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Возвращает статистику кэша БЕЗ IO (использует кэшированные размеры).
    /// </summary>
    public CacheStats GetStats()
    {
        long totalSize = 0;
        int completeCount = 0;
        int partialCount = 0;

        foreach (var entry in _entries.Values)
        {
            totalSize += entry.ActualFileSize;

            if (entry.IsComplete)
                completeCount++;
            else if (entry.DownloadedChunks > 0)
                partialCount++;
        }

        return new CacheStats
        {
            TotalEntries = _entries.Count,
            CompleteEntries = completeCount,
            PartialEntries = partialCount,
            TotalSizeBytes = totalSize,
            MaxSizeBytes = _maxCacheSize
        };
    }

    /// <summary>
    /// Возвращает кортеж (fileCount, sizeMb) для совместимости со старым API.
    /// </summary>
    public (int FileCount, int SizeMb) GetStatsCompact()
    {
        var stats = GetStats();
        return (stats.CompleteEntries, (int)(stats.TotalSizeBytes / 1024 / 1024));
    }

    /// <summary>
    /// Возвращает статистику папки Downloads.
    /// </summary>
    public static (int FileCount, int SizeMb) GetDownloadsStats()
    {
        try
        {
            var dir = new DirectoryInfo(G.Folder.Downloads);
            if (!dir.Exists)
                return (0, 0);

            var files = dir.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            long totalBytes = files.Sum(f => f.Length);

            return (files.Length, (int)(totalBytes / 1024 / 1024));
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] GetDownloadsStats error: {ex.Message}");
            return (0, 0);
        }
    }

    /// <summary>
    /// Возвращает список закэшированных форматов для трека.
    /// O(k) где k — количество форматов.
    /// </summary>
    public List<(string Container, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(string, int)>();

        if (!_trackIndex.TryGetValue(trackId, out var keys))
            return result;

        foreach (var key in keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.IsComplete)
            {
                result.Add((entry.Format.ToString(), entry.Bitrate));
            }
        }

        return result;
    }

    /// <summary>
    /// Проверяет, закэширован ли конкретный формат/битрейт.
    /// </summary>
    public bool IsFormatCached(string trackId, string container, int bitrate)
    {
        var normalizedBitrate = NormalizeBitrate(bitrate);

        if (!Enum.TryParse<AudioFormat>(container, true, out var format))
            return false;

        var cacheKey = BuildCacheKey(trackId, format, normalizedBitrate);
        return IsFullyCached(cacheKey);
    }

    /// <summary>
    /// Строит уникальный ключ кэша: trackId + формат + нормализованный битрейт.
    /// </summary>
    public static string BuildCacheKey(string trackId, AudioFormat format, int bitrate)
    {
        int normalizedBitrate = NormalizeBitrate(bitrate);
        return $"{trackId}_{format}_{normalizedBitrate}";
    }

    /// <summary>
    /// Нормализует битрейт к ближайшему "стандартному" значению.
    /// </summary>
    public static int NormalizeBitrate(int bitrate)
    {
        if (bitrate <= 0) return 128;

        int[] standards = [48, 64, 96, 128, 160, 192, 256, 320];

        int closest = standards[0];
        int minDiff = Math.Abs(bitrate - closest);

        foreach (int std in standards)
        {
            int diff = Math.Abs(bitrate - std);
            if (diff < minDiff)
            {
                minDiff = diff;
                closest = std;
            }
        }

        return minDiff > 20 ? bitrate : closest;
    }

    #endregion

    #region Export to Downloads

    /// <summary>
    /// Экспортирует полностью закэшированный трек в папку Downloads.
    /// </summary>
    public async Task<bool> ExportTrackToDownloadsAsync(
        string trackId, 
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct = default)
    {
        var entry = FindBestCache(trackId);
        if (entry == null)
        {
            Log.Warn($"[AudioCache] Track {trackId} not fully cached, cannot export");
            return false;
        }

        return await PromoteCacheToDownloadsAsync(entry, getTrackFunc, updateTrackFunc, ct);
    }

    private async Task<bool> PromoteCacheToDownloadsAsync(
        CacheEntry entry,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct)
    {
        if (!await _saveLock.WaitAsync(1000, ct))
            return false;

        try
        {
            var track = await getTrackFunc(entry.TrackId);
            if (track == null)
            {
                Log.Warn($"[AudioCache] Track not found: {entry.TrackId}");
                return false;
            }

            if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
            {
                Log.Debug($"[AudioCache] Already downloaded: {track.Title}");
                return true;
            }

            var cachePath = GetCachePath(entry.CacheKey);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[AudioCache] Cache file not found: {cachePath}");
                return false;
            }

            var fileInfo = new FileInfo(cachePath);
            if (fileInfo.Length < entry.TotalSize)
            {
                Log.Warn($"[AudioCache] Incomplete cache file: {fileInfo.Length} < {entry.TotalSize}");
                return false;
            }

            string ext = entry.Format switch
            {
                AudioFormat.WebM => "webm",
                AudioFormat.Mp4 => "m4a",
                AudioFormat.Ogg => "ogg",
                _ => "audio"
            };

            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string destPath = Path.Combine(G.Folder.Downloads, safeName);

            if (File.Exists(destPath))
            {
                var existingInfo = new FileInfo(destPath);
                if (existingInfo.Length == entry.TotalSize)
                {
                    track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
                    await updateTrackFunc(track);
                    Log.Debug($"[AudioCache] File already exists: {safeName}");
                    return true;
                }

                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{entry.Bitrate}kbps.{ext}");
            }

            Log.Info($"[AudioCache] Exporting to Downloads: {Path.GetFileName(destPath)}");

            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
            await updateTrackFunc(track);

            OnFormatCached?.Invoke(entry.TrackId, entry.Format.ToString(), entry.Bitrate, true);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Export failed: {ex.Message}");
            return false;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    #endregion

    #region Clear & Maintenance

    /// <summary>
    /// Полностью очищает кэш аудио.
    /// </summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        if (!await _saveLock.WaitAsync(5000, ct))
        {
            Log.Warn("[AudioCache] ClearAllAsync: couldn't acquire lock");
            return;
        }

        try
        {
            Log.Info("[AudioCache] Clearing all cache...");

            _entries.Clear();
            _trackIndex.Clear();

            var dir = new DirectoryInfo(_cacheDirectory);
            if (dir.Exists)
            {
                foreach (var file in dir.GetFiles())
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}");
                    }
                }
            }

            Log.Info("[AudioCache] Cache cleared");
        }
        finally
        {
            _saveLock.Release();
        }

        // Уведомляем подписчиков ПОСЛЕ освобождения лока
        try
        {
            OnCacheCleared?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] OnCacheCleared handler error: {ex.Message}");
        }
    }

    /// <summary>
    /// Очищает папку Downloads.
    /// </summary>
    public static async Task ClearDownloadsAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var dir = new DirectoryInfo(G.Folder.Downloads);
                if (!dir.Exists)
                    return;

                Log.Info("[AudioCache] Clearing downloads folder...");

                foreach (var file in dir.GetFiles())
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}");
                    }
                }

                Log.Info("[AudioCache] Downloads cleared");
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioCache] ClearDownloadsAsync error: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// Удаляет кэш конкретного трека (все форматы).
    /// </summary>
    public void RemoveTrackCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys))
            return;

        var keysToRemove = keys.ToList();

        foreach (var key in keysToRemove)
        {
            RemoveCache(key);
        }

        Log.Debug($"[AudioCache] Removed {keysToRemove.Count} cache entries for track {trackId}");
    }

    /// <summary>
    /// Удаляет неполные/повреждённые кэши.
    /// </summary>
    public async Task RemoveIncompleteAsync(CancellationToken ct = default)
    {
        var incomplete = _entries
            .Where(kv => !kv.Value.IsComplete)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in incomplete)
        {
            RemoveCache(key);
        }

        if (incomplete.Count > 0)
        {
            Log.Info($"[AudioCache] Removed {incomplete.Count} incomplete cache entries");
            await SaveIndexAsync();
        }
    }

    /// <summary>
    /// Проверяет целостность кэша и удаляет сиротские файлы.
    /// </summary>
    public async Task ValidateAndCleanupAsync(CancellationToken ct = default)
    {
        // 1. Удаляем записи без файлов
        var orphanedEntries = new List<string>();

        foreach (var (key, entry) in _entries)
        {
            var filePath = GetCachePath(key);
            if (!File.Exists(filePath))
            {
                orphanedEntries.Add(key);
            }
            else
            {
                // Обновляем кэшированный размер
                UpdateFileSizeCache(entry);
            }
        }

        foreach (var key in orphanedEntries)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                RemoveFromTrackIndex(entry.TrackId, key);
            }
        }

        // 2. Удаляем файлы без записей
        var validFiles = _entries.Keys
            .Select(GetCachePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dir = new DirectoryInfo(_cacheDirectory);
        if (dir.Exists)
        {
            foreach (var file in dir.GetFiles($"*{CacheFileExtension}"))
            {
                if (!validFiles.Contains(file.FullName))
                {
                    try
                    {
                        file.Delete();
                        Log.Debug($"[AudioCache] Deleted orphaned file: {file.Name}");
                    }
                    catch { }
                }
            }
        }

        if (orphanedEntries.Count > 0)
        {
            Log.Info($"[AudioCache] Validation: removed {orphanedEntries.Count} orphaned entries");
            await SaveIndexAsync();
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Добавляет cacheKey в reverse index для trackId.
    /// </summary>
    private void AddToTrackIndex(string trackId, string cacheKey)
    {
        _trackIndex.AddOrUpdate(
            trackId,
            _ => new List<string> { cacheKey },
            (_, list) =>
            {
                if (!list.Contains(cacheKey))
                    list.Add(cacheKey);
                return list;
            });
    }

    /// <summary>
    /// Удаляет cacheKey из reverse index.
    /// </summary>
    private void RemoveFromTrackIndex(string trackId, string cacheKey)
    {
        if (_trackIndex.TryGetValue(trackId, out var list))
        {
            list.Remove(cacheKey);
            if (list.Count == 0)
                _trackIndex.TryRemove(trackId, out _);
        }
    }

    /// <summary>
    /// Обновляет кэшированный размер файла в entry (без IO при следующих вызовах GetStats).
    /// </summary>
    private void UpdateFileSizeCache(CacheEntry entry)
    {
        try
        {
            var filePath = GetCachePath(entry.CacheKey);
            if (File.Exists(filePath))
            {
                entry.ActualFileSize = new FileInfo(filePath).Length;
            }
        }
        catch
        {
            // Игнорируем ошибки IO при обновлении размера
        }
    }

    private void LoadIndex()
    {
        var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);

        if (!File.Exists(indexPath))
            return;

        try
        {
            var json = File.ReadAllText(indexPath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.CacheKey))
                        continue;

                    var filePath = GetCachePath(entry.CacheKey);
                    if (File.Exists(filePath))
                    {
                        entry.RestoreChunkMask();
                        UpdateFileSizeCache(entry);
                        _entries.TryAdd(entry.CacheKey, entry);
                        AddToTrackIndex(entry.TrackId, entry.CacheKey);
                    }
                }
            }

            Log.Debug($"[AudioCache] Loaded {_entries.Count} entries");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Failed to load index: {ex.Message}");
        }
    }

    private async Task SaveIndexAsync()
    {
        if (_disposed) return;

        if (!await _saveLock.WaitAsync(CacheSaveLockTimeoutMs))
            return;

        try
        {
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            var entries = _entries.Values.ToList();

            foreach (var entry in entries)
            {
                entry.SaveChunkMask();
            }

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(indexPath, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Failed to save index: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task AutoSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CacheAutoSaveIntervalMs, ct);
                await SaveIndexAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioCache] Auto-save error: {ex.Message}");
            }
        }
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();

        try { _autoSaveTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { }

        try { SaveIndexAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch { }

        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();

        try { await _autoSaveTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { }

        await SaveIndexAsync();

        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    #endregion
}

public sealed class CacheEntry
{
    public string CacheKey { get; init; } = "";
    public string TrackId { get; init; } = "";
    public string OriginalUrl { get; set; } = "";
    public long TotalSize { get; init; }
    public AudioFormat Format { get; init; }
    public AudioCodec Codec { get; set; }
    public int Bitrate { get; set; }
    public long DurationMs { get; set; } = -1;
    public int ChunkSize { get; init; }
    public int TotalChunks { get; init; }

    private int _downloadedChunks;

    public int DownloadedChunks
    {
        get => Volatile.Read(ref _downloadedChunks);
        set => Volatile.Write(ref _downloadedChunks, value);
    }

    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsComplete { get; set; }

    /// <summary>
    /// Кэшированный размер файла (обновляется при MarkComplete/WriteChunk).
    /// Избегаем IO при GetStats().
    /// </summary>
    public long ActualFileSize { get; set; }

    public string? ChunkMaskData { get; set; }

    [JsonIgnore]
    private int[]? _chunkBits;

    [JsonIgnore]
    public double DownloadProgress => TotalChunks == 0 ? 0 : (double)DownloadedChunks / TotalChunks * 100;

    public bool IsChunkDownloaded(int index)
    {
        EnsureChunkBits();
        if (index < 0 || index >= TotalChunks) return false;
        return (Volatile.Read(ref _chunkBits![index >> 5]) & (1 << (index & 31))) != 0;
    }

    public void MarkChunkDownloaded(int index)
    {
        EnsureChunkBits();
        if (index < 0 || index >= TotalChunks) return;

        int word = index >> 5;
        int bit = 1 << (index & 31);

        int current = Volatile.Read(ref _chunkBits![word]);
        if ((current & bit) == 0)
        {
            int original;
            do
            {
                original = current;
                current = Interlocked.CompareExchange(ref _chunkBits![word], original | bit, original);
            } while (current != original);

            if ((original & bit) == 0)
            {
                Interlocked.Increment(ref _downloadedChunks);
            }
        }
    }

    public void SaveChunkMask()
    {
        if (_chunkBits == null) return;

        var bytes = new byte[_chunkBits.Length * 4];
        Buffer.BlockCopy(_chunkBits, 0, bytes, 0, bytes.Length);
        ChunkMaskData = Convert.ToBase64String(bytes);
    }

    public void RestoreChunkMask()
    {
        if (string.IsNullOrEmpty(ChunkMaskData)) return;

        EnsureChunkBits();

        try
        {
            var bytes = Convert.FromBase64String(ChunkMaskData);
            Buffer.BlockCopy(bytes, 0, _chunkBits!, 0, Math.Min(bytes.Length, _chunkBits!.Length * 4));

            int count = 0;
            for (int i = 0; i < TotalChunks; i++)
            {
                if (IsChunkDownloaded(i))
                    count++;
            }
            Volatile.Write(ref _downloadedChunks, count);
        }
        catch { }
    }

    private void EnsureChunkBits()
    {
        _chunkBits ??= new int[(TotalChunks + 31) / 32];
    }
}

public readonly struct CacheStats
{
    public int TotalEntries { get; init; }
    public int CompleteEntries { get; init; }
    public int PartialEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public long MaxSizeBytes { get; init; }

    public double UsagePercent => MaxSizeBytes == 0 ? 0 : (double)TotalSizeBytes / MaxSizeBytes * 100;

    public string TotalSizeFormatted => FormatSize(TotalSizeBytes);
    public string MaxSizeFormatted => FormatSize(MaxSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}