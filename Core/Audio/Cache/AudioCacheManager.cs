using System.Buffers;
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
    /// Reverse index: trackId -> thread-safe множество cacheKey.
    /// Ускоряет поиск форматов трека с O(N) до O(1).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _trackIndex = new();

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>Per-file семафоры для изоляции параллельных записей одного файла.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileWriteLocks = new();

    private readonly CancellationTokenSource _timerCts = new();
    private readonly Task _autoSaveTask;
    private volatile bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public event Action<string, string, int, bool>? OnFormatCached;
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

    public bool IsTrackFullyCached(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return false;
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return false;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && EnsureCacheFileIntegrity(entry))
            {
                return true;
            }
        }

        return false;
    }

    public CacheEntry? FindBestCacheByTrackId(string trackId) => FindBestCache(trackId);

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

        foreach (var (trackId, tracksList) in trackMap)
        {
            if (!_trackIndex.TryGetValue(trackId, out var keys)) continue;

            CacheEntry? bestEntry = null;

            foreach (var key in keys.Keys)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.IsComplete
                    && EnsureCacheFileIntegrity(entry)
                    && (bestEntry == null || entry.Bitrate > bestEntry.Bitrate))
                {
                    bestEntry = entry;
                }
            }

            if (bestEntry != null)
            {
                foreach (var track in tracksList)
                    track.MarkAsCached(bestEntry.Format.ToString(), bestEntry.Bitrate);
            }
        }
    }

    public bool IsFullyCached(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry)
        && entry.IsComplete
        && EnsureCacheFileIntegrity(entry);

    public CacheEntry? FindBestCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return null;

        CacheEntry? best = null;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && EnsureCacheFileIntegrity(entry)
                && (best == null || entry.Bitrate > best.Bitrate))
            {
                best = entry;
            }
        }

        return best;
    }

    public bool HasPartialCache(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) && entry.DownloadedChunks > 0;

    public CacheEntry? GetCacheInfo(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) ? entry : null;

    public string GetCachePath(string cacheKey)
    {
        var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))[..16];
        return Path.Combine(_cacheDirectory, safeId + CacheFileExtension);
    }

    public void Touch(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
            entry.LastAccessedAt = DateTime.UtcNow;
    }

    public CacheEntry CreateOrUpdate(
         string cacheKey, string trackId, string url, long totalSize,
         AudioFormat format, AudioCodec codec, int bitrate = 0,
         long durationMs = -1, int chunkSize = ChunkSize)
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
        if (bitrate > 0) entry.Bitrate = bitrate;
        if (durationMs > 0) entry.DurationMs = durationMs;

        // Если кэш-запись существовала, но не содержала скачанных данных,
        // мы можем безопасно обновить размер чанка под новый сетевой профиль.
        if (entry.DownloadedChunks == 0 && entry.ChunkSize != chunkSize)
        {
            entry.ChunkSize = chunkSize;
            entry.TotalChunks = (int)Math.Ceiling((double)totalSize / chunkSize);
            entry.ResetChunkMask();
        }

        AddToTrackIndex(trackId, cacheKey);
        return entry;
    }

    public void MarkComplete(string cacheKey, long? durationMs = null, int? bitrate = null)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;

        entry.IsComplete = true;
        entry.CompletedAt = DateTime.UtcNow;
        entry.LastAccessedAt = DateTime.UtcNow;
        if (durationMs.HasValue) entry.DurationMs = durationMs.Value;
        if (bitrate.HasValue) entry.Bitrate = bitrate.Value;

        UpdateFileSizeCache(entry);
        Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
        _ = SaveIndexAsync();
        RaiseFormatCached(entry);
    }

    public async Task WriteChunkAsync(
        string cacheKey, int chunkIndex, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;

        if (entry.IsComplete) return;

        var fileLock = _fileWriteLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (entry.IsComplete) return;

            var filePath = GetCachePath(cacheKey);
            long offset = (long)chunkIndex * entry.ChunkSize;

            await using var fs = new FileStream(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(data, ct).ConfigureAwait(false);

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
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Читает чанк из файла кэша.
    /// </summary>
    /// <remarks>
    /// <para><b>Short read handling:</b> Если прочитано меньше байт чем ожидалось,
    /// чанк инвалидируется в bitmap через <see cref="CacheEntry.InvalidateChunk"/>.
    /// При следующем обращении <see cref="CachingStreamSource"/> перекачает его заново.</para>
    /// </remarks>
    public async Task<(IMemoryOwner<byte> Owner, int Length)?> ReadChunkAsync(
        string cacheKey, int chunkIndex, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return null;
        if (!entry.IsChunkDownloaded(chunkIndex)) return null;

        var filePath = GetCachePath(cacheKey);
        if (!File.Exists(filePath)) return null;

        long offset = (long)chunkIndex * entry.ChunkSize;
        int size = (int)Math.Min(entry.ChunkSize, entry.TotalSize - offset);
        if (size <= 0) return null;

        var memoryOwner = MemoryPool<byte>.Shared.Rent(size);

        try
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            var buffer = memoryOwner.Memory[..size];

            while (totalRead < size)
            {
                int read = await fs.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != size)
            {
                memoryOwner.Dispose();

                // Инвалидируем только этот чанк — не весь entry.
                // CachingStreamSource при следующем EnsureChunkAsync перекачает его.
                entry.InvalidateChunk(chunkIndex);

                Log.Warn($"[AudioCache] Short read chunk {chunkIndex} of {cacheKey}: " +
                         $"expected={size}, got={totalRead}. Chunk invalidated for re-download.");
                return null;
            }

            entry.LastAccessedAt = DateTime.UtcNow;
            return (memoryOwner, size);
        }
        catch
        {
            memoryOwner.Dispose();
            return null;
        }
    }

    public Stream? OpenCachedStream(string cacheKey)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)
            || !entry.IsComplete
            || !EnsureCacheFileIntegrity(entry))
        {
            return null;
        }

        Touch(cacheKey);
        return new FileStream(
            GetCachePath(cacheKey),
            FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: CacheFileBufferSize);
    }

    public void RemoveCache(string cacheKey)
    {
        if (!_entries.TryRemove(cacheKey, out var entry)) return;

        RemoveFromTrackIndex(entry.TrackId, cacheKey);

        var filePath = GetCachePath(cacheKey);
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch { }

        _ = SaveIndexAsync();
    }

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var stats = GetStats();
        if (stats.TotalSizeBytes <= _maxCacheSize) return;

        Log.Info($"[AudioCache] Cleanup needed: {stats.TotalSizeBytes / 1024 / 1024}MB > {_maxCacheSize / 1024 / 1024}MB");

        long totalSize = stats.TotalSizeBytes;

        var entries = _entries.Values
            .OrderBy(e => e.LastAccessedAt)
            .ToList();

        foreach (var entry in entries)
        {
            if (totalSize <= _maxCacheSize * CacheCleanupThreshold) break;
            totalSize -= entry.ActualFileSize;
            RemoveCache(entry.CacheKey);
        }

        Log.Info($"[AudioCache] Cleanup complete, new size: {totalSize / 1024 / 1024}MB");
    }

    #endregion

    #region Resume Cache From Downloaded File

    public async Task ResumeCacheFromDownloadedFileAsync(
        string trackId,
        string downloadedFilePath,
        AudioFormat format,
        int bitrate,
        int startChunkHint = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId) || !File.Exists(downloadedFilePath))
            return;

        var downloadedInfo = new FileInfo(downloadedFilePath);
        if (downloadedInfo.Length == 0)
            return;

        string cacheKey = AudioSourceFactory.BuildCacheKey(trackId, format, bitrate);

        if (_entries.TryGetValue(cacheKey, out var existingEntry) && existingEntry.IsComplete)
        {
            Log.Debug($"[AudioCache] Resume skipped: {cacheKey} already complete");
            return;
        }

        long fileSize = downloadedInfo.Length;

        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = "",
            TotalSize = fileSize,
            Format = format,
            Codec = AudioSourceFactory.GetCodecForFormat(format),
            Bitrate = bitrate,
            ChunkSize = ChunkSize,
            TotalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize),
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        AddToTrackIndex(trackId, cacheKey);

        int chunkSize = entry.ChunkSize;
        int totalChunks = entry.TotalChunks;
        int clampedStart = Math.Clamp(startChunkHint, 0, totalChunks - 1);

        Log.Info($"[AudioCache] Resuming cache from downloaded file: {cacheKey}, " +
                 $"chunks={totalChunks}, start={clampedStart}, file={fileSize / 1024}KB");

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            await using var sourceStream = new FileStream(
                downloadedFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            await WriteChunkRangeAsync(
                entry, sourceStream, rentedBuffer,
                fromChunk: clampedStart, toChunkExclusive: totalChunks,
                fileSize, cacheKey, ct).ConfigureAwait(false);

            if (!ct.IsCancellationRequested && clampedStart > 0)
            {
                await WriteChunkRangeAsync(
                    entry, sourceStream, rentedBuffer,
                    fromChunk: 0, toChunkExclusive: clampedStart,
                    fileSize, cacheKey, ct).ConfigureAwait(false);
            }

            if (!entry.IsComplete && !ct.IsCancellationRequested)
            {
                double completionRatio = (double)entry.DownloadedChunks / entry.TotalChunks;
                if (completionRatio >= 0.99)
                {
                    var fileLock = _fileWriteLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                    await fileLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (!entry.IsComplete)
                        {
                            entry.IsComplete = true;
                            entry.CompletedAt = DateTime.UtcNow;
                            UpdateFileSizeCache(entry);
                            Log.Info($"[AudioCache] Cache complete via mismatch guard: {cacheKey} " +
                                     $"({entry.DownloadedChunks}/{entry.TotalChunks} chunks, ratio={completionRatio:P1})");
                            _ = SaveIndexAsync();
                            RaiseFormatCached(entry);
                        }
                    }
                    finally
                    {
                        fileLock.Release();
                    }
                }
            }

            if (entry.IsComplete)
                Log.Info($"[AudioCache] Resume complete: {cacheKey}");
            else if (ct.IsCancellationRequested)
                Log.Debug($"[AudioCache] Resume cancelled: {cacheKey} " +
                          $"({entry.DownloadedChunks}/{entry.TotalChunks} chunks written)");
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"[AudioCache] Resume cancelled: {cacheKey}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Resume failed for {cacheKey}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private async Task WriteChunkRangeAsync(
        CacheEntry entry,
        FileStream sourceStream,
        byte[] rentedBuffer,
        int fromChunk,
        int toChunkExclusive,
        long fileSize,
        string cacheKey,
        CancellationToken ct)
    {
        int chunkSize = entry.ChunkSize;

        for (int i = fromChunk; i < toChunkExclusive && !ct.IsCancellationRequested; i++)
        {
            if (entry.IsChunkDownloaded(i)) continue;

            long offset = (long)i * chunkSize;
            if (offset >= fileSize) break;

            int expectedBytes = (int)Math.Min(chunkSize, fileSize - offset);

            if (sourceStream.Position != offset)
                sourceStream.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            while (totalRead < expectedBytes)
            {
                int read = await sourceStream.ReadAsync(
                    rentedBuffer.AsMemory(totalRead, expectedBytes - totalRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead == 0) continue;

            await WriteChunkAsync(cacheKey, i, rentedBuffer.AsMemory(0, totalRead), ct).ConfigureAwait(false);

            if (entry.IsComplete) return;
        }
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Возвращает компактную статистику кэша (кол-во файлов, размер в МБ).
    /// <para>Учитывает как полностью скачанные треки, так и находящиеся в процессе загрузки (частичный кэш).</para>
    /// </summary>
    public (int FileCount, int SizeMb) GetStatsCompact()
    {
        var stats = GetStats();
        // Суммируем завершенные и частичные файлы на диске, чтобы пользователь видел реальный счетчик
        int totalFiles = stats.CompleteEntries + stats.PartialEntries;
        return (totalFiles, (int)(stats.TotalSizeBytes / 1024 / 1024));
    }

    /// <summary>
    /// Собирает полную статистику использования дискового пространства кэша.
    /// <para>Опрашивает размеры частично скачанных файлов на диске на холодном пути (при вызове метода),
    /// гарантируя точные показатели в UI и корректное поведение алгоритмов очистки диска.</para>
    /// </summary>
    public CacheStats GetStats()
    {
        long totalSize = 0;
        int completeCount = 0;
        int partialCount = 0;
        int totalCount = 0;

        foreach (var entry in _entries.Values)
        {
            // Обновляем размер неполных файлов на диске прямо перед подсчетом статистики
            if (!entry.IsComplete)
            {
                UpdateFileSizeCache(entry);
            }
            else if (entry.ActualFileSize == 0)
            {
                // На всякий случай проверяем и завершенные файлы (если пользователь стёр их вручную)
                UpdateFileSizeCache(entry);
            }

            // Учитываем в статистике только реально существующие файлы на диске
            if (entry.ActualFileSize > 0)
            {
                totalCount++;
                if (entry.IsComplete)
                {
                    completeCount++;
                }
                else if (entry.DownloadedChunks > 0)
                {
                    partialCount++;
                }
                totalSize += entry.ActualFileSize;
            }
        }

        return new CacheStats
        {
            TotalEntries = totalCount,
            CompleteEntries = completeCount,
            PartialEntries = partialCount,
            TotalSizeBytes = totalSize,
            MaxSizeBytes = _maxCacheSize
        };
    }

    public static (int FileCount, int SizeMb) GetDownloadsStats()
    {
        try
        {
            var dir = new DirectoryInfo(G.Folder.Downloads);
            if (!dir.Exists) return (0, 0);
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

    public List<(string Container, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(string, int)>();
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return result;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && EnsureCacheFileIntegrity(entry))
            {
                result.Add((entry.Format.ToString(), entry.Bitrate));
            }
        }

        return result;
    }

    public bool IsFormatCached(string trackId, string container, int bitrate)
    {
        if (!Enum.TryParse<AudioFormat>(container, true, out var format)) return false;
        return IsFullyCached(AudioSourceFactory.BuildCacheKey(trackId, format, bitrate));
    }

    #endregion

    #region Export to Downloads

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
        return await PromoteCacheToDownloadsAsync(entry, getTrackFunc, updateTrackFunc, ct).ConfigureAwait(false);
    }

    private async Task<bool> PromoteCacheToDownloadsAsync(
        CacheEntry entry,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct)
    {
        if (!await _saveLock.WaitAsync(1000, ct).ConfigureAwait(false)) return false;

        try
        {
            var track = await getTrackFunc(entry.TrackId).ConfigureAwait(false);
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
                var existing = new FileInfo(destPath);
                if (existing.Length == entry.TotalSize)
                {
                    track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
                    await updateTrackFunc(track).ConfigureAwait(false);
                    return true;
                }

                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{entry.Bitrate}kbps.{ext}");
            }

            Log.Info($"[AudioCache] Exporting to Downloads: {Path.GetFileName(destPath)}");
            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
            await updateTrackFunc(track).ConfigureAwait(false);
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

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        if (!await _saveLock.WaitAsync(5000, ct).ConfigureAwait(false))
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
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
            }

            Log.Info("[AudioCache] Cache cleared");
        }
        finally
        {
            _saveLock.Release();
        }

        try { OnCacheCleared?.Invoke(); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnCacheCleared handler error: {ex.Message}"); }
    }

    public static async Task ClearDownloadsAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var dir = new DirectoryInfo(G.Folder.Downloads);
                if (!dir.Exists) return;

                Log.Info("[AudioCache] Clearing downloads folder...");
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
                Log.Info("[AudioCache] Downloads cleared");
            }
            catch (Exception ex) { Log.Error($"[AudioCache] ClearDownloadsAsync error: {ex.Message}"); }
        }, ct).ConfigureAwait(false);
    }

    public void RemoveTrackCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;

        var keysToRemove = keys.Keys.ToList();
        foreach (var key in keysToRemove)
            RemoveCache(key);

        Log.Debug($"[AudioCache] Removed {keysToRemove.Count} cache entries for track {trackId}");
    }

    public async Task RemoveIncompleteAsync(CancellationToken ct = default)
    {
        var incomplete = _entries
            .Where(kv => !kv.Value.IsComplete)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in incomplete)
            RemoveCache(key);

        if (incomplete.Count > 0)
        {
            Log.Info($"[AudioCache] Removed {incomplete.Count} incomplete cache entries");
            await SaveIndexAsync().ConfigureAwait(false);
        }
    }

    public async Task ValidateAndCleanupAsync(CancellationToken ct = default)
    {
        var orphanedEntries = new List<string>();

        foreach (var (key, entry) in _entries)
        {
            if (!File.Exists(GetCachePath(key)))
                orphanedEntries.Add(key);
            else
                UpdateFileSizeCache(entry);
        }

        foreach (var key in orphanedEntries)
        {
            if (_entries.TryRemove(key, out var entry))
                RemoveFromTrackIndex(entry.TrackId, key);
        }

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
                    try { file.Delete(); Log.Debug($"[AudioCache] Deleted orphaned file: {file.Name}"); }
                    catch { }
                }
            }
        }

        if (orphanedEntries.Count > 0)
        {
            Log.Info($"[AudioCache] Validation: removed {orphanedEntries.Count} orphaned entries");
            await SaveIndexAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Проверяет, что файл кэша, помеченный как <see cref="CacheEntry.IsComplete"/>,
    /// физически существует на диске и его размер ≥ <see cref="CacheEntry.TotalSize"/>.
    ///
    /// <para><b>Почему необходимо:</b> при crash/kill процесса между установкой
    /// <c>IsComplete = true</c> (in-memory) и физическим flush последних чанков
    /// на диск, индекс кэша сохраняется с <c>IsComplete</c>, а файл остаётся
    /// усечённым. Без этой проверки <see cref="Sources.LocalFileSource"/> открывает
    /// truncated файл, и парсер падает с <see cref="EndOfStreamException"/>
    /// при seek за границу реальных данных.</para>
    ///
    /// <para><b>Побочные эффекты при невалидности:</b></para>
    /// <list type="bullet">
    ///   <item><c>IsComplete</c> сбрасывается в <c>false</c></item>
    ///   <item>Битовая маска чанков обнуляется</item>
    ///   <item>Повреждённый файл удаляется с диска</item>
    ///   <item>Индекс асинхронно пересохраняется</item>
    /// </list>
    /// <para>Вызывающий код (FindBestCache, IsFullyCached и т.д.) получает <c>false</c>
    /// и не предлагает эту entry как готовую. <see cref="AudioSourceFactory"/> при следующем
    /// воспроизведении создаст <see cref="Sources.CachingStreamSource"/> и скачает трек заново.</para>
    /// </summary>
    /// <param name="entry">Запись кэша для проверки. Должна иметь <c>IsComplete == true</c>.</param>
    /// <returns>
    /// <c>true</c> — файл валиден, можно использовать.<br/>
    /// <c>false</c> — файл отсутствует или усечён, entry инвалидирована.
    /// </returns>
    private bool EnsureCacheFileIntegrity(CacheEntry entry)
    {
        if (!entry.IsComplete) return false;

        var filePath = GetCachePath(entry.CacheKey);

        try
        {
            var fi = new FileInfo(filePath);

            if (fi.Exists && fi.Length >= entry.TotalSize)
                return true;

            long actualSize = fi.Exists ? fi.Length : 0;

            Log.Warn($"[AudioCache] ⚠ Truncated cache invalidated: {entry.CacheKey} " +
                     $"(disk={actualSize / 1024}KB, expected={entry.TotalSize / 1024}KB). " +
                     $"Track will re-download on next play.");

            // Сброс метаданных — entry остаётся в словаре, но больше
            // не проходит фильтр IsComplete ни в одном lookup-методе.
            entry.IsComplete = false;
            entry.CompletedAt = null;
            entry.ActualFileSize = 0;
            entry.ResetChunkMask();

            // Удаление повреждённого файла — CachingStreamSource создаст новый.
            if (fi.Exists)
            {
                try { fi.Delete(); }
                catch (Exception ex)
                {
                    Log.Warn($"[AudioCache] Failed to delete truncated file: {ex.Message}");
                }
            }

            _ = SaveIndexAsync();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Integrity check I/O error for {entry.CacheKey}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Принудительно инвалидирует конкретный чанк в кэше.
    /// Используется Self-Healing конвейером при обнаружении мусора парсером.
    /// </summary>
    public void InvalidateChunk(string cacheKey, int chunkIndex)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            // Сбрасываем флаг завершенности, чтобы разрешить запись исправленного чанка на диск
            entry.IsComplete = false;
            entry.CompletedAt = null;
            entry.InvalidateChunk(chunkIndex);

            // Синхронизируем размер файла на диске
            UpdateFileSizeCache(entry);

            // Асинхронно сохраняем индекс, чтобы при перезапуске битый чанк был скачан заново
            _ = SaveIndexAsync();
            Log.Info($"[AudioCache] Chunk {chunkIndex} invalidated for {cacheKey} due to corruption.");
        }
    }

    /// <summary>
    /// Выполняет точечную хирургическую инвалидацию конкретного чанка для лучшего формата указанного trackId.
    /// Переводит запись в статус незавершённой, сохраняя сам файл на диске для последующего патчинга.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="chunkIndex">Индекс повреждённого чанка.</param>
    public void InvalidateCacheChunkByTrackId(string trackId, int chunkIndex)
    {
        if (string.IsNullOrEmpty(trackId)) return;

        var entry = FindBestCache(trackId);
        if (entry != null)
        {
            InvalidateCacheChunk(entry.CacheKey, chunkIndex);
        }
    }

    /// <summary>
    /// Выполняет точечную хирургическую инвалидацию конкретного чанка по его cacheKey.
    /// Переводит запись в статус незавершённой, сохраняя сам файл на диске для последующего патчинга.
    /// </summary>
    /// <param name="cacheKey">Уникальный ключ кэша.</param>
    /// <param name="chunkIndex">Индекс повреждённого чанка.</param>
    public void InvalidateCacheChunk(string cacheKey, int chunkIndex)
    {
        if (string.IsNullOrEmpty(cacheKey)) return;

        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            entry.IsComplete = false;
            entry.CompletedAt = null;
            entry.InvalidateChunk(chunkIndex);

            // Обновляем ActualFileSize, но файл на диске сохраняем нетронутым
            UpdateFileSizeCache(entry);

            _ = SaveIndexAsync();
            Log.Info($"[AudioCache] Surgical Invalidation: Key {cacheKey}, Chunk {chunkIndex} marked corrupt. File kept intact for stream-patching.");
        }
    }

    #endregion

    #region Private Helpers

    private void AddToTrackIndex(string trackId, string cacheKey)
    {
        var keys = _trackIndex.GetOrAdd(trackId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 1);
    }

    private void RemoveFromTrackIndex(string trackId, string cacheKey)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;
        keys.TryRemove(cacheKey, out _);
        if (keys.IsEmpty) _trackIndex.TryRemove(trackId, out _);
    }

    /// <summary>
    /// Считывает физический размер файла кэша на диске и обновляет свойство ActualFileSize записи.
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
            else
            {
                // Защита: сбрасываем в 0, если файл был удалён вручную во время сессии
                entry.ActualFileSize = 0;
            }
        }
        catch { }
    }

    private void RaiseFormatCached(CacheEntry entry)
    {
        try { OnFormatCached?.Invoke(entry.TrackId, entry.Format.ToString(), entry.Bitrate, false); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnFormatCached handler error: {ex.Message}"); }
    }

    private void LoadIndex()
    {
        var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
        if (!File.Exists(indexPath)) return;

        try
        {
            var json = File.ReadAllText(indexPath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.CacheKey)) continue;

                    if (File.Exists(GetCachePath(entry.CacheKey)))
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
        catch (Exception ex) { Log.Warn($"[AudioCache] Failed to load index: {ex.Message}"); }
    }

    private async Task SaveIndexAsync()
    {
        if (_disposed) return;
        if (!await _saveLock.WaitAsync(CacheSaveLockTimeoutMs).ConfigureAwait(false)) return;

        try
        {
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            var entries = _entries.Values.ToList();

            foreach (var entry in entries)
                entry.SaveChunkMask();

            var json = JsonSerializer.Serialize(entries, s_jsonOptions);
            await File.WriteAllTextAsync(indexPath, json).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn($"[AudioCache] Failed to save index: {ex.Message}"); }
        finally { _saveLock.Release(); }
    }

    private async Task AutoSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CacheAutoSaveIntervalMs, ct).ConfigureAwait(false);
                await SaveIndexAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"[AudioCache] Auto-save error: {ex.Message}"); }
        }
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();
        try { _autoSaveTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { SaveIndexAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();
        try { await _autoSaveTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        await SaveIndexAsync().ConfigureAwait(false);
        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    #endregion
}

/// Представляет запись метаданных кэша для конкретного аудиопотока.
/// </summary>
public sealed class CacheEntry
{
    /// <summary>Уникальный ключ кэша (trackId + format + normalized_bitrate).</summary>
    public string CacheKey { get; init; } = "";

    /// <summary>Идентификатор трека.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>Исходный URL, с которого производилось скачивание.</summary>
    public string OriginalUrl { get; set; } = "";

    /// <summary>
    /// Полный размер контента в байтах.
    /// <para>Допускает изменение, если на диске ещё нет скачанных чанков.</para>
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>Формат аудио-контейнера.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Аудио-кодек.</summary>
    public AudioCodec Codec { get; set; }

    /// <summary>Реальный битрейт в kbps.</summary>
    public int Bitrate { get; set; }

    /// <summary>Длительность трека в миллисекундах.</summary>
    public long DurationMs { get; set; } = -1;

    /// <summary>
    /// Размер одного чанка в байтах.
    /// <para>Допускает изменение для пустых записей кэша при смене интернет-профиля.</para>
    /// </summary>
    public int ChunkSize { get; set; }

    /// <summary>
    /// Общее количество чанков в контенте.
    /// <para>Допускает изменение при перерасчёте сетки чанков пустого файла.</para>
    /// </summary>
    public int TotalChunks { get; set; }

    private int _downloadedChunks;

    /// <summary>Количество успешно скачанных и записанных на диск чанков.</summary>
    public int DownloadedChunks
    {
        get => Volatile.Read(ref _downloadedChunks);
        set => Volatile.Write(ref _downloadedChunks, value);
    }

    /// <summary>Дата и время создания записи.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Дата и время последнего обращения к записи.</summary>
    public DateTime LastAccessedAt { get; set; }

    /// <summary>Дата и время полного завершения кэширования.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Флаг полной готовности локального кэша.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Фактический размер файла кэша на диске.</summary>
    public long ActualFileSize { get; set; }

    /// <summary>Строковое представление битовой маски чанков в формате Base64.</summary>
    public string? ChunkMaskData { get; set; }

    [JsonIgnore] private int[]? _chunkBits;
    private ConcurrentDictionary<int, byte>? _corruptedOfflineChunks;

    /// <summary>Прогресс загрузки в процентах (0.0 - 100.0).</summary>
    [JsonIgnore]
    public double DownloadProgress =>
        TotalChunks == 0 ? 0 : (double)DownloadedChunks / TotalChunks * 100;

    /// <summary>Проверяет, загружен ли чанк с заданным индексом. Потокобезопасно.</summary>
    public bool IsChunkDownloaded(int index)
    {
        EnsureChunkBits();
        if (index < 0 || index >= TotalChunks) return false;
        return (Volatile.Read(ref _chunkBits![index >> 5]) & (1 << (index & 31))) != 0;
    }

    /// <summary>Помечает чанк загруженным через CAS-цикл. Потокобезопасно.</summary>
    public void MarkChunkDownloaded(int index)
    {
        EnsureChunkBits();
        if (index < 0 || index >= TotalChunks) return;

        int word = index >> 5;
        int bit = 1 << (index & 31);
        int current = Volatile.Read(ref _chunkBits![word]);

        if ((current & bit) != 0) return;

        int original;
        do
        {
            original = current;
            current = Interlocked.CompareExchange(ref _chunkBits![word], original | bit, original);
        } while (current != original);

        if ((original & bit) == 0)
            Interlocked.Increment(ref _downloadedChunks);
    }

    /// <summary>
    /// Сбрасывает флаг загрузки для одного чанка через CAS-цикл.
    /// </summary>
    public void InvalidateChunk(int index)
    {
        EnsureChunkBits();
        if (index < 0 || index >= TotalChunks) return;

        int word = index >> 5;
        int bit = 1 << (index & 31);
        int current = Volatile.Read(ref _chunkBits![word]);

        if ((current & bit) == 0) return;

        int original;
        do
        {
            original = current;
            current = Interlocked.CompareExchange(ref _chunkBits![word], original & ~bit, original);
        } while (current != original);

        if ((original & bit) != 0)
            Interlocked.Decrement(ref _downloadedChunks);
    }

    /// <summary>
    /// Помечает чанк как "безнадежно испорченный" для текущей оффлайн-сессии.
    /// Предотвращает бесконечные попытки парсера прочитать мусор, если нет сети для восстановления.
    /// </summary>
    public void MarkChunkCorruptedOffline(int index)
    {
        _corruptedOfflineChunks ??= new ConcurrentDictionary<int, byte>();
        _corruptedOfflineChunks.TryAdd(index, 1);
        InvalidateChunk(index); // Сбрасываем основной бит
    }

    /// <summary>
    /// Проверяет, был ли чанк помечен как мертвый в оффлайне.
    /// </summary>
    public bool IsChunkCorruptedOffline(int index) =>
        _corruptedOfflineChunks != null && _corruptedOfflineChunks.ContainsKey(index);

    /// <summary>
    /// Сериализует битовую маску чанков в Base64 для сохранения в JSON-индекс.
    /// Использует <see cref="ArrayPool{T}.Shared"/> для промежуточного буфера.
    /// </summary>
    public void SaveChunkMask()
    {
        if (_chunkBits == null) return;

        int byteCount = _chunkBits.Length * sizeof(int);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Buffer.BlockCopy(_chunkBits, 0, rented, 0, byteCount);
            ChunkMaskData = Convert.ToBase64String(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Восстанавливает битовую маску из Base64 и пересчитывает <see cref="DownloadedChunks"/>.</summary>
    public void RestoreChunkMask()
    {
        if (string.IsNullOrEmpty(ChunkMaskData)) return;

        EnsureChunkBits();

        try
        {
            var bytes = Convert.FromBase64String(ChunkMaskData);
            Buffer.BlockCopy(bytes, 0, _chunkBits!, 0, Math.Min(bytes.Length, _chunkBits!.Length * sizeof(int)));

            int count = 0;
            for (int i = 0; i < TotalChunks; i++)
                if (IsChunkDownloaded(i)) count++;

            Volatile.Write(ref _downloadedChunks, count);
        }
        catch { }
    }

    /// <summary>
    /// Сбрасывает маску чанков и обнуляет прогресс загрузки.
    /// <para>Необходим при изменении размера чанков для пустого элемента кэша во избежание рассинхронизации битовых индексов.</para>
    /// </summary>
    public void ResetChunkMask()
    {
        _chunkBits = null;
        Volatile.Write(ref _downloadedChunks, 0);
        ChunkMaskData = null;
    }

    private void EnsureChunkBits()
    {
        _chunkBits ??= new int[(TotalChunks + 31) >> 5];
    }
}

public readonly struct CacheStats
{
    public int TotalEntries { get; init; }
    public int CompleteEntries { get; init; }
    public int PartialEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public long MaxSizeBytes { get; init; }

    public double UsagePercent =>
        MaxSizeBytes == 0 ? 0 : (double)TotalSizeBytes / MaxSizeBytes * 100;

    public string TotalSizeFormatted => FormatSize(TotalSizeBytes);
    public string MaxSizeFormatted => FormatSize(MaxSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}