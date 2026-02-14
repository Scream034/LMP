// Core/Audio/Sources/UniversalStreamSource.cs

using System.Collections.Concurrent;
using System.Net;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Универсальный источник для WebM и MP4 (без кэширования на диск).
/// </summary>
public sealed class UniversalStreamSource : IAudioSource
{
    private const int ChunkSize = 256 * 1024;
    private const int MaxRamChunks = 32;
    private const int MaxConcurrentDownloads = 4;
    private const int DownloadTimeoutMs = 15000;
    
    private readonly string _cacheId;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;
    private readonly AudioCodec _expectedCodec;
    
    private string _currentUrl;
    private readonly long _contentLength;
    private readonly int _totalChunks;
    
    private readonly ConcurrentDictionary<int, byte[]> _ramChunks = new();
    private readonly ConcurrentBitArray _downloadedChunks;
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSlots;
    
    private IContainerParser? _parser;
    private Stream? _readStream;
    
    private long _durationMs = -1;
    private long _positionMs;
    private int _currentChunk;
    private volatile bool _initialized;
    private volatile bool _disposed;
    
    private CancellationTokenSource? _downloadCts;
    private Task? _preloadTask;
    
    public long DurationMs => _durationMs;
    public long PositionMs => Interlocked.Read(ref _positionMs);
    public bool CanSeek => true;
    public AudioCodec Codec { get; private set; }
    
    public double BufferProgress => _totalChunks == 0 ? 0 : 
        Math.Min((double)_downloadedChunks.PopCount() / _totalChunks * 100, 100);
    
    public bool IsFullyBuffered => _downloadedChunks.PopCount() >= _totalChunks;
    
    public UniversalStreamSource(
        string cacheId,
        string url,
        long contentLength,
        AudioCodec expectedCodec,
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        if (contentLength <= 0)
            throw new ArgumentException("Content length must be positive", nameof(contentLength));
        
        _cacheId = cacheId;
        _currentUrl = url;
        _contentLength = contentLength;
        _expectedCodec = expectedCodec;
        _httpClient = httpClient;
        _urlRefresher = urlRefresher;
        
        _totalChunks = (int)Math.Ceiling((double)contentLength / ChunkSize);
        _downloadedChunks = new ConcurrentBitArray(_totalChunks);
        _downloadSlots = new SemaphoreSlim(MaxConcurrentDownloads);
        
        Codec = expectedCodec;
    }
    
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;
        
        try
        {
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            
            for (int i = 0; i < Math.Min(4, _totalChunks); i++)
            {
                await DownloadChunkAsync(i, _downloadCts.Token);
            }
            
            _readStream = new ChunkReadStream(this);
            
            var header = new byte[12];
            int bytesRead = 0;
            while (bytesRead < 12)
            {
                int read = _readStream.Read(header, bytesRead, 12 - bytesRead);
                if (read == 0) break;
                bytesRead += read;
            }
            _readStream.Position = 0;
            
            var format = AudioSourceFactory.DetectFormatByMagic(header);
            
            _parser = format switch
            {
                AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(_readStream),
                AudioFormat.Mp4 => new Mp4ContainerParser(_readStream),
                _ => throw new NotSupportedException($"Unsupported format: {format}")
            };
            
            if (!await _parser.ParseHeadersAsync(ct))
            {
                throw new InvalidOperationException("Failed to parse headers");
            }
            
            _durationMs = _parser.DurationMs;
            Codec = _parser.Codec;
            _initialized = true;
            
            _preloadTask = Task.Run(() => PreloadLoopAsync(_downloadCts.Token));
            
            Log.Info($"[UniversalSource] Initialized: duration={_durationMs}ms, codec={Codec}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[UniversalSource] Init failed: {ex.Message}", ex);
            return false;
        }
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
            _currentChunk = (int)(_readStream!.Position / ChunkSize);
            
            return frame;
        }
        catch (IOException) when (!_disposed)
        {
            await EnsureChunkAsync(_currentChunk, ct);
            return await ReadFrameAsync(ct);
        }
    }
    
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null || _durationMs <= 0) return false;
        
        var seekInfo = _parser.FindSeekPosition(positionMs);
        
        if (seekInfo == null)
        {
            long approxPosition = (long)(_contentLength * ((double)positionMs / _durationMs));
            seekInfo = (approxPosition, positionMs);
        }
        
        int targetChunk = (int)(seekInfo.Value.BytePosition / ChunkSize);
        targetChunk = Math.Clamp(targetChunk, 0, _totalChunks - 1);
        
        for (int i = targetChunk; i < Math.Min(targetChunk + 3, _totalChunks); i++)
        {
            await EnsureChunkAsync(i, ct);
        }
        
        _readStream!.Position = seekInfo.Value.BytePosition;
        _currentChunk = targetChunk;
        _parser.Reset();
        
        Interlocked.Exchange(ref _positionMs, positionMs);
        
        return true;
    }
    
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_totalChunks == 0) return [];
        
        var ranges = new List<(double, double)>();
        int? rangeStart = null;
        
        for (int i = 0; i < _totalChunks; i++)
        {
            if (_downloadedChunks.Get(i))
            {
                rangeStart ??= i;
            }
            else if (rangeStart.HasValue)
            {
                ranges.Add(((double)rangeStart.Value / _totalChunks, (double)i / _totalChunks));
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
        if (_downloadedChunks.Get(index)) return;
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
        if (index < 0 || index >= _totalChunks) return;
        if (_downloadedChunks.Get(index)) return;
        
        bool gotSlot = false;
        
        try
        {
            gotSlot = await _downloadSlots.WaitAsync(500, ct);
            if (!gotSlot || _downloadedChunks.Get(index)) return;
            
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
            
            _downloadedChunks.Set(index, true);
            _ramChunks.TryAdd(index, data);
            
            if (_ramChunks.Count > MaxRamChunks)
            {
                EvictOldChunks();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[UniversalSource] Chunk {index} failed: {ex.Message}");
        }
        finally
        {
            if (gotSlot)
            {
                try { _downloadSlots.Release(); } catch { }
            }
        }
    }
    
    private void EvictOldChunks()
    {
        int current = _currentChunk;
        
        var toEvict = _ramChunks.Keys
            .Where(i => Math.Abs(i - current) > 10)
            .OrderByDescending(i => Math.Abs(i - current))
            .Take(MaxRamChunks / 4)
            .ToList();
        
        foreach (var idx in toEvict)
        {
            _ramChunks.TryRemove(idx, out _);
        }
    }
    
    internal int ReadAt(long position, byte[] buffer, int offset, int count)
    {
        if (position >= _contentLength) return 0;
        
        int chunkIndex = (int)(position / ChunkSize);
        int offsetInChunk = (int)(position % ChunkSize);
        
        if (!_downloadedChunks.Get(chunkIndex))
        {
            DownloadChunkAsync(chunkIndex, CancellationToken.None).GetAwaiter().GetResult();
        }
        
        if (!_ramChunks.TryGetValue(chunkIndex, out var chunkData))
            return 0;
        
        int available = Math.Min(count, chunkData.Length - offsetInChunk);
        if (available <= 0) return 0;
        
        Buffer.BlockCopy(chunkData, offsetInChunk, buffer, offset, available);
        return available;
    }
    
    #endregion
    
    #region Background Tasks
    
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct);
                
                int current = _currentChunk;
                
                for (int i = 0; i < 8 && current + i < _totalChunks; i++)
                {
                    int idx = current + i;
                    if (!_downloadedChunks.Get(idx) && !_activeDownloads.ContainsKey(idx))
                    {
                        _ = EnsureChunkAsync(idx, ct);
                    }
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
    
    #region ChunkReadStream
    
    private sealed class ChunkReadStream : Stream
    {
        private readonly UniversalStreamSource _source;
        private long _position;
        
        public ChunkReadStream(UniversalStreamSource source) => _source = source;
        
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