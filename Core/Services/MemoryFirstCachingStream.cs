using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net; // Добавлено для HttpStatusCode
using System.Net.Http.Headers;
using System.Threading.Channels;
using MyLiteMusicPlayer.Core.Models;

namespace MyLiteMusicPlayer.Core.Services;

/// <summary>
/// Adaptive Streaming Buffer optimized for VLC.
/// Now fully dynamic based on injected StreamingConfig.
/// </summary>
public sealed class MemoryFirstCachingStream : Stream
{
    // Config fields
    private readonly int _chunkSize;
    private readonly int _readAheadChunks;
    private readonly int _maxConcurrentDownloads;
    private readonly int _maxRamChunks;
    private readonly int _downloadTimeoutMs;

    // Fixed internals
    private const int ProgressLogIntervalBytes = 6 * 1024 * 1024;
    private const int MaxFileOpenRetries = 10;
    private const int FileOpenRetryDelayMs = 100;

    private readonly string _trackId;
    
    // ИЗМЕНЕНИЕ 1: Убрали readonly, чтобы обновлять ссылку при протухании
    private string _url; 
    
    // ИЗМЕНЕНИЕ 2: Функция для обновления ссылки
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

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
    
    // Блокировка для обновления URL, чтобы 5 потоков не обновляли его одновременно
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly SemaphoreSlim _fileSemaphore = new(1, 1);
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

    // ИЗМЕНЕНИЕ 3: Обновленный конструктор
    public MemoryFirstCachingStream(
        string trackId, string url, long contentLength,
        HttpClient http, StreamCacheManager cacheManager, StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _trackId = trackId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;

        // Apply Config
        _chunkSize = config.ChunkSize;
        _readAheadChunks = config.ReadAheadChunks;
        _maxConcurrentDownloads = config.MaxConcurrentDownloads;
        _maxRamChunks = config.MaxRamChunks;
        _downloadTimeoutMs = config.DownloadTimeoutMs;

        _cachePath = cacheManager.GetCachePath(trackId);
        _downloadCts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        _totalChunks = (int)((_contentLength + _chunkSize - 1) / _chunkSize);

        var meta = cacheManager.LoadOrCreateMetadata(trackId, url, contentLength);
        _diskRanges = RangeMap.Deserialize(meta.RangesJson);
        _bytesDownloaded = _diskRanges.DownloadedBytes;

        _cacheFile = OpenCacheFileWithRetry(_cachePath);

        if (_cacheFile != null && _cacheFile.Length < _contentLength)
            _cacheFile.SetLength(_contentLength);

        // Bounded channel to prevent RAM explosion if disk is slow
        _diskChannel = Channel.CreateBounded<(long, byte[], int)>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        _diskWriterTask = Task.Run(DiskWriterLoopAsync);

        Log.Info($"Opened {trackId}: {contentLength / 1024 / 1024}MB (Mode: {config.GetType().Name}). Cached: {_diskRanges.DownloadedBytes / 1024}KB");
    }

