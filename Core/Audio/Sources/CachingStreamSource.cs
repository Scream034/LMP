// Core/Audio/Sources/CachingStreamSource.cs

using System.Collections.Concurrent;
using System.Net;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник с полной поддержкой кэширования.
/// </summary>
public sealed class CachingStreamSource : IAudioSource
{
    private const int ChunkSize = 256 * 1024;
    private const int MaxConcurrentDownloads = 4;
    private const int DownloadTimeoutMs = 15000;
    
    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly AudioFormat _format;
    private readonly HttpClient _httpClient;
    private readonly AudioCacheManager _cacheManager;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;
    
    private CacheEntry? _cacheEntry;
    private IContainerParser? _parser;
    private Stream? _readStream;
    
    private readonly ConcurrentDictionary<int, byte[]> _ramChunks = new();
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSlots;
    
    private int _currentChunk;
    private long _positionMs;
    private string _currentUrl;
    private volatile bool _initialized;
    private volatile bool _disposed;
    private volatile bool _isOfflineMode;
    
    private CancellationTokenSource? _downloadCts;
    private Task? _preloadTask;
    
    public long DurationMs => _parser?.DurationMs ?? _cacheEntry?.DurationMs ?? -1;
    public long PositionMs => Interlocked.Read(ref _positionMs);
    public bool CanSeek => true;
    public AudioCodec Codec { get; private set; }
    
    public double BufferProgress => _cacheEntry?.DownloadProgress ?? 0;
    public bool IsFullyBuffered => _cacheEntry?.IsComplete ?? false;
    public bool IsOfflineMode => _isOfflineMode;
    
    public CachingStreamSource(
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _trackId = trackId;
        _url = url;
        _currentUrl = url;
        _contentLength = contentLength;
        _format = format;
        _httpClient = httpClient;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;
        
        _downloadSlots = new SemaphoreSlim(MaxConcurrentDownloads);
        
        Codec = AudioSourceFactory.GetCodecForFormat(format);
    }
    
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;
        
        try
        {
            // Проверяем полный кэш
            if (_cacheManager.IsFullyCached(_trackId))
            {
                Log.Info($"[CachingSource] Using fully cached: {_trackId}");
                _isOfflineMode = true;
                return await InitializeFromCacheAsync(ct);
            }
            
            // Создаём запись кэша
            _cacheEntry = _cacheManager.CreateOrUpdate(
                _trackId, _url, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format));
            
            if (_cacheEntry.DownloadedChunks > 0)
            {
                Log.Info($"[CachingSource] Resuming: {_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks} chunks");
            }
            
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            
            // Загружаем начальные чанки
            for (int i = 0; i < Math.Min(4, _cacheEntry.TotalChunks); i++)
            {
                await EnsureChunkAsync(i, _downloadCts.Token);
            }
            
            _readStream = new CachingReadStream(this);
            
            _parser = _format switch
            {
                AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(_readStream),
                AudioFormat.Mp4 => new Mp4ContainerParser(_readStream),
                _ => throw new NotSupportedException($"Format not supported: {_format}")
            };
            
            if (!await _parser.ParseHeadersAsync(ct))
            {
                throw new InvalidOperationException("Failed to parse headers");
            }
            
            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            
            _initialized = true;
            
            _preloadTask = Task.Run(() => PreloadLoopAsync(_downloadCts.Token));
            
            Log.Info($"[CachingSource] Initialized: duration={DurationMs}ms, cached={_cacheEntry.DownloadProgress:F0}%");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachingSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }
    
    private async Task<bool> InitializeFromCacheAsync(CancellationToken ct)
    {
        var stream = _cacheManager.OpenCachedStream(_trackId);
        if (stream == null)
        {
            _isOfflineMode = false;
            return await InitializeAsync(ct);
        }
        
        _readStream = stream;
        
        _parser = _format switch
        {
            AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(_readStream),
            AudioFormat.Mp4 => new Mp4ContainerParser(_readStream),
            _ => throw new NotSupportedException($"Format: {_format}")
        };
        
        if (!await _parser.ParseHeadersAsync(ct))
        {
            return false;
        }
        
        Codec = _parser.Codec;
        _initialized = true;
        
        return true;
    }
    
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Not initialized");
        
        try
        {
            var frame = await _parser.ReadNextFrameAsync(ct);
            
            if (frame == null)
                return null;
            
            Interlocked.Exchange(ref _positionMs, frame.Value.TimestampMs);
            
            if (!_isOfflineMode && _readStream is CachingReadStream crs)
            {
                _currentChunk = (int)(crs.Position / ChunkSize);
            }
            
            return frame;
        }
        catch (IOException) when (!_disposed && !_isOfflineMode)
        {
            await EnsureChunkAsync(_currentChunk, ct);
            return await ReadFrameAsync(ct);
        }
    }
    
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null) return false;
        
        var seekInfo = _parser.FindSeekPosition(positionMs);
        
        if (seekInfo == null && _contentLength > 0 && DurationMs > 0)
        {
            long approxPosition = (long)(_contentLength * ((double)positionMs / DurationMs));
            seekInfo = (approxPosition, positionMs);
        }
        
        if (seekInfo == null) return false;
        
        int targetChunk = (int)(seekInfo.Value.BytePosition / ChunkSize);
        
        if (!_isOfflineMode && _cacheEntry != null)
        {
            for (int i = targetChunk; i < Math.Min(targetChunk + 3, _cacheEntry.TotalChunks); i++)
            {
                await EnsureChunkAsync(i, ct);
            }
        }
        
        _readStream!.Position = seekInfo.Value.BytePosition;
        _currentChunk = targetChunk;
        _parser.Reset();
        
        Interlocked.Exchange(ref _positionMs, positionMs);
        
        return true;
    }
    
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_isOfflineMode || _cacheEntry == null)
            return [(0.0, 1.0)];
        
        var ranges = new List<(double, double)>();
        int? rangeStart = null;
        
        for (int i = 0; i < _cacheEntry.TotalChunks; i++)
        {
            if (_cacheEntry.IsChunkDownloaded(i))
            {
                rangeStart ??= i;
            }
            else if (rangeStart.HasValue)
            {
                ranges.Add(((double)rangeStart.Value / _cacheEntry.TotalChunks,
                            (double)i / _cacheEntry.TotalChunks));
                rangeStart = null;
            }
        }
        
        if (rangeStart.HasValue)
        {
            ranges.Add(((double)rangeStart.Value / _cacheEntry.TotalChunks, 1.0));
        }
        
        return ranges;
    }
    
    public void ReleaseRamBuffers()
    {
        int current = _currentChunk;
        
        foreach (var idx in _ramChunks.Keys.Where(i => Math.Abs(i - current) > 5).ToList())
        {
            _ramChunks.TryRemove(idx, out _);
        }
    }
    
    public void CancelPendingOperations() => _downloadCts?.Cancel();
    
    #region Chunk Management
    
    private async Task EnsureChunkAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null) return;
        if (_cacheEntry.IsChunkDownloaded(index)) return;
        if (_ramChunks.ContainsKey(index)) return;
        
        if (_activeDownloads.TryGetValue(index, out var existingTask))
        {
            await existingTask;
            return;
        }
        
        var downloadTask = DownloadChunkAsync(index, ct);
        _activeDownloads.TryAdd(index, downloadTask);
        
        try
        {
            await downloadTask;
        }
        finally
        {
            _activeDownloads.TryRemove(index, out _);
        }
    }
    
    private async Task DownloadChunkAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || _cacheEntry.IsChunkDownloaded(index)) return;
        
        bool gotSlot = false;
        
        try
        {
            gotSlot = await _downloadSlots.WaitAsync(500, ct);
            if (!gotSlot) return;
            
            long start = (long)index * ChunkSize;
            long end = Math.Min(start + ChunkSize - 1, _contentLength - 1);
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeoutMs);
            
            using var request = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
            
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await RefreshUrlAsync(ct);
                return;
            }
            
            response.EnsureSuccessStatusCode();
            
            var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
            
            _ramChunks.TryAdd(index, data);
            await _cacheManager.WriteChunkAsync(_trackId, index, data, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Chunk {index} failed: {ex.Message}");
        }
        finally
        {
            if (gotSlot)
            {
                try { _downloadSlots.Release(); } catch { }
            }
        }
    }
    
    internal async Task<int> ReadAtAsync(long position, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (position >= _contentLength) return 0;
        
        int chunkIndex = (int)(position / ChunkSize);
        int offsetInChunk = (int)(position % ChunkSize);
        
        // RAM cache
        if (_ramChunks.TryGetValue(chunkIndex, out var ramData))
        {
            int available = Math.Min(count, ramData.Length - offsetInChunk);
            if (available > 0)
            {
                Buffer.BlockCopy(ramData, offsetInChunk, buffer, offset, available);
                return available;
            }
        }
        
        // Disk cache
        var diskData = await _cacheManager.ReadChunkAsync(_trackId, chunkIndex, ct);
        if (diskData != null)
        {
            _ramChunks.TryAdd(chunkIndex, diskData);
            
            int available = Math.Min(count, diskData.Length - offsetInChunk);
            if (available > 0)
            {
                Buffer.BlockCopy(diskData, offsetInChunk, buffer, offset, available);
                return available;
            }
        }
        
        // Download
        await EnsureChunkAsync(chunkIndex, ct);
        
        if (_ramChunks.TryGetValue(chunkIndex, out ramData))
        {
            int available = Math.Min(count, ramData.Length - offsetInChunk);
            if (available > 0)
            {
                Buffer.BlockCopy(ramData, offsetInChunk, buffer, offset, available);
                return available;
            }
        }
        
        return 0;
    }
    
    #endregion
    
    #region Background Tasks
    
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _cacheEntry != null)
        {
            try
            {
                await Task.Delay(200, ct);
                
                int current = _currentChunk;
                
                for (int i = 0; i < 8 && current + i < _cacheEntry.TotalChunks; i++)
                {
                    int idx = current + i;
                    if (!_cacheEntry.IsChunkDownloaded(idx) && !_activeDownloads.ContainsKey(idx))
                    {
                        _ = EnsureChunkAsync(idx, ct);
                    }
                }
                
                // Evict old RAM chunks
                if (_ramChunks.Count > 32)
                {
                    ReleaseRamBuffers();
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
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
            }
        }
        catch { }
    }
    
    #endregion
    
    #region Dispose
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _parser?.Dispose();
        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _downloadCts?.Cancel();
        
        if (_preloadTask != null)
        {
            try { await _preloadTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }
        
        _downloadCts?.Dispose();
        _parser?.Dispose();
        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
    }
    
    #endregion
    
    #region CachingReadStream
    
    private sealed class CachingReadStream : Stream
    {
        private readonly CachingStreamSource _source;
        private long _position;
        
        public CachingReadStream(CachingStreamSource source) => _source = source;
        
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
            int read = _source.ReadAtAsync(_position, buffer, offset, count, CancellationToken.None)
                .GetAwaiter().GetResult();
            _position += read;
            return read;
        }
        
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int read = await _source.ReadAtAsync(_position, buffer, offset, count, ct);
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