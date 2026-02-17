using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Cache;

public sealed class AudioCacheManager : IAsyncDisposable, IDisposable
{
    private readonly string _cacheDirectory;
    private readonly long _maxCacheSize;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly CancellationTokenSource _timerCts = new();
    private readonly Task _autoSaveTask;

    private volatile bool _disposed;

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

    public bool IsFullyCached(string cacheKey)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry))
            return false;

        if (!entry.IsComplete)
            return false;

        var filePath = GetCachePath(cacheKey);
        if (!File.Exists(filePath))
            return false;

        var fileInfo = new FileInfo(filePath);
        return fileInfo.Length >= entry.TotalSize;
    }

    public CacheEntry? FindBestCache(string trackId)
    {
        return _entries.Values
            .Where(e => e.TrackId == trackId && e.IsComplete)
            .OrderByDescending(e => e.Bitrate)
            .FirstOrDefault(e =>
            {
                var path = GetCachePath(e.CacheKey);
                return File.Exists(path) && new FileInfo(path).Length >= e.TotalSize;
            });
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

            Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
            _ = SaveIndexAsync();
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
                Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
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
        if (_entries.TryRemove(cacheKey, out _))
        {
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
        long totalSize = CalculateTotalCacheSize();

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

            var filePath = GetCachePath(entry.CacheKey);
            if (File.Exists(filePath))
            {
                totalSize -= new FileInfo(filePath).Length;
            }

            RemoveCache(entry.CacheKey);
        }

        Log.Info($"[AudioCache] Cleanup complete, new size: {totalSize / 1024 / 1024}MB");
    }

    public CacheStats GetStats()
    {
        long totalSize = 0;
        int completeCount = 0;
        int partialCount = 0;

        foreach (var entry in _entries.Values)
        {
            var filePath = GetCachePath(entry.CacheKey);
            if (File.Exists(filePath))
            {
                totalSize += new FileInfo(filePath).Length;
            }

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

    #endregion

    #region Private Methods

    private long CalculateTotalCacheSize()
    {
        long totalSize = 0;

        foreach (var entry in _entries.Values)
        {
            var filePath = GetCachePath(entry.CacheKey);
            if (File.Exists(filePath))
            {
                totalSize += new FileInfo(filePath).Length;
            }
        }

        return totalSize;
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
                        _entries.TryAdd(entry.CacheKey, entry);
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