    private static FileStream? OpenCacheFileWithRetry(string path)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxFileOpenRetries; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite,
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
        return null;
    }

    public async Task<bool> PreBufferAsync(CancellationToken ct)
    {
        if (_disposed || _disposing) return false;

        CancellationTokenSource? linkedCts = null;

        try
        {
            try
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token, _disposeCts.Token);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            using (linkedCts)
            {
                var token = linkedCts.Token;

                _downloadLoop ??= Task.Run(() => DownloadLoopAsync(token), token);

                if (HasChunk(0)) return true;

                var sw = Stopwatch.StartNew();
                EnqueueUrgent(0);

                while (!HasChunk(0))
                {
                    if (_disposed || _disposing || token.IsCancellationRequested) return false;

                    try
                    {
                        if (!_dataAvailable.Wait(200, token))
                        {
                            if (sw.ElapsedMilliseconds > _downloadTimeoutMs)
                            {
                                Log.Error($"PreBuffer timeout for {_trackId} after {sw.ElapsedMilliseconds}ms");
                                return false;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    if (!HasChunk(0)) _dataAvailable.Reset();
                }
                return true;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (ex is ObjectDisposedException) return false;
            Log.Error($"PreBuffer Error: {ex.Message}");
            return false;
        }
    }

    public void CancelPendingReads()
    {
        if (!_disposeCts.IsCancellationRequested)
        {
            try { _disposeCts.Cancel(); } catch { }
        }
    }

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
            while (!HasChunk(chunkIndex))
            {
                if (_disposed || _disposing || _disposeCts.IsCancellationRequested) return 0;

                EnqueueUrgent(chunkIndex);

                try
                {
                    if (!_dataAvailable.Wait(500, _disposeCts.Token)) { }
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }

                if (!HasChunk(chunkIndex)) _dataAvailable.Reset();
            }

            int bytesRead = TryReadChunk(chunkIndex, offsetInChunk, buffer, offset, toRead);

            if (bytesRead > 0)
            {
                Interlocked.Add(ref _position, bytesRead);
                EnqueueReadAhead(chunkIndex);
            }

            return bytesRead;
        }
        catch (Exception)
        {
            return 0;
        }
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
        Volatile.Write(ref _position, newPos);

        int newChunk = (int)(newPos / _chunkSize);
        EnqueueUrgent(newChunk);

        return newPos;
    }

    private int TryReadChunk(int idx, int off, byte[] buf, int bufOff, int count)
    {
        if (_chunks.TryGetValue(idx, out var chunk))
        {
            int usefulDataLength = (idx == _totalChunks - 1)
                ? (int)(_contentLength - ((long)idx * _chunkSize))
                : _chunkSize;

            int available = Math.Min(count, usefulDataLength - off);
            if (available > 0) Buffer.BlockCopy(chunk, off, buf, bufOff, available);
            return available;
        }

        long start = (long)idx * _chunkSize;
        if (_diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength)))
        {
            return ReadFromDisk(start + off, buf, bufOff, count);
        }

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
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            Log.Error($"Disk read error: {ex.Message}");
            throw;
        }
    }

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

    private void TryEnqueue(int idx, int priority)
    {
        if (HasChunk(idx)) return;
        if (_pendingDownloads.ContainsKey(idx)) return;

        if (_queuedChunks.Add(idx))
        {
            _downloadQueue.Enqueue(idx, priority);
        }
    }

    private bool HasChunk(int idx)
    {
        if (_chunks.ContainsKey(idx)) return true;
        long start = (long)idx * _chunkSize;
        return _diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength));
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
                    if (sw.Elapsed.TotalSeconds > 1) Log.Info($"Download complete in {sw.Elapsed.TotalSeconds:F1}s");
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
        try
        {
            await DownloadChunkAsync(idx, ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                Log.Warn($"Chunk {idx} error: {ex.Message}");
        }
        finally
        {
            _pendingDownloads.TryRemove(idx, out _);
            _downloadSemaphore.Release();
        }
    }

    // ИЗМЕНЕНИЕ 4: Полностью переписанный метод с поддержкой повторных попыток при 403
    private async Task DownloadChunkAsync(int idx, CancellationToken ct)
    {
        if (HasChunk(idx) || _disposing) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDownloads.TryAdd(idx, tcs.Task)) return;

        byte[]? buffer = null;
        int maxRetries = 2; // Сколько раз пытаться обновить ссылку
        int retry = 0;

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

                // Используем ResponseHeadersRead для контроля статус-кода
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // Обработка 403 Forbidden
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (retry < maxRetries && _urlRefresher != null)
                    {
                        Log.Warn($"Chunk {idx} got 403. Refreshing URL (Attempt {retry + 1})...");
                        
                        // Блокируем, чтобы только один поток обновлял URL
                        await _refreshLock.WaitAsync(cts.Token);
                        try
                        {
                             // Проверяем, может URL уже обновил другой поток
                             // (в этом примере просто дергаем рефрешер, но можно сравнить URL)
                             var newUrl = await _urlRefresher(cts.Token);
                             if (!string.IsNullOrEmpty(newUrl))
                             {
                                 _url = newUrl;
                                 Log.Info("URL refreshed successfully.");
                             }
                        }
                        catch (Exception refreshEx)
                        {
                            Log.Error($"Failed to refresh URL: {refreshEx.Message}");
                        }
                        finally
                        {
                            _refreshLock.Release();
                        }
                        
                        retry++;
                        continue; // Перезапуск цикла с новым URL
                    }
                }

                resp.EnsureSuccessStatusCode();

                buffer = ArrayPool<byte>.Shared.Rent(_chunkSize);
                using var netStream = await resp.Content.ReadAsStreamAsync(cts.Token);

                int totalRead = 0, bytesRead;
                while ((bytesRead = await netStream.ReadAsync(buffer, totalRead, _chunkSize - totalRead, cts.Token)) > 0)
                    totalRead += bytesRead;

                if (!_chunks.ContainsKey(idx) && !_disposing)
                {
                    _chunks[idx] = buffer;
                    Interlocked.Add(ref _bytesDownloaded, totalRead);
                    _dataAvailable.Set();

                    if (_cacheFile != null && !_disposing)
                    {
                        byte[] diskBuf = ArrayPool<byte>.Shared.Rent(totalRead);
                        Buffer.BlockCopy(buffer, 0, diskBuf, 0, totalRead);
                        await _diskChannel.Writer.WriteAsync((start, diskBuf, totalRead), cts.Token);
                    }
                    buffer = null; // Prevent return in finally
                    if (_chunks.Count > _maxRamChunks) TrimRamCache();
                }
                
                // Успешно завершили
                tcs.SetResult();
                break; 
            }
            catch (Exception ex)
            {
                // Если мы уже исчерпали попытки или это не сетевая ошибка, которую мы лечим
                if (retry >= maxRetries || ex is OperationCanceledException)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                
                // Если произошла другая Http ошибка, но не 403, можно попробовать просто подождать
                if (ex is HttpRequestException)
                {
                    Log.Warn($"Chunk {idx} generic http error: {ex.Message}. Retrying...");
                    await Task.Delay(500, ct);
                    retry++;
                    continue;
                }
                
                tcs.TrySetException(ex);
                throw;
            }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void TrimRamCache()
    {
        if (_chunks.Count <= _maxRamChunks / 2) return;
        int current = (int)(Volatile.Read(ref _position) / _chunkSize);
        var toRemove = _chunks.Keys
            .Where(i => i < current - 4 || i > current + _readAheadChunks * 4)
            .OrderByDescending(i => Math.Abs(i - current))
            .Take(_chunks.Count / 3);

        foreach (var i in toRemove)
        {
            if (_chunks.TryRemove(i, out var buf))
                ArrayPool<byte>.Shared.Return(buf);
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
        int bytesWrittenSinceSave = 0;
        const int SaveThreshold = 512 * 1024;

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
                    await _fileSemaphore.WaitAsync(_disposeCts.Token);
                    try
                    {
                        if (_cacheFile != null)
                        {
                            _cacheFile.Seek(pos, SeekOrigin.Begin);
                            await _cacheFile.WriteAsync(data, 0, len, _disposeCts.Token);
                        }
                    }
                    finally { _fileSemaphore.Release(); }

                    _diskRanges.MarkComplete(pos, pos + len);
                    bytesWrittenSinceSave += len;

                    if (bytesWrittenSinceSave >= SaveThreshold)
                    {
                        Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));
                        bytesWrittenSinceSave = 0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Disk write error: {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(data);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }

        if (!_disposing)
        {
            Try(() =>
            {
                if (_fileSemaphore.Wait(1000))
                {
                    try { _cacheFile?.Flush(); } finally { _fileSemaphore.Release(); }
                }
            });
            Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposing = true;

        Log.Info($"Disposing {_trackId} ({DownloadProgress:F1}%)");

        if (disposing)
        {
            Try(_downloadCts.Cancel);
            Try(_disposeCts.Cancel);
            Try(_dataAvailable.Set);
            Try(() => _diskChannel.Writer.TryComplete());
            Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));

            Task.Run(async () =>
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
                catch { }
                finally
                {
                    foreach (var buf in _chunks.Values)
                        Try(() => ArrayPool<byte>.Shared.Return(buf));
                    _chunks.Clear();

                    Try(_fileSemaphore.Dispose);
                    Try(_downloadSemaphore.Dispose);
                    Try(_refreshLock.Dispose); // Не забываем освободить лок
                    Try(_downloadCts.Dispose);
                    Try(_disposeCts.Dispose);
                }
            });
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private static void Try(Action a) { try { a(); } catch { } }
}