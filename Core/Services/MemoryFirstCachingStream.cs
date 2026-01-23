using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.Channels;
using MyLiteMusicPlayer.Core.Models;

namespace MyLiteMusicPlayer.Core.Services;

/// <summary>
/// Adaptive Streaming Buffer с приоритетной загрузкой по запросам VLC.
/// Оптимизировано для минимизации нагрузки на GC (ArrayPool).
/// </summary>
public sealed class MemoryFirstCachingStream : Stream
{
    private const int ChunkSize = 128 * 1024;          // 128KB
    private const int ReadAheadChunks = 3;
    private const int MaxConcurrentDownloads = 3;
    private const int ReadTimeoutMs = 5000;
    private const int MaxRamChunks = 128;              // ~16MB
    private const int ChunkDownloadTimeoutMs = 20000;
    private const int ProgressLogIntervalBytes = 6 * 1024 * 1024;
    private const int MaxFileOpenRetries = 10;
    private const int FileOpenRetryDelayMs = 100;

    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly RangeMap _diskRanges;
    private readonly int _totalChunks;

    private readonly ConcurrentDictionary<int, byte[]> _chunks = new();

    private readonly ConcurrentDictionary<int, Task> _pendingDownloads = new();
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly PriorityQueue<int, int> _downloadQueue = new();
    private readonly HashSet<int> _queuedChunks = [];
    private readonly Lock _queueLock = new();

    private readonly ReaderWriterLockSlim _fileLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Channel<(long Pos, byte[] Data, int Len)> _diskChannel;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationTokenSource _downloadCts;

    private readonly Task _diskWriterTask;
    private Task? _downloadLoop;
    private FileStream? _cacheFile;

    private long _position;
    private long _bytesDownloaded;
    private volatile bool _downloadComplete;
    private volatile bool _disposed;
    private volatile bool _disposing;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _contentLength;

    public override long Position
    {
        get => Volatile.Read(ref _position);
        set => Seek(value, SeekOrigin.Begin);
    }

    public double DownloadProgress => _contentLength <= 0 ? 0 :
        Math.Min((double)Volatile.Read(ref _bytesDownloaded) / _contentLength * 100, 100);

    public bool IsFullyDownloaded => _downloadComplete;

    public MemoryFirstCachingStream(
        string trackId, string url, long contentLength,
        HttpClient http, StreamCacheManager cacheManager)
    {
        _trackId = trackId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _cachePath = cacheManager.GetCachePath(trackId);
        _downloadCts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(MaxConcurrentDownloads);
        _totalChunks = (int)((_contentLength + ChunkSize - 1) / ChunkSize);

        var meta = cacheManager.LoadOrCreateMetadata(trackId, url, contentLength);
        _diskRanges = RangeMap.Deserialize(meta.RangesJson);

        // Открываем файл с повторными попытками
        _cacheFile = OpenCacheFileWithRetry(_cachePath);

        if (_cacheFile != null && _cacheFile.Length < _contentLength)
            _cacheFile.SetLength(_contentLength);

        _diskChannel = Channel.CreateBounded<(long, byte[], int)>(
            new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        _diskWriterTask = Task.Run(DiskWriterLoopAsync);
        _bytesDownloaded = _diskRanges.DownloadedBytes;

        Log.Info($"Opened {trackId}: {contentLength / 1024 / 1024}MB, cached: {_diskRanges.DownloadedBytes / 1024}KB");
    }

    private static FileStream? OpenCacheFileWithRetry(string path)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxFileOpenRetries; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite, // Разрешаем совместный доступ
                    65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            catch (IOException ex) when (attempt < MaxFileOpenRetries)
            {
                lastException = ex;
                Log.Warn($"Cache file busy, retry {attempt}/{MaxFileOpenRetries}...");
                Thread.Sleep(FileOpenRetryDelayMs * attempt);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to open cache file: {ex.Message}");
                return null;
            }
        }

        Log.Error($"Failed to open cache file after {MaxFileOpenRetries} retries: {lastException?.Message}");
        return null; // Работаем без файлового кэша
    }

    public async Task<bool> PreBufferAsync(CancellationToken ct)
    {
        if (_disposed || _disposing) return false;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token, _disposeCts.Token);

            _downloadLoop ??= Task.Run(() => DownloadLoopAsync(linkedCts.Token), linkedCts.Token);

            if (HasChunk(0)) return true;

            var sw = Stopwatch.StartNew();
            EnqueueUrgent(0);

            while (!HasChunk(0))
            {
                if (_disposed || _disposing) return false;

                if (!_dataAvailable.Wait(500, linkedCts.Token))
                {
                    if (sw.ElapsedMilliseconds > ChunkDownloadTimeoutMs)
                    {
                        Log.Error($"PreBuffer timeout after {sw.ElapsedMilliseconds}ms");
                        return false;
                    }
                }
                _dataAvailable.Reset();
            }

            Log.Info($"PreBuffer OK in {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Нормальная отмена - не логируем как ошибку
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed || _disposing) return 0;

        long pos = Volatile.Read(ref _position);
        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, _contentLength - pos);
        if (count <= 0) return 0;

        int chunkIndex = (int)(pos / ChunkSize);
        int offsetInChunk = (int)(pos % ChunkSize);
        int toRead = Math.Min(count, ChunkSize - offsetInChunk);

        var sw = Stopwatch.StartNew();

        while (!HasChunk(chunkIndex))
        {
            if (_disposed || _disposing) return 0;

            EnqueueUrgent(chunkIndex);

            try
            {
                if (!_dataAvailable.Wait(1000, _disposeCts.Token))
                {
                    if (sw.ElapsedMilliseconds > ReadTimeoutMs)
                    {
                        Log.Error($"Read timeout for chunk {chunkIndex}");
                        return 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            _dataAvailable.Reset();
        }

        int bytesRead = TryReadChunk(chunkIndex, offsetInChunk, buffer, offset, toRead);
        if (bytesRead > 0)
        {
            Interlocked.Add(ref _position, bytesRead);
            EnqueueReadAhead(chunkIndex);
        }

        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) return 0;

        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _contentLength + offset,
            _ => throw new ArgumentException("Invalid origin", nameof(origin))
        };

        newPos = Math.Clamp(newPos, 0, _contentLength);
        int newChunk = (int)(newPos / ChunkSize);

        EnqueueUrgent(newChunk);
        Volatile.Write(ref _position, newPos);
        return newPos;
    }

    private int TryReadChunk(int idx, int off, byte[] buf, int bufOff, int count)
    {
        if (_chunks.TryGetValue(idx, out var chunk))
        {
            int usefulDataLength = (idx == _totalChunks - 1)
                ? (int)(_contentLength - ((long)idx * ChunkSize))
                : ChunkSize;

            int available = Math.Min(count, usefulDataLength - off);
            if (available > 0)
            {
                Buffer.BlockCopy(chunk, off, buf, bufOff, available);
            }
            return available;
        }

        long start = (long)idx * ChunkSize;
        return _diskRanges.IsRangeComplete(start, Math.Min(start + ChunkSize, _contentLength))
            ? ReadFromDisk(start + off, buf, bufOff, count)
            : 0;
    }

    private int ReadFromDisk(long pos, byte[] buf, int off, int count)
    {
        if (_cacheFile == null || _disposing) return 0;

        try
        {
            if (!_fileLock.TryEnterReadLock(100)) return 0;
            try
            {
                if (_cacheFile == null) return 0;
                _cacheFile.Seek(pos, SeekOrigin.Begin);
                return _cacheFile.Read(buf, off, count);
            }
            finally { _fileLock.ExitReadLock(); }
        }
        catch { return 0; }
    }

    private void EnqueueUrgent(int idx)
    {
        lock (_queueLock)
        {
            TryEnqueue(idx, 0);
            for (int i = 1; i <= 4 && idx + i < _totalChunks; i++)
                TryEnqueue(idx + i, i);
        }
    }

    private void EnqueueReadAhead(int current)
    {
        lock (_queueLock)
        {
            for (int i = 1; i <= ReadAheadChunks && current + i < _totalChunks; i++)
                TryEnqueue(current + i, 100 + i);
        }
    }

    private void TryEnqueue(int idx, int priority)
    {
        if (HasChunk(idx) || _pendingDownloads.ContainsKey(idx)) return;
        if (_queuedChunks.Add(idx)) _downloadQueue.Enqueue(idx, priority);
    }

    private bool HasChunk(int idx)
    {
        if (_chunks.ContainsKey(idx)) return true;
        long start = (long)idx * ChunkSize;
        return _diskRanges.IsRangeComplete(start, Math.Min(start + ChunkSize, _contentLength));
    }

    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastLog = 0;

        while (!ct.IsCancellationRequested && !_disposing)
        {
            int chunk = -1;

            lock (_queueLock)
            {
                while (_downloadQueue.Count > 0)
                {
                    var c = _downloadQueue.Dequeue();
                    _queuedChunks.Remove(c);
                    if (!HasChunk(c) && !_pendingDownloads.ContainsKey(c)) { chunk = c; break; }
                }
            }

            if (chunk < 0)
            {
                if (IsAllDownloaded())
                {
                    _downloadComplete = true;
                    if (sw.Elapsed.TotalSeconds > 1)
                        Log.Info($"Download complete in {sw.Elapsed.TotalSeconds:F1}s");
                    break;
                }

                try { await Task.Delay(100, ct); } catch { break; }
                continue;
            }

            try
            {
                await _downloadSemaphore.WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }

            _ = DownloadChunkSafeAsync(chunk, ct);

            long bytes = Volatile.Read(ref _bytesDownloaded);
            if (bytes - lastLog >= ProgressLogIntervalBytes)
            {
                lastLog = bytes;
                double speed = bytes / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                Log.Info($"Download: {bytes / 1024}KB @ {speed:F0}KB/s ({bytes * 100.0 / _contentLength:F1}%)");
            }
        }
    }

    private async Task DownloadChunkSafeAsync(int idx, CancellationToken ct)
    {
        try { await DownloadChunkAsync(idx, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Chunk {idx} failed: {ex.Message}"); }
        finally { _downloadSemaphore.Release(); }
    }

    private async Task DownloadChunkAsync(int idx, CancellationToken ct)
    {
        if (HasChunk(idx) || _disposing) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDownloads.TryAdd(idx, tcs.Task))
        {
            if (_pendingDownloads.TryGetValue(idx, out var t))
                try { await t.WaitAsync(ct); } catch { }
            return;
        }

        byte[]? buffer = null;

        try
        {
            long start = (long)idx * ChunkSize;
            long end = Math.Min(start + ChunkSize - 1, _contentLength - 1);

            using var req = new HttpRequestMessage(HttpMethod.Get, _url);
            req.Headers.Range = new RangeHeaderValue(start, end);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token);
            cts.CancelAfter(ChunkDownloadTimeoutMs);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();

            buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

            using var netStream = await resp.Content.ReadAsStreamAsync(cts.Token);

            int totalRead = 0;
            int bytesRead;
            while ((bytesRead = await netStream.ReadAsync(buffer, totalRead, ChunkSize - totalRead, cts.Token)) > 0)
            {
                totalRead += bytesRead;
            }

            if (!_chunks.ContainsKey(idx) && !_disposing)
            {
                _chunks[idx] = buffer;

                Interlocked.Add(ref _bytesDownloaded, totalRead);
                _dataAvailable.Set();

                // Копия для записи на диск
                if (!_disposing && _cacheFile != null)
                {
                    byte[] diskBuffer = ArrayPool<byte>.Shared.Rent(totalRead);
                    Buffer.BlockCopy(buffer, 0, diskBuffer, 0, totalRead);
                    _diskChannel.Writer.TryWrite((start, diskBuffer, totalRead));
                }

                buffer = null; // Передали владение в _chunks

                if (_chunks.Count > MaxRamChunks) TrimRamCache();
            }

            tcs.SetResult();
        }
        catch (OperationCanceledException) { tcs.TrySetCanceled(); }
        catch (Exception ex) { tcs.TrySetException(ex); }
        finally
        {
            _pendingDownloads.TryRemove(idx, out _);
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void TrimRamCache()
    {
        if (_chunks.Count <= MaxRamChunks / 2) return;

        int current = (int)(Volatile.Read(ref _position) / ChunkSize);

        var toRemove = _chunks.Keys
            .Where(i => i < current - 4 || i > current + ReadAheadChunks * 2)
            .Where(i =>
            {
                long s = (long)i * ChunkSize;
                return _diskRanges.IsRangeComplete(s, Math.Min(s + ChunkSize, _contentLength));
            })
            .OrderByDescending(i => Math.Abs(i - current))
            .Take(_chunks.Count / 3);

        foreach (var i in toRemove)
        {
            if (_chunks.TryRemove(i, out var buf))
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    private bool IsAllDownloaded()
    {
        for (int i = 0; i < _totalChunks; i++)
            if (!HasChunk(i)) return false;
        return true;
    }

    private async Task DiskWriterLoopAsync()
    {
        try
        {
            await foreach (var (pos, data, len) in _diskChannel.Reader.ReadAllAsync(_disposeCts.Token))
            {
                if (_disposing || _cacheFile == null)
                {
                    ArrayPool<byte>.Shared.Return(data);
                    continue;
                }

                try
                {
                    if (_fileLock.TryEnterWriteLock(500))
                    {
                        try
                        {
                            if (_cacheFile != null)
                            {
                                _cacheFile.Seek(pos, SeekOrigin.Begin);
                                _cacheFile.Write(data, 0, len);
                            }
                        }
                        finally { _fileLock.ExitWriteLock(); }

                        _diskRanges.MarkComplete(pos, pos + len);
                    }
                }
                catch { }
                finally
                {
                    ArrayPool<byte>.Shared.Return(data);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }

        if (_disposing) return;

        Try(() =>
        {
            if (_fileLock.TryEnterWriteLock(1000))
            {
                try { _cacheFile?.Flush(); }
                finally { _fileLock.ExitWriteLock(); }
            }
        });
        Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposing = true;

        Log.Info($"Disposing ({DownloadProgress:F1}% buffered)");

        if (disposing)
        {
            // 1. Отменяем все операции
            Try(_downloadCts.Cancel);
            Try(_disposeCts.Cancel);
            Try(_dataAvailable.Set);
            Try(() => _diskChannel.Writer.TryComplete());

            // 2. Ждём завершения задач
            Try(() => _diskWriterTask.Wait(2000));
            Try(() => _downloadLoop?.Wait(1000));

            // 3. Сохраняем метаданные
            Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));

            // 4. Закрываем файл
            if (_fileLock.TryEnterWriteLock(2000))
            {
                try
                {
                    Try(() => _cacheFile?.Flush());
                    Try(() => _cacheFile?.Dispose());
                    _cacheFile = null;
                }
                finally
                {
                    _fileLock.ExitWriteLock();
                }
            }
            else
            {
                // Принудительно закрываем
                Try(() => _cacheFile?.Dispose());
                _cacheFile = null;
            }

            // 5. Возвращаем буферы в пул
            foreach (var buffer in _chunks.Values)
            {
                Try(() => ArrayPool<byte>.Shared.Return(buffer));
            }
            _chunks.Clear();
            _pendingDownloads.Clear();

            // 6. Освобождаем ресурсы
            Try(_dataAvailable.Dispose);
            Try(_fileLock.Dispose);
            Try(_downloadCts.Dispose);
            Try(_disposeCts.Dispose);
            Try(_downloadSemaphore.Dispose);
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private static void Try(Action a) { try { a(); } catch { } }
}

