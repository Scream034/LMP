using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Helpers;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник с chunk-based кэшированием.
/// RAM буфер с выгрузкой на диск.
/// </summary>
public sealed class CachedStreamSource : IAudioSource
{
    #region Constants
    
    private const int DefaultChunkSize = 256 * 1024; // 256KB
    private const int MaxRamChunks = 32;
    private const int MaxConcurrentDownloads = 4;
    private const int DownloadTimeoutMs = 15000;
    private const int UrlRefreshIntervalMs = 300_000;
    
    #endregion
    
    #region Fields
    
    private readonly string _cacheId;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;
    private readonly WebMParser _parser;
    
    private string _currentUrl;
    private DateTime _urlFetchedAt;
    private readonly long _contentLength;
    private readonly int _chunkSize;
    private readonly int _totalChunks;
    
    // Chunk storage
    private readonly ConcurrentDictionary<int, byte[]> _ramChunks = new();
    private readonly ConcurrentBitArray _downloadedChunks;
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSlots;
    
    // State
    private long _position;
    private long _durationMs = -1;
    private long _positionMs;
    private int _currentFrameChunk;
    private volatile bool _initialized;
    private volatile bool _disposed;
    
    // Buffering
    private readonly Queue<AudioFrame> _frameBuffer = new();
    private readonly object _bufferLock = new();
    
    // Cancellation
    private CancellationTokenSource? _downloadCts;
    private Task? _preloadTask;
    
    // Stats
    private long _bytesDownloaded;
    private int _ramChunkCount;
    
    // Cache path
    private readonly string? _cachePath;
    
    #endregion
    
    #region Properties
    
    public long DurationMs => _durationMs;
    public long PositionMs => Interlocked.Read(ref _positionMs);
    public bool CanSeek => true;
    public AudioCodec Codec => AudioCodec.Opus;
    
    public double BufferProgress => _totalChunks == 0 
        ? 0 
        : Math.Min((double)_downloadedChunks.PopCount() / _totalChunks * 100, 100);
    
    public bool IsFullyBuffered => _downloadedChunks.PopCount() >= _totalChunks;
    
    #endregion
    
    public CachedStreamSource(
        string cacheId,
        string url,
        long contentLength,
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        string? cachePath = null,
        int chunkSize = DefaultChunkSize)
    {
        if (contentLength <= 0)
            throw new ArgumentException("Content length must be positive", nameof(contentLength));
        
        _cacheId = cacheId;
        _currentUrl = url;
        _contentLength = contentLength;
        _httpClient = httpClient;
        _urlRefresher = urlRefresher;
        _cachePath = cachePath;
        _chunkSize = chunkSize;
        
        _totalChunks = (int)Math.Ceiling((double)contentLength / chunkSize);
        _downloadedChunks = new ConcurrentBitArray(_totalChunks);
        _downloadSlots = new SemaphoreSlim(MaxConcurrentDownloads);
        _urlFetchedAt = DateTime.UtcNow;
        
        // Create parser with a placeholder stream (will be replaced)
        _parser = new WebMParser(new ChunkReadStream(this));
        
        Log.Debug($"[CachedSource] Created: {_totalChunks} chunks × {chunkSize / 1024}KB");
    }
    
    #region IAudioSource Implementation
    
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;
        
        try
        {
            // Try load from disk cache
            if (await TryLoadFromCacheAsync(ct))
            {
                Log.Info($"[CachedSource] Loaded from cache: {_cacheId}");
                _initialized = true;
                return true;
            }
            
            // Download initial chunks
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            
            // Download first few chunks for parsing
            for (int i = 0; i < Math.Min(4, _totalChunks); i++)
            {
                await DownloadChunkAsync(i, _downloadCts.Token);
            }
            
            // Parse WebM headers
            if (!await _parser.ParseHeadersAsync(ct))
            {
                throw new AudioSourceException("Failed to parse WebM headers");
            }
            
            _durationMs = _parser.DurationMs;
            _initialized = true;
            
            // Start preload
            _preloadTask = Task.Run(() => PreloadLoopAsync(_downloadCts.Token), _downloadCts.Token);
            
            Log.Info($"[CachedSource] Initialized: duration={_durationMs}ms");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachedSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }
    
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!_initialized)
            throw new InvalidOperationException("Not initialized");
        
        // Check buffer first
        lock (_bufferLock)
        {
            if (_frameBuffer.Count > 0)
            {
                var frame = _frameBuffer.Dequeue();
                Interlocked.Exchange(ref _positionMs, frame.TimestampMs);
                return frame;
            }
        }
        
        // Read from parser
        try
        {
            var block = await _parser.ReadNextBlockAsync(ct);
            
            if (block == null)
                return null;
            
            Interlocked.Exchange(ref _positionMs, block.Value.TimestampMs);
            
            return new AudioFrame
            {
                Data = block.Value.Data,
                TimestampMs = block.Value.TimestampMs,
                DurationMs = 20,
                IsKeyFrame = block.Value.IsKeyFrame
            };
        }
        catch (HttpRequestException ex) when (IsUrlExpired(ex))
        {
            await RefreshUrlAsync(ct);
            return await ReadFrameAsync(ct);
        }
    }
    
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_totalChunks == 0 || _durationMs <= 0) return false;
        
        // Find byte position
        var bytePosition = _parser.FindSeekPosition(positionMs);
        if (bytePosition == null && _contentLength > 0)
        {
            bytePosition = (long)(_contentLength * ((double)positionMs / _durationMs));
        }
        
        if (bytePosition == null) return false;
        
        int targetChunk = (int)(bytePosition.Value / _chunkSize);
        targetChunk = Math.Clamp(targetChunk, 0, _totalChunks - 1);
        
        Log.Debug($"[CachedSource] Seek to {positionMs}ms → chunk {targetChunk}");
        
        // Ensure chunk is downloaded
        if (!HasChunk(targetChunk))
        {
            await DownloadChunkAsync(targetChunk, ct);
        }
        
        _currentFrameChunk = targetChunk;
        Interlocked.Exchange(ref _position, bytePosition.Value);
        Interlocked.Exchange(ref _positionMs, positionMs);
        
        lock (_bufferLock)
        {
            _frameBuffer.Clear();
        }
        
        return true;
    }
    
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_totalChunks == 0) return [];
        
        var ranges = new List<(double, double)>();
        int? rangeStart = null;
        
        for (int i = 0; i < _totalChunks; i++)
        {
            if (HasChunk(i))
            {
                rangeStart ??= i;
            }
            else if (rangeStart.HasValue)
            {
                double start = (double)rangeStart.Value / _totalChunks;
                double end = (double)i / _totalChunks;
                ranges.Add((start, end));
                rangeStart = null;
            }
        }
        
        if (rangeStart.HasValue)
        {
            ranges.Add(((double)rangeStart.Value / _totalChunks, 1.0));
        }
        
        return ranges;
    }
    
    public void ReleaseRamBuffers()
    {
        int current = _currentFrameChunk;
        int released = 0;
        
        foreach (var idx in _ramChunks.Keys.Where(i => Math.Abs(i - current) > 5).ToList())
        {
            if (_ramChunks.TryRemove(idx, out _))
            {
                Interlocked.Decrement(ref _ramChunkCount);
                released++;
            }
        }
        
        if (released > 0)
        {
            GC.Collect(1, GCCollectionMode.Optimized, false);
            Log.Debug($"[CachedSource] Released {released} RAM chunks");
        }
    }
    
    public void CancelPendingOperations()
    {
        _downloadCts?.Cancel();
    }
    
    #endregion
    
    #region Chunk Management
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasChunk(int index) => _downloadedChunks.Get(index);
    
    internal byte[]? GetChunkData(int index)
    {
        if (_ramChunks.TryGetValue(index, out var data))
            return data;
        
        // Try disk
        data = ReadChunkFromDisk(index);
        if (data != null)
        {
            TryPromoteToRam(index, data);
            return data;
        }
        
        return null;
    }
    
    internal int ReadAt(long position, byte[] buffer, int offset, int count)
    {
        if (position >= _contentLength) return 0;
        
        int chunkIndex = (int)(position / _chunkSize);
        int offsetInChunk = (int)(position % _chunkSize);
        
        if (!HasChunk(chunkIndex))
        {
            // Synchronous download for Read()
            DownloadChunkAsync(chunkIndex, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        
        var chunkData = GetChunkData(chunkIndex);
        if (chunkData == null) return 0;
        
        int available = Math.Min(count, chunkData.Length - offsetInChunk);
        if (available <= 0) return 0;
        
        Buffer.BlockCopy(chunkData, offsetInChunk, buffer, offset, available);
        return available;
    }
    
    private async Task DownloadChunkAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _totalChunks) return;
        if (HasChunk(index)) return;
        if (_activeDownloads.ContainsKey(index)) return;
        
        var task = DownloadChunkInternalAsync(index, ct);
        _activeDownloads.TryAdd(index, task);
        
        try
        {
            await task;
        }
        finally
        {
            _activeDownloads.TryRemove(index, out _);
        }
    }
    
    private async Task DownloadChunkInternalAsync(int index, CancellationToken ct)
    {
        bool gotSlot = false;
        
        try
        {
            gotSlot = await _downloadSlots.WaitAsync(500, ct);
            if (!gotSlot || HasChunk(index)) return;
            
            long start = (long)index * _chunkSize;
            long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);
            
            await EnsureFreshUrlAsync(ct);
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeoutMs);
            
            using var request = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            
            using var response = await _httpClient.SendAsync(
                request, 
                HttpCompletionOption.ResponseHeadersRead, 
                cts.Token);
            
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await RefreshUrlAsync(ct);
                return; // Retry on next attempt
            }
            
            response.EnsureSuccessStatusCode();
            
            var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
            StoreChunk(index, data);
            
            Log.Trace($"[CachedSource] Chunk {index}: {data.Length} bytes");
        }
        catch (OperationCanceledException) { /* ignore */ }
        catch (Exception ex)
        {
            Log.Warn($"[CachedSource] Chunk {index} failed: {ex.Message}");
        }
        finally
        {
            if (gotSlot)
            {
                try { _downloadSlots.Release(); } catch { }
            }
        }
    }
    
    private void StoreChunk(int index, byte[] data)
    {
        _downloadedChunks.Set(index, true);
        Interlocked.Add(ref _bytesDownloaded, data.Length);
        
        if (_ramChunks.TryAdd(index, data))
        {
            Interlocked.Increment(ref _ramChunkCount);
        }
        
        WriteChunkToDisk(index, data);
        
        // Evict old chunks if over limit
        if (Volatile.Read(ref _ramChunkCount) > MaxRamChunks)
        {
            EvictOldChunks();
        }
    }
    
    private void TryPromoteToRam(int index, byte[] data)
    {
        if (Volatile.Read(ref _ramChunkCount) < MaxRamChunks && _ramChunks.TryAdd(index, data))
        {
            Interlocked.Increment(ref _ramChunkCount);
        }
    }
    
    private void EvictOldChunks()
    {
        int center = _currentFrameChunk;
        
        var toEvict = _ramChunks.Keys
            .Where(i => i < center - 4 || i > center + 16)
            .OrderByDescending(i => Math.Abs(i - center))
            .Take(MaxRamChunks / 4)
            .ToList();
        
        foreach (var idx in toEvict)
        {
            if (_ramChunks.TryRemove(idx, out _))
            {
                Interlocked.Decrement(ref _ramChunkCount);
            }
        }
    }
    
    #endregion
    
    #region Disk Cache
    
    private void WriteChunkToDisk(int index, byte[] data)
    {
        if (string.IsNullOrEmpty(_cachePath)) return;
        
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            long offset = (long)index * _chunkSize;
            
            using var fs = new FileStream(
                _cachePath, 
                FileMode.OpenOrCreate, 
                FileAccess.Write, 
                FileShare.ReadWrite);
            
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachedSource] Write chunk {index} failed: {ex.Message}");
        }
    }
    
    private byte[]? ReadChunkFromDisk(int index)
    {
        if (string.IsNullOrEmpty(_cachePath) || !File.Exists(_cachePath))
            return null;
        
        try
        {
            long start = (long)index * _chunkSize;
            int size = (int)Math.Min(_chunkSize, _contentLength - start);
            
            using var fs = new FileStream(
                _cachePath, 
                FileMode.Open, 
                FileAccess.Read, 
                FileShare.ReadWrite);
            
            if (fs.Length < start + size) return null;
            
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[size];
            
            return fs.Read(buffer, 0, size) == size ? buffer : null;
        }
        catch
        {
            return null;
        }
    }
    
    private async Task<bool> TryLoadFromCacheAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_cachePath) || !File.Exists(_cachePath))
            return false;
        
        try
        {
            var info = new FileInfo(_cachePath);
            if (info.Length != _contentLength)
                return false;
            
            // Mark all chunks as downloaded
            for (int i = 0; i < _totalChunks; i++)
            {
                _downloadedChunks.Set(i, true);
            }
            
            Volatile.Write(ref _bytesDownloaded, _contentLength);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    #endregion
    
    #region URL Management
    
    private async Task EnsureFreshUrlAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _urlFetchedAt).TotalMilliseconds < UrlRefreshIntervalMs)
            return;
        
        await RefreshUrlAsync(ct);
    }
    
    private async Task RefreshUrlAsync(CancellationToken ct)
    {
        if (_urlRefresher == null) return;
        
        try
        {
            var newUrl = await _urlRefresher(ct);
            if (!string.IsNullOrEmpty(newUrl))
            {
                _currentUrl = newUrl;
                _urlFetchedAt = DateTime.UtcNow;
                Log.Debug("[CachedSource] URL refreshed");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachedSource] URL refresh failed: {ex.Message}");
        }
    }
    
    private static bool IsUrlExpired(HttpRequestException ex)
    {
        return ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone;
    }
    
    #endregion
    
    #region Preload
    
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct);
                
                int current = _currentFrameChunk;
                
                // Download ahead
                for (int i = 0; i < 8 && current + i < _totalChunks; i++)
                {
                    if (!HasChunk(current + i) && !_activeDownloads.ContainsKey(current + i))
                    {
                        _ = DownloadChunkAsync(current + i, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"[CachedSource] Preload error: {ex.Message}");
            }
        }
    }
    
    #endregion
    
    #region Dispose
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
        _parser.Dispose();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _downloadCts?.Cancel();
        
        if (_preloadTask != null)
        {
            try { await _preloadTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { /* ignore */ }
        }
        
        _downloadCts?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
        _parser.Dispose();
    }
    
    #endregion
    
    #region ChunkReadStream
    
    /// <summary>
    /// Stream wrapper for WebMParser that reads from chunks.
    /// </summary>
    private sealed class ChunkReadStream : Stream
    {
        private readonly CachedStreamSource _source;
        private long _position;
        
        public ChunkReadStream(CachedStreamSource source) => _source = source;
        
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _source._contentLength;
        
        public override long Position
        {
            get => _position;
            set => _position = value;
        }
        
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.ReadAt(_position, buffer, offset, count);
            _position += read;
            return read;
        }
        
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _source._contentLength + offset,
                _ => _position
            };
            return _position;
        }
        
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    
    #endregion
}

/// <summary>
/// Lock-free bit array для отслеживания загруженных чанков.
/// </summary>
internal sealed class ConcurrentBitArray
{
    private readonly int[] _data;
    private readonly int _length;
    
    public ConcurrentBitArray(int length)
    {
        _length = length;
        _data = new int[(length + 31) / 32];
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index)
    {
        if ((uint)index >= (uint)_length) return false;
        return (Volatile.Read(ref _data[index >> 5]) & (1 << (index & 31))) != 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, bool value)
    {
        if ((uint)index >= (uint)_length) return;
        
        int word = index >> 5;
        int bit = 1 << (index & 31);
        int current, desired;
        
        do
        {
            current = Volatile.Read(ref _data[word]);
            desired = value ? (current | bit) : (current & ~bit);
            if (desired == current) return;
        } while (Interlocked.CompareExchange(ref _data[word], desired, current) != current);
    }
    
    public int PopCount()
    {
        int count = 0;
        for (int i = 0; i < _data.Length; i++)
        {
            count += BitOperations.PopCount((uint)Volatile.Read(ref _data[i]));
        }
        return Math.Min(count, _length);
    }
}