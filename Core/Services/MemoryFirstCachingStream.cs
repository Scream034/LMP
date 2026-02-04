using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LMP.Core.Models;

namespace LMP.Core.Services;

public sealed class MemoryFirstCachingStream : Stream
{
    #region Constants

    private const int MaxFileOpenRetries = 10;
    private const int FileOpenRetryDelayMs = 100;
    private const int DiskSaveThresholdBytes = 128 * 1024;

    #endregion

    #region Configuration

    private readonly int _chunkSize;
    private readonly int _readAheadChunks;
    private readonly int _maxConcurrentDownloads;
    private readonly int _maxRamChunks;
    private readonly int _downloadTimeoutMs;

    #endregion

    #region Identity

    private readonly string _cacheId;
    private readonly string _originalTrackId;
    private string _url;
    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly int _totalChunks;

    #endregion

    #region Dependencies

    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    #endregion

    #region State

    private readonly ConcurrentDictionary<int, byte[]> _chunks = new();
    private readonly ConcurrentDictionary<int, Task> _pendingDownloads = new();
    private readonly RangeMap _diskRanges;

    private long _position;
    private long _bytesDownloaded;
    private volatile bool _downloadComplete;
    private volatile bool _disposed;
    private volatile bool _disposing;

    #endregion

    #region Synchronization

    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly SemaphoreSlim _fileSemaphore = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Lock _queueLock = new();

    private readonly PriorityQueue<int, int> _downloadQueue = new();
    private readonly HashSet<int> _queuedChunks = [];

    private readonly Channel<(long Pos, byte[] Data, int Len)> _diskChannel;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationTokenSource _downloadCts;

    #endregion

    #region Tasks

    private readonly Task _diskWriterTask;
    private Task? _downloadLoop;
    private FileStream? _cacheFile;

    #endregion

    #region Stream Properties

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _contentLength;

    public override long Position
    {
        get => Volatile.Read(ref _position);
        set => Seek(value, SeekOrigin.Begin);
    }

    public double DownloadProgress
    {
        get
        {
            if (_contentLength <= 0) return 0;
            return Math.Min((double)Volatile.Read(ref _bytesDownloaded) / _contentLength * 100, 100);
        }
    }

    public bool IsFullyDownloaded => _downloadComplete;

    #endregion

    #region Constructor

    public MemoryFirstCachingStream(
        string cacheId,
        string url,
        long contentLength,
        HttpClient http,
        StreamCacheManager cacheManager,
        StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        string? originalTrackId = null)
    {
        _cacheId = cacheId;
        _originalTrackId = originalTrackId ?? cacheId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;

        // Config
        _chunkSize = config.ChunkSize;
        _readAheadChunks = config.ReadAheadChunks;
        _maxConcurrentDownloads = config.MaxConcurrentDownloads;
        _maxRamChunks = config.MaxRamChunks > 0 ? config.MaxRamChunks : 50;
        _downloadTimeoutMs = config.DownloadTimeoutMs;

        // Derived
        _cachePath = StreamCacheManager.GetCachePath(_cacheId);
        _downloadCts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        _totalChunks = (int)((_contentLength + _chunkSize - 1) / _chunkSize);

        // Metadata
        var meta = StreamCacheManager.TryGetMetadata(cacheId)
            ?? StreamCacheManager.LoadOrCreateMetadata(cacheId, url, contentLength);

        _diskRanges = RangeMap.Deserialize(meta.RangesJson);
        _bytesDownloaded = _diskRanges.DownloadedBytes;

        // Already complete?
        if (_diskRanges.IsFullyDownloaded(_contentLength))
        {
            _downloadComplete = true;
            _cacheManager.TriggerCacheCompleted(_cacheId, _originalTrackId);
            Log.Info($"[CacheStream] {_cacheId} already complete");
        }

        // File
        _cacheFile = OpenCacheFile(_cachePath);
        if (_cacheFile != null && _cacheFile.Length < _contentLength)
            _cacheFile.SetLength(_contentLength);

        // Disk writer channel
        _diskChannel = Channel.CreateBounded<(long, byte[], int)>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _diskWriterTask = Task.Run(DiskWriterLoopAsync);

        // Diagnostics
        MemoryDiagnostics.TrackInstance("Stream.Active");
        MemoryDiagnostics.TrackBytes("Stream.TotalSize", _contentLength);

        Log.Info($"[CacheStream] Opened {_cacheId}: {contentLength / 1024 / 1024}MB");
    }

    #endregion

    #region Public API

    public async ValueTask<bool> PreBufferAsync(CancellationToken ct)
    {
        if (_disposed || _disposing) return false;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token, _disposeCts.Token);
            var token = linked.Token;

            _downloadLoop ??= Task.Run(() => DownloadLoopAsync(token), token);

            if (HasChunk(0)) return true;

            EnqueueUrgent(0);

            var sw = Stopwatch.StartNew();
            while (!HasChunk(0))
            {
                if (token.IsCancellationRequested) return false;
                if (!_dataAvailable.Wait(200, token))
                {
                    if (sw.ElapsedMilliseconds > _downloadTimeoutMs) return false;
                }
                if (!HasChunk(0)) _dataAvailable.Reset();
            }
            return true;
        }
        catch { return false; }
    }

    public void CancelPendingReads()
    {
        try { _disposeCts.Cancel(); } catch { }
    }

    public void ReleaseRamBuffers()
    {
        if (_disposed) return;

        int removed = 0;
        long freed = 0;

        foreach (var kvp in _chunks)
        {
            int idx = kvp.Key;
            long start = (long)idx * _chunkSize;
            long end = Math.Min(start + _chunkSize, _contentLength);

            if (_diskRanges.IsRangeComplete(start, end))
            {
                if (_chunks.TryRemove(idx, out var buffer))
                {
                    freed += buffer.Length;
                    removed++;
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        if (freed > 0)
        {
            MemoryDiagnostics.UntrackBytes("Stream.RAMChunks", freed);
            Log.Info($"[CacheStream] Released {removed} chunks ({freed / 1024 / 1024}MB) on minimize");
        }
    }

    #endregion

    #region Stream Implementation

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed || _disposing || _disposeCts.IsCancellationRequested) return 0;

        long pos = Volatile.Read(ref _position);
        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, _contentLength - pos);
        if (count <= 0) return 0;

        int chunkIndex = (int)(pos / _chunkSize);
        int offsetInChunk = (int)(pos % _chunkSize);
        int toRead = Math.Min(count, _chunkSize - offsetInChunk);

        try
        {
            // Wait for chunk
            while (!HasChunk(chunkIndex))
            {
                if (_disposed || _disposing || _disposeCts.IsCancellationRequested) return 0;
                EnqueueUrgent(chunkIndex);
                try { _dataAvailable.Wait(500, _disposeCts.Token); } catch { return 0; }
                if (!HasChunk(chunkIndex)) _dataAvailable.Reset();
            }

            int bytesRead = ReadChunk(chunkIndex, offsetInChunk, buffer, offset, toRead);
            if (bytesRead > 0)
            {
                Interlocked.Add(ref _position, bytesRead);
                EnqueueReadAhead(chunkIndex);
            }
            return bytesRead;
        }
        catch { return 0; }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) return 0;

        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _contentLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        newPos = Math.Clamp(newPos, 0, _contentLength);
        Volatile.Write(ref _position, newPos);

        int newChunk = (int)(newPos / _chunkSize);
        EnqueueUrgent(newChunk);

        return newPos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    #endregion

    #region Chunk Operations

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasChunk(int idx)
    {
        if (_chunks.ContainsKey(idx)) return true;
        long start = (long)idx * _chunkSize;
        return _diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength));
    }

    private int ReadChunk(int idx, int off, byte[] buf, int bufOff, int count)
    {
        // Try RAM first
        if (_chunks.TryGetValue(idx, out var chunk))
        {
            int usefulLen = idx == _totalChunks - 1
                ? (int)(_contentLength - ((long)idx * _chunkSize))
                : _chunkSize;
            int available = Math.Min(count, usefulLen - off);
            if (available > 0) Buffer.BlockCopy(chunk, off, buf, bufOff, available);
            return available;
        }

        // Fall back to disk
        long start = (long)idx * _chunkSize;
        if (_diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength)))
            return ReadFromDisk(start + off, buf, bufOff, count);

        return 0;
    }

    private int ReadFromDisk(long pos, byte[] buf, int off, int count)
    {
        if (_cacheFile == null || _disposing) return 0;

        try
        {
            _fileSemaphore.Wait(_disposeCts.Token);
            try
            {
                if (_cacheFile == null) return 0;
                _cacheFile.Seek(pos, SeekOrigin.Begin);
                return _cacheFile.Read(buf, off, count);
            }
            finally { _fileSemaphore.Release(); }
        }
        catch { return 0; }
    }

    #endregion

    #region Download Queue

    private void EnqueueUrgent(int idx)
    {
        lock (_queueLock)
        {
            TryEnqueue(idx, 0);
            for (int i = 1; i <= 3 && idx + i < _totalChunks; i++)
                TryEnqueue(idx + i, i);
        }
    }

    private void EnqueueReadAhead(int current)
    {
        lock (_queueLock)
        {
            for (int i = 1; i <= _readAheadChunks && current + i < _totalChunks; i++)
                TryEnqueue(current + i, 50 + i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryEnqueue(int idx, int priority)
    {
        if (HasChunk(idx) || _pendingDownloads.ContainsKey(idx)) return;
        if (_queuedChunks.Add(idx))
            _downloadQueue.Enqueue(idx, priority);
    }

    #endregion

    #region Download Loop

    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposing)
        {
            int chunk = -1;

            lock (_queueLock)
            {
                while (_downloadQueue.Count > 0)
                {
                    var c = _downloadQueue.Dequeue();
                    _queuedChunks.Remove(c);
                    if (!HasChunk(c) && !_pendingDownloads.ContainsKey(c))
                    {
                        chunk = c;
                        break;
                    }
                }
            }

            if (chunk < 0)
            {
                if (IsAllDownloaded())
                {
                    _downloadComplete = true;
                    break;
                }
                try { await Task.Delay(100, ct); } catch { break; }
                continue;
            }

            try { await _downloadSemaphore.WaitAsync(ct); }
            catch { break; }

            _ = DownloadChunkAsync(chunk, ct);
        }
    }

    private async Task DownloadChunkAsync(int idx, CancellationToken ct)
    {
        byte[]? buffer = null;
        int retry = 0;
        const int maxRetries = 2;

        try
        {
            if (HasChunk(idx) || _disposing) return;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingDownloads.TryAdd(idx, tcs.Task)) return;

            while (retry <= maxRetries)
            {
                try
                {
                    long start = (long)idx * _chunkSize;
                    long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);

                    using var req = new HttpRequestMessage(HttpMethod.Get, _url);
                    req.Headers.Range = new RangeHeaderValue(start, end);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token);
                    cts.CancelAfter(_downloadTimeoutMs);

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    if (resp.StatusCode == HttpStatusCode.Forbidden && retry < maxRetries && _urlRefresher != null)
                    {
                        await RefreshUrlAsync(cts.Token);
                        retry++;
                        continue;
                    }

                    resp.EnsureSuccessStatusCode();

                    buffer = ArrayPool<byte>.Shared.Rent(_chunkSize);
                    using var netStream = await resp.Content.ReadAsStreamAsync(cts.Token);

                    int totalRead = 0, bytesRead;
                    while ((bytesRead = await netStream.ReadAsync(buffer.AsMemory(totalRead, _chunkSize - totalRead), cts.Token)) > 0)
                        totalRead += bytesRead;

                    if (!_chunks.ContainsKey(idx) && !_disposing)
                    {
                        _chunks[idx] = buffer;
                        Interlocked.Add(ref _bytesDownloaded, totalRead);
                        _dataAvailable.Set();

                        MemoryDiagnostics.TrackBytes("Stream.RAMChunks", buffer.Length);

                        // Queue for disk write
                        if (_cacheFile != null && !_disposing)
                        {
                            var diskBuf = ArrayPool<byte>.Shared.Rent(totalRead);
                            Buffer.BlockCopy(buffer, 0, diskBuf, 0, totalRead);
                            await _diskChannel.Writer.WriteAsync((start, diskBuf, totalRead), cts.Token);
                        }

                        buffer = null; // Ownership transferred

                        if (_chunks.Count > _maxRamChunks)
                            TrimRamCache();
                    }

                    tcs.SetResult();
                    break;
                }
                catch (Exception ex) when (retry < maxRetries && ex is not OperationCanceledException)
                {
                    await Task.Delay(500, ct);
                    retry++;
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                Log.Warn($"[CacheStream] Chunk {idx} error: {ex.Message}");
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            _pendingDownloads.TryRemove(idx, out _);
            _downloadSemaphore.Release();
        }
    }

    private async ValueTask RefreshUrlAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var newUrl = await _urlRefresher!(ct);
            if (!string.IsNullOrEmpty(newUrl)) _url = newUrl;
        }
        finally { _refreshLock.Release(); }
    }

    private void TrimRamCache()
    {
        if (_chunks.Count <= _maxRamChunks) return;

        int current = (int)(Volatile.Read(ref _position) / _chunkSize);
        int keepStart = current - 2;
        int keepEnd = current + _readAheadChunks * 2;

        // Inline removal without LINQ allocation
        foreach (var key in _chunks.Keys)
        {
            if (key < keepStart || key > keepEnd)
            {
                if (_chunks.TryRemove(key, out var buf))
                {
                    MemoryDiagnostics.UntrackBytes("Stream.RAMChunks", buf.Length);
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }
    }

    private bool IsAllDownloaded()
    {
        for (int i = 0; i < _totalChunks; i++)
            if (!HasChunk(i)) return false;
        return true;
    }

    #endregion

    #region Disk Writer

    private async Task DiskWriterLoopAsync()
    {
        int bytesWritten = 0;

        try
        {
            await foreach (var (pos, data, len) in _diskChannel.Reader.ReadAllAsync(_disposeCts.Token))
            {
                try
                {
                    if (_disposing || _cacheFile == null) continue;

                    await _fileSemaphore.WaitAsync(_disposeCts.Token);
                    try
                    {
                        if (_cacheFile != null)
                        {
                            _cacheFile.Seek(pos, SeekOrigin.Begin);
                            await _cacheFile.WriteAsync(data.AsMemory(0, len), _disposeCts.Token);
                        }
                    }
                    finally { _fileSemaphore.Release(); }

                    _diskRanges.MarkComplete(pos, pos + len);
                    bytesWritten += len;

                    // Check completion
                    if (!_downloadComplete && _diskRanges.IsFullyDownloaded(_contentLength))
                    {
                        _downloadComplete = true;
                        SaveRanges();
                        bytesWritten = 0;
                        Log.Info($"[CacheStream] {_cacheId} fully cached!");
                        _cacheManager.TriggerCacheCompleted(_cacheId, _originalTrackId);
                    }
                    else if (bytesWritten >= DiskSaveThresholdBytes)
                    {
                        SaveRanges();
                        bytesWritten = 0;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error($"[CacheStream] Disk write error: {ex.Message}"); }
                finally { ArrayPool<byte>.Shared.Return(data); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Final check
            if (!_downloadComplete && _diskRanges.IsFullyDownloaded(_contentLength))
            {
                _downloadComplete = true;
                SaveRanges();
                _cacheManager.TriggerCacheCompleted(_cacheId, _originalTrackId);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SaveRanges()
    {
        try { StreamCacheManager.UpdateRanges(_cacheId, _diskRanges); } catch { }
    }

    #endregion

    #region File Helpers

    private static FileStream? OpenCacheFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                Log.Error($"[CacheStream] Failed to create dir: {ex.Message}");
                return null;
            }
        }

        for (int attempt = 1; attempt <= MaxFileOpenRetries; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            catch (IOException) when (attempt < MaxFileOpenRetries)
            {
                Thread.Sleep(FileOpenRetryDelayMs * attempt);
            }
            catch (Exception ex)
            {
                Log.Error($"[CacheStream] File open error: {ex.Message}");
                return null;
            }
        }
        return null;
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposing = true;
        _disposed = true;

        if (disposing)
        {
            MemoryDiagnostics.UntrackInstance("Stream.Active");
            MemoryDiagnostics.UntrackBytes("Stream.TotalSize", _contentLength);

            Try(_downloadCts.Cancel);
            Try(_disposeCts.Cancel);
            Try(() => _diskChannel.Writer.TryComplete());

            // Drain channel
            while (_diskChannel.Reader.TryRead(out var item))
                ArrayPool<byte>.Shared.Return(item.Data);

            SaveRanges();
            _dataAvailable.Set();

            // Async cleanup
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAny(_diskWriterTask, Task.Delay(1000));

                    await _fileSemaphore.WaitAsync(2000);
                    try
                    {
                        Try(() => _cacheFile?.Flush());
                        Try(() => _cacheFile?.Dispose());
                        _cacheFile = null;
                    }
                    finally { _fileSemaphore.Release(); }
                }
                finally
                {
                    long freed = 0;
                    foreach (var buf in _chunks.Values)
                    {
                        freed += buf.Length;
                        Try(() => ArrayPool<byte>.Shared.Return(buf));
                    }

                    if (freed > 0)
                        MemoryDiagnostics.UntrackBytes("Stream.RAMChunks", freed);

                    _chunks.Clear();

                    Try(_fileSemaphore.Dispose);
                    Try(_downloadSemaphore.Dispose);
                    Try(_refreshLock.Dispose);
                    Try(_downloadCts.Dispose);
                    Try(_disposeCts.Dispose);
                }
            });
        }

        base.Dispose(disposing);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Try(Action a) { try { a(); } catch { } }

    #endregion
}