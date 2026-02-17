using System.Collections.Concurrent;
using System.Net;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник аудио с сегментным кэшированием.
/// Поддерживает seek, частичную загрузку и визуализацию прогресса буфера.
/// </summary>
public sealed class CachingStreamSource : IAudioSource
{
    #region Fields

    private readonly string _cacheKey;
    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly AudioFormat _format;
    private readonly int _bitrate;
    private readonly HttpClient _httpClient;
    private readonly AudioCacheManager _cacheManager;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    private CacheEntry? _cacheEntry;
    private IContainerParser? _parser;
    private AsyncCachingReadStream? _readStream;

    private readonly ConcurrentDictionary<int, byte[]> _ramChunks = new();
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads);
    private readonly Lock _seekLock = new();

    private int _currentChunk;
    private long _positionMs;
    private string _currentUrl;
    private int _backgroundChunksLoaded;

    private volatile bool _initialized;
    private volatile bool _disposed;
    private volatile bool _isOfflineMode;

    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _seekCts;
    private Task? _preloadTask;

    #endregion

    #region Properties

    public long DurationMs => _parser?.DurationMs ?? _cacheEntry?.DurationMs ?? -1;
    public long PositionMs => Volatile.Read(ref _positionMs);
    public bool CanSeek => true;
    public AudioCodec Codec { get; private set; }
    public byte[]? DecoderConfig => _parser?.DecoderConfig;
    public int SampleRate => _parser?.SampleRate ?? 0;
    public int Channels => _parser?.Channels ?? 0;

    public double BufferProgress => _isOfflineMode ? 100 : (_cacheEntry?.DownloadProgress ?? 0);
    public bool IsFullyBuffered => _cacheEntry?.IsComplete ?? _isOfflineMode;
    public bool IsOfflineMode => _isOfflineMode;
    public long DownloadedBytes => (_cacheEntry?.DownloadedChunks ?? 0) * (long)ChunkSize;
    public int Bitrate => _cacheEntry?.Bitrate ?? _bitrate;

    #endregion

    #region Constructor

    public CachingStreamSource(
        string cacheKey,
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _cacheKey = cacheKey;
        _trackId = trackId;
        _url = url;
        _currentUrl = url;
        _contentLength = contentLength;
        _format = format;
        _bitrate = bitrate;
        _httpClient = httpClient;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;
        Codec = codec;
    }

    #endregion

    #region Initialization

    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            if (_cacheManager.IsFullyCached(_cacheKey))
            {
                Log.Info($"[CachingSource] Using fully cached: {_cacheKey}");
                _isOfflineMode = true;
                return await InitializeFromCacheAsync(ct);
            }

            _cacheEntry = _cacheManager.CreateOrUpdate(
                _cacheKey, _trackId, _url, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format),
                _bitrate,
                chunkSize: ChunkSize);

            if (_cacheEntry.DownloadedChunks > 0)
                Log.Info($"[CachingSource] Resuming: {_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks} chunks");

            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await LoadInitialChunksAsync(_operationCts.Token);

            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            if (!await _parser.ParseHeadersAsync(ct))
                throw new InvalidOperationException("Failed to parse container headers");

            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;

            _initialized = true;
            _preloadTask = Task.Run(() => PreloadLoopAsync(_operationCts.Token));

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
        var stream = _cacheManager.OpenCachedStream(_cacheKey);
        if (stream == null)
        {
            _isOfflineMode = false;
            return await InitializeAsync(ct);
        }

        _cacheEntry = _cacheManager.GetCacheInfo(_cacheKey);
        _readStream = new AsyncCachingReadStream(this, stream);
        _parser = CreateParser(_readStream);

        if (!await _parser.ParseHeadersAsync(ct))
            return false;

        Codec = _parser.Codec;
        _initialized = true;
        return true;
    }

    private IContainerParser CreateParser(Stream stream) => _format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };

    private async Task LoadInitialChunksAsync(CancellationToken ct)
    {
        if (_cacheEntry == null) return;

        int count = Math.Min(InitialChunksToLoad, _cacheEntry.TotalChunks);
        var tasks = new Task[count];
        
        for (int i = 0; i < count; i++)
            tasks[i] = EnsureChunkAsync(i, ct);

        await Task.WhenAll(tasks);
    }

    #endregion

    #region Reading

    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Not initialized");

        try
        {
            var frame = await _parser.ReadNextFrameAsync(ct);
            if (frame == null) return null;

            Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
            UpdateCurrentChunk();
            return frame;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException) when (!_disposed && !_isOfflineMode)
        {
            await EnsureChunkAsync(_currentChunk, ct);
            return await ReadFrameAsync(ct);
        }
    }

    private void UpdateCurrentChunk()
    {
        if (!_isOfflineMode && _readStream != null)
            _currentChunk = (int)(_readStream.Position / ChunkSize);
    }

    #endregion

    #region Seeking

    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null) return false;

        lock (_seekLock)
        {
            _seekCts?.Cancel();
            _seekCts?.Dispose();
            _seekCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        var localCts = _seekCts;

        try
        {
            var seekInfo = _parser.FindSeekPosition(positionMs);
            if (seekInfo == null)
            {
                Log.Warn($"[CachingSource] No seek point for {positionMs}ms");
                return false;
            }

            long targetBytePos = seekInfo.Value.BytePosition;
            long segmentStartMs = seekInfo.Value.TimestampMs;
            int targetChunk = (int)(targetBytePos / ChunkSize);

            Log.Debug($"[CachingSource] Seek: {positionMs}ms → chunk {targetChunk}/{_cacheEntry?.TotalChunks}");

            if (!_isOfflineMode && _cacheEntry != null)
            {
                await PreloadChunksForSeekAsync(targetChunk, localCts!.Token);
            }

            _readStream!.Position = targetBytePos;
            _currentChunk = targetChunk;
            _parser.Reset();
            Volatile.Write(ref _positionMs, segmentStartMs);

            return true;
        }
        catch (OperationCanceledException) when (localCts!.Token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            lock (_seekLock)
            {
                if (_seekCts == localCts)
                    _seekCts = null;
            }
            localCts?.Dispose();
        }
    }

    private async Task PreloadChunksForSeekAsync(int targetChunk, CancellationToken ct)
    {
        if (_cacheEntry == null) return;

        var tasks = new List<Task>();
        int end = Math.Min(targetChunk + SeekPreloadChunks, _cacheEntry.TotalChunks);

        for (int i = targetChunk; i < end; i++)
        {
            if (!IsChunkAvailable(i))
                tasks.Add(EnsureChunkAsync(i, ct));
        }

        if (tasks.Count > 0)
        {
            Log.Debug($"[CachingSource] Seek preloading {tasks.Count} chunks from {targetChunk}");
            await Task.WhenAll(tasks);
        }
    }

    #endregion

    #region Buffered Ranges

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_isOfflineMode)
            return [(0.0, 1.0)];

        if (_cacheEntry == null)
            return [];

        int total = _cacheEntry.TotalChunks;
        if (total == 0)
            return [];

        var ranges = new List<(double, double)>();
        int? rangeStart = null;

        for (int i = 0; i < total; i++)
        {
            if (IsChunkAvailable(i))
            {
                rangeStart ??= i;
            }
            else if (rangeStart.HasValue)
            {
                ranges.Add(((double)rangeStart.Value / total, (double)i / total));
                rangeStart = null;
            }
        }

        if (rangeStart.HasValue)
            ranges.Add(((double)rangeStart.Value / total, 1.0));

        return ranges;
    }

    private bool IsChunkAvailable(int index) =>
        _cacheEntry!.IsChunkDownloaded(index) || _ramChunks.ContainsKey(index);

    public void ReleaseRamBuffers()
    {
        int current = Volatile.Read(ref _currentChunk);
        
        foreach (var idx in _ramChunks.Keys.Where(i => Math.Abs(i - current) > RamEvictionDistance).ToList())
            _ramChunks.TryRemove(idx, out _);
    }

    public void CancelPendingOperations() => _operationCts?.Cancel();

    #endregion

    #region Chunk Management

    private async Task EnsureChunkAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || IsChunkAvailable(index))
            return;

        if (_activeDownloads.TryGetValue(index, out var existingTask))
        {
            await existingTask;
            return;
        }

        var downloadTask = DownloadChunkCoreAsync(index, ct);
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

    private async Task DownloadChunkCoreAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || _cacheEntry.IsChunkDownloaded(index))
            return;

        bool gotSlot = false;

        try
        {
            gotSlot = await _downloadSlots.WaitAsync(DownloadSlotTimeoutMs, ct);
            if (!gotSlot) return;

            if (_cacheEntry.IsChunkDownloaded(index)) return;

            long start = (long)index * ChunkSize;
            long end = Math.Min(start + ChunkSize - 1, _contentLength - 1);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeoutMs);

            using var request = SharedHttpClient.CreateRangeRequest(_currentUrl, start, end);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await RefreshUrlAsync(ct);
                return;
            }

            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
            _ramChunks.TryAdd(index, data);

            try
            {
                await _cacheManager.WriteChunkAsync(_cacheKey, index, data, ct);
            }
            catch (IOException ex)
            {
                Log.Warn($"[CachingSource] Disk write failed: {ex.Message}");
            }

            if (_ramChunks.Count > MaxRamChunks)
                EvictDistantRamChunks();
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
                try { _downloadSlots.Release(); }
                catch { }
            }
        }
    }

    private void EvictDistantRamChunks()
    {
        int current = Volatile.Read(ref _currentChunk);

        var toEvict = _ramChunks.Keys
            .Where(i => Math.Abs(i - current) > RamEvictionDistance)
            .OrderByDescending(i => Math.Abs(i - current))
            .Take(MaxRamChunks / 4)
            .ToList();

        foreach (var idx in toEvict)
            _ramChunks.TryRemove(idx, out _);
    }

    internal async Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        if (position >= _contentLength) return 0;

        int chunkIndex = (int)(position / ChunkSize);
        int offsetInChunk = (int)(position % ChunkSize);

        if (_ramChunks.TryGetValue(chunkIndex, out var ramData))
            return CopyFromChunk(ramData, offsetInChunk, buffer);

        var diskData = await _cacheManager.ReadChunkAsync(_cacheKey, chunkIndex, ct);
        if (diskData != null)
        {
            _ramChunks.TryAdd(chunkIndex, diskData);
            return CopyFromChunk(diskData, offsetInChunk, buffer);
        }

        await EnsureChunkAsync(chunkIndex, ct);

        if (_ramChunks.TryGetValue(chunkIndex, out ramData))
            return CopyFromChunk(ramData, offsetInChunk, buffer);

        return 0;
    }

    private static int CopyFromChunk(byte[] chunkData, int offset, Memory<byte> buffer)
    {
        int available = Math.Min(buffer.Length, chunkData.Length - offset);
        if (available <= 0) return 0;
        
        chunkData.AsSpan(offset, available).CopyTo(buffer.Span);
        return available;
    }

    #endregion

    #region Background Preload

    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        int lastReportedProgress = -1;
        int idleCycles = 0;
        _backgroundChunksLoaded = 0;

        while (!ct.IsCancellationRequested && _cacheEntry != null)
        {
            try
            {
                await Task.Delay(PreloadIntervalMs, ct);

                if (_cacheEntry.IsComplete)
                    break;

                int current = Volatile.Read(ref _currentChunk);
                int pending = _activeDownloads.Count;

                if (pending >= MaxConcurrentDownloads)
                {
                    idleCycles = 0;
                    continue;
                }

                bool activePreload = false;
                int chunksAhead = 0;

                for (int i = 0; i <= PreloadAheadChunks && pending < MaxConcurrentDownloads; i++)
                {
                    int idx = current + i;
                    if (idx >= _cacheEntry.TotalChunks) break;

                    if (IsChunkAvailable(idx))
                    {
                        chunksAhead++;
                    }
                    else if (!_activeDownloads.ContainsKey(idx))
                    {
                        _ = EnsureChunkAsync(idx, ct);
                        pending++;
                        activePreload = true;
                        await Task.Delay(50, ct);
                    }
                }

                if (!activePreload)
                    idleCycles++;
                else
                    idleCycles = 0;

                bool canBackgroundFill =
                    !activePreload
                    && idleCycles >= BackgroundFillIdleCycles
                    && pending < MaxConcurrentDownloads
                    && chunksAhead >= MinBufferAheadForBackgroundFill
                    && (MaxBackgroundChunksPerSession == 0 || _backgroundChunksLoaded < MaxBackgroundChunksPerSession);

                if (canBackgroundFill)
                {
                    int? target = FindNearestMissingChunk(current);

                    if (target.HasValue && !IsChunkAvailable(target.Value) && !_activeDownloads.ContainsKey(target.Value))
                    {
                        _ = EnsureChunkAsync(target.Value, ct);
                        _backgroundChunksLoaded++;
                        await Task.Delay(BackgroundFillIntervalMs, ct);
                    }
                }

                int progress = (int)_cacheEntry.DownloadProgress;
                if (progress / 25 > lastReportedProgress / 25)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% ({_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks})");
                    lastReportedProgress = progress;
                }

                if (_ramChunks.Count > MaxRamChunks)
                    ReleaseRamBuffers();
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private int? FindNearestMissingChunk(int currentChunk)
    {
        if (_cacheEntry == null) return null;
        int total = _cacheEntry.TotalChunks;

        for (int offset = 1; offset < total; offset++)
        {
            int forward = currentChunk + offset;
            if (forward < total && !IsChunkAvailable(forward))
                return forward;

            int backward = currentChunk - offset;
            if (backward >= 0 && !IsChunkAvailable(backward))
                return backward;
        }

        return null;
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
                Log.Info("[CachingSource] URL refreshed");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] URL refresh failed: {ex.Message}");
        }
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_seekLock)
        {
            _seekCts?.Cancel();
            _seekCts?.Dispose();
        }

        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _parser?.Dispose();
        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_seekLock)
        {
            _seekCts?.Cancel();
            _seekCts?.Dispose();
        }

        _operationCts?.Cancel();

        if (_preloadTask != null)
        {
            try { await _preloadTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }

        _operationCts?.Dispose();

        if (_parser != null)
            await _parser.DisposeAsync();

        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
    }

    #endregion

    #region AsyncCachingReadStream

    private sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource? _source;
        private readonly Stream? _fileStream;
        private long _position;

        public AsyncCachingReadStream(CachingStreamSource source) => _source = source;

        public AsyncCachingReadStream(CachingStreamSource source, Stream fileStream)
        {
            _source = source;
            _fileStream = fileStream;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _fileStream?.Length ?? _source?._contentLength ?? 0;

        public override long Position
        {
            get => _fileStream?.Position ?? Volatile.Read(ref _position);
            set
            {
                if (_fileStream != null) _fileStream.Position = value;
                else Volatile.Write(ref _position, value);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_fileStream != null) return await _fileStream.ReadAsync(buffer, ct);
            if (_source == null) return 0;

            long pos = Volatile.Read(ref _position);
            int read = await _source.ReadAtAsync(pos, buffer, ct);
            Volatile.Write(ref _position, pos + read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_fileStream != null) return _fileStream.Seek(offset, origin);

            long length = _source?._contentLength ?? 0;
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Volatile.Read(ref _position) + offset,
                SeekOrigin.End => length + offset,
                _ => Volatile.Read(ref _position)
            };

            Volatile.Write(ref _position, newPos);
            return newPos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fileStream?.Dispose();
            base.Dispose(disposing);
        }
    }

    #endregion
}