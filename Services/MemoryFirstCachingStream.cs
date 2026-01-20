using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.Channels;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Memory-First Caching Stream v7 - True streaming with read-ahead buffer.
/// 
/// ИЗМЕНЕНИЯ:
/// 1. Read-ahead buffer - качаем только N MB впереди позиции воспроизведения
/// 2. Chunked requests для больших файлов (YouTube throttling fix)
/// 3. Пауза загрузки когда буфер заполнен
/// 4. Адаптивный размер блока для больших файлов
/// </summary>
public sealed class MemoryFirstCachingStream : Stream
{
    #region Constants

    private const int BlockSize = 128 * 1024;                    // 128KB блоки
    private const int SmallBlockSize = 64 * 1024;                // 64KB для больших файлов
    private const int MaxRamBlocks = 256;                        // 32MB в RAM макс
    private const int ReadAheadBytes = 8 * 1024 * 1024;          // 8MB read-ahead (TRUE STREAMING!)
    private const int ReadWaitTimeoutMs = 8000;                  // 8 сек макс ожидание в Read
    private const int MinPreBufferBlocks = 1;                       // 64KB для больших файлов (было 2)
    private const int PreBufferTimeoutMs = 3000;                    // 3 сек (было 5)
    private const int LargeFileThreshold = 20 * 1024 * 1024;        // 20MB (было 15MB)
    private const int ChunkSize = 10 * 1024 * 1024;                 // 10MB chunks (было 5MB)
    private const int ParallelChunks = 2;                           // Параллельные потоки для chunked

    #endregion

    #region State

    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly RangeMap _diskRanges;
    private readonly bool _isLargeFile;
    private readonly int _effectiveBlockSize;

    // === RAM BUFFER ===
    private readonly ConcurrentDictionary<long, byte[]> _ramBlocks = new();
    private readonly ConcurrentDictionary<long, int> _blockSizes = new();

    // === DOWNLOAD STATE ===
    private long _downloadPosition;
    private long _downloadedBytes;
    private volatile bool _downloadActive;
    private readonly object _downloadLock = new();
    private int _downloadGeneration;
    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;
    private readonly Stopwatch _downloadStopwatch = new();
    private DateTime _lastDownloadStart = DateTime.MinValue;

    // === DISK WRITER ===
    private FileStream? _cacheFile;
    private readonly ReaderWriterLockSlim _fileLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Channel<(long Position, byte[] Data, int Length)> _diskChannel;
    private readonly Task _diskWriterTask;

    // === EVENTS ===
    private readonly ManualResetEventSlim _dataAvailable = new(false);

    // === POSITION ===
    private long _position;
    private long _lastLoggedSeekPos = -1;

    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;
    private volatile bool _disposing;

    #endregion

    #region Properties

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
            long diskBytes = _diskRanges.DownloadedBytes;
            long ramBytes = Volatile.Read(ref _downloadedBytes);
            long total = Math.Min(Math.Max(diskBytes, ramBytes), _contentLength);
            return (double)total / _contentLength * 100;
        }
    }

    public bool IsFullyDownloaded => _diskRanges.IsFullyDownloaded(_contentLength);

    #endregion

    #region Constructor

    public MemoryFirstCachingStream(
        string trackId,
        string url,
        long contentLength,
        HttpClient http,
        StreamCacheManager cacheManager)
    {
        _trackId = trackId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _cachePath = cacheManager.GetCachePath(trackId);
        _isLargeFile = contentLength > LargeFileThreshold;
        _effectiveBlockSize = _isLargeFile ? SmallBlockSize : BlockSize;

        _diskRanges = cacheManager.LoadRanges(trackId);

        _cacheFile = new FileStream(
            _cachePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        if (_cacheFile.Length < _contentLength)
        {
            _cacheFile.SetLength(_contentLength);
        }

        _diskChannel = Channel.CreateBounded<(long, byte[], int)>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _diskWriterTask = Task.Run(DiskWriterLoopAsync);

        long cachedKB = _diskRanges.DownloadedBytes / 1024;
        Debug.WriteLine($"[MemoryFirst] Opened {trackId}, size: {contentLength / 1024 / 1024}MB, " +
                        $"disk cache: {cachedKB}KB, large: {_isLargeFile}, block: {_effectiveBlockSize / 1024}KB");
    }

    #endregion

    #region === PRE-BUFFER ===


    public async Task<bool> PreBufferAsync(int requestedBytes, CancellationToken ct)
    {
        if (_disposed) return false;

        // ✅ АДАПТИВНЫЙ PREBUFFER - для больших файлов меньше
        int minBytes = _isLargeFile
            ? _effectiveBlockSize                          // 64KB для больших
            : MinPreBufferBlocks * _effectiveBlockSize;    // 128KB для маленьких

        // Проверяем disk cache
        if (_diskRanges.IsRangeComplete(0, minBytes))
        {
            Debug.WriteLine($"[MemoryFirst] PreBuffer: disk cache hit ({_diskRanges.DownloadedBytes / 1024}KB)");

            if (!IsFullyDownloaded)
            {
                long nextMissing = FindNextMissingPosition(0);
                if (nextMissing < _contentLength)
                {
                    StartContinuousDownload(nextMissing);
                }
            }
            return true;
        }

        // Запускаем download
        StartContinuousDownload(0);

        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[MemoryFirst] PreBuffer: waiting for {minBytes / 1024}KB...");

        try
        {
            while (!ct.IsCancellationRequested && !_disposing)
            {
                if (HasDataInRange(0, minBytes))
                {
                    Debug.WriteLine($"[MemoryFirst] PreBuffer OK in {sw.ElapsedMilliseconds}ms");
                    return true;
                }

                if (sw.ElapsedMilliseconds > PreBufferTimeoutMs)
                {
                    long downloaded = Volatile.Read(ref _downloadedBytes);
                    int ramBlocks = _ramBlocks.Count;

                    // ✅ ДЛЯ БОЛЬШИХ ФАЙЛОВ - стартуем если есть хоть что-то
                    bool hasEnough = _isLargeFile
                        ? downloaded >= _effectiveBlockSize || ramBlocks >= 1
                        : downloaded >= _effectiveBlockSize * 2 || ramBlocks >= 2;

                    Debug.WriteLine($"[MemoryFirst] PreBuffer timeout ({sw.ElapsedMilliseconds}ms), " +
                                    $"downloaded: {downloaded / 1024}KB, blocks: {ramBlocks}, " +
                                    $"starting anyway: {hasEnough}");
                    return hasEnough;
                }

                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return Volatile.Read(ref _downloadedBytes) > 0 || _ramBlocks.Count > 0;
        }

        return false;
    }

    private bool HasDataInRange(long start, int count)
    {
        long startBlock = start / _effectiveBlockSize;
        long endBlock = (start + count - 1) / _effectiveBlockSize;

        for (long block = startBlock; block <= endBlock; block++)
        {
            long blockStart = block * _effectiveBlockSize;
            long blockEnd = Math.Min(blockStart + _effectiveBlockSize, _contentLength);

            if (!_ramBlocks.ContainsKey(block) && !_diskRanges.IsRangeComplete(blockStart, blockEnd))
            {
                return false;
            }
        }
        return true;
    }

    #endregion

    #region === READ ===

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed) return 0;

        long pos = Volatile.Read(ref _position);
        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, _contentLength - pos);
        if (count <= 0) return 0;

        int totalRead = 0;
        int waitAttempts = 0;
        int maxWaitAttempts = ReadWaitTimeoutMs / 100;

        while (totalRead < count && !_disposed && waitAttempts < maxWaitAttempts)
        {
            long currentPos = pos + totalRead;
            int remaining = count - totalRead;

            long blockIndex = currentPos / _effectiveBlockSize;
            int offsetInBlock = (int)(currentPos % _effectiveBlockSize);
            int toRead = Math.Min(remaining, _effectiveBlockSize - offsetInBlock);

            // 1. Try RAM
            if (_ramBlocks.TryGetValue(blockIndex, out var block))
            {
                int blockSize = _blockSizes.GetValueOrDefault(blockIndex, block.Length);
                int available = Math.Min(toRead, blockSize - offsetInBlock);

                if (available > 0)
                {
                    Buffer.BlockCopy(block, offsetInBlock, buffer, offset + totalRead, available);
                    totalRead += available;
                    waitAttempts = 0;
                    continue;
                }
            }

            // 2. Try Disk
            long blockStart = blockIndex * _effectiveBlockSize;
            long blockEnd = Math.Min(blockStart + _effectiveBlockSize, _contentLength);
            if (_diskRanges.IsRangeComplete(blockStart, blockEnd))
            {
                int diskRead = ReadFromDisk(currentPos, buffer, offset + totalRead, toRead);
                if (diskRead > 0)
                {
                    totalRead += diskRead;
                    waitAttempts = 0;
                    continue;
                }
            }

            // 3. Wait for data
            waitAttempts++;
            _dataAvailable.Reset();
            _dataAvailable.Wait(100);

            if (waitAttempts % 5 == 0)
            {
                EnsureDownloadRunning(currentPos);
            }
        }

        if (waitAttempts >= maxWaitAttempts && totalRead == 0)
        {
            Debug.WriteLine($"[MemoryFirst] READ TIMEOUT at {pos / 1024}KB");
        }

        Interlocked.Add(ref _position, totalRead);
        return totalRead;
    }

    private int ReadFromDisk(long position, byte[] buffer, int offset, int count)
    {
        if (_cacheFile == null || _disposing) return 0;

        try
        {
            _fileLock.EnterReadLock();
            try
            {
                if (_cacheFile == null) return 0;
                _cacheFile.Seek(position, SeekOrigin.Begin);
                return _cacheFile.Read(buffer, offset, count);
            }
            finally
            {
                _fileLock.ExitReadLock();
            }
        }
        catch
        {
            return 0;
        }
    }

    private void EnsureDownloadRunning(long readPosition)
    {
        lock (_downloadLock)
        {
            if (_downloadActive) return;
        }

        long nextMissing = FindNextMissingPosition(readPosition);
        if (nextMissing < _contentLength)
        {
            StartContinuousDownload(nextMissing);
        }
    }

    #endregion

    #region === SEEK ===

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) return 0;

        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _contentLength + offset,
            _ => throw new ArgumentException("Invalid SeekOrigin", nameof(origin))
        };

        newPosition = Math.Clamp(newPosition, 0, _contentLength);

        long oldPosition = Volatile.Read(ref _position);
        Volatile.Write(ref _position, newPosition);

        if (Math.Abs(newPosition - _lastLoggedSeekPos) > _effectiveBlockSize * 4)
        {
            _lastLoggedSeekPos = newPosition;
        }

        // Перезапуск download если нужно
        bool needRestart;
        lock (_downloadLock)
        {
            needRestart = !_downloadActive || !HasDataInRange(newPosition, _effectiveBlockSize * 4);
        }

        if (needRestart)
        {
            long nextMissing = FindNextMissingPosition(newPosition);
            if (nextMissing < _contentLength && nextMissing < newPosition + _effectiveBlockSize * 8)
            {
                StartContinuousDownload(nextMissing);
            }
        }

        return newPosition;
    }

    #endregion

    #region === CONTINUOUS DOWNLOAD WITH CHUNKING ===

    private void StartContinuousDownload(long fromPosition)
    {
        if (_disposing) return;

        int myGeneration;
        CancellationTokenSource myCts;

        lock (_downloadLock)
        {
            var timeSinceLastStart = DateTime.UtcNow - _lastDownloadStart;
            if (timeSinceLastStart.TotalMilliseconds < 200 && _downloadActive)
            {
                return;
            }

            if (_downloadActive)
            {
                long dlPos = Volatile.Read(ref _downloadPosition);
                long readPos = Volatile.Read(ref _position);

                if (dlPos >= readPos - _effectiveBlockSize * 2 &&
                    dlPos < readPos + ReadAheadBytes)
                {
                    return;
                }
            }

            myGeneration = ++_downloadGeneration;
            _downloadActive = true;
            _lastDownloadStart = DateTime.UtcNow;

            var oldCts = _downloadCts;
            myCts = new CancellationTokenSource();
            _downloadCts = myCts;

            try { oldCts?.Cancel(); } catch { }
        }

        var ct = CancellationTokenSource.CreateLinkedTokenSource(
            myCts.Token, _disposeCts.Token).Token;

        long startPos = FindNextMissingPosition(fromPosition);

        if (startPos >= _contentLength)
        {
            lock (_downloadLock)
            {
                if (_downloadGeneration == myGeneration)
                    _downloadActive = false;
            }
            return;
        }

        Volatile.Write(ref _downloadPosition, startPos);
        _downloadStopwatch.Restart();

        _downloadTask = Task.Run(async () =>
        {
            try
            {
                if (_isLargeFile)
                {
                    // ✅ ДЛЯ БОЛЬШИХ ФАЙЛОВ - СРАЗУ ПАРАЛЛЕЛЬНЫЙ CHUNKED
                    // Не тратим время на throttling detection
                    Debug.WriteLine($"[MemoryFirst] Large file detected, using parallel chunked download");
                    await ParallelChunkedDownloadAsync(startPos, myGeneration, ct);
                }
                else
                {
                    // Для маленьких - обычный download
                    await ContinuousDownloadLoopAsync(startPos, myGeneration, ct);
                }
            }
            finally
            {
                lock (_downloadLock)
                {
                    if (_downloadGeneration == myGeneration)
                    {
                        _downloadActive = false;
                    }
                }
                _dataAvailable.Set();
            }
        });
    }

    private async Task ParallelChunkedDownloadAsync(long startPosition, int generation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[MemoryFirst] PARALLEL CHUNKED download starting from {startPosition / 1024}KB");

        long position = startPosition;
        var activeTasks = new List<Task>();
        var semaphore = new SemaphoreSlim(ParallelChunks, ParallelChunks);
        long totalDownloaded = 0;
        long lastLogBytes = 0;

        while (position < _contentLength && !ct.IsCancellationRequested && !_disposing)
        {
            // === READ-AHEAD CHECK ===
            long readPos = Volatile.Read(ref _position);
            long aheadBytes = position - readPos;

            if (aheadBytes > ReadAheadBytes && position > readPos)
            {
                await Task.Delay(200, ct);
                continue;
            }

            // ✅ Проверяем поколение БЕЗ await
            bool shouldReturn = false;
            lock (_downloadLock)
            {
                if (_downloadGeneration != generation)
                {
                    shouldReturn = true;
                }
            }

            if (shouldReturn)
            {
                // ✅ await ВНЕ lock
                await Task.WhenAll(activeTasks);
                return;
            }

            // Пропускаем уже скачанные диапазоны
            if (_diskRanges.IsRangeComplete(position, Math.Min(position + ChunkSize, _contentLength)))
            {
                position += ChunkSize;
                continue;
            }

            // Ждём свободный слот
            await semaphore.WaitAsync(ct);

            long chunkStart = position;
            long chunkEnd = Math.Min(position + ChunkSize - 1, _contentLength - 1);
            position += ChunkSize;

            // Запускаем chunk в отдельной задаче
            var task = Task.Run(async () =>
            {
                try
                {
                    long downloaded = await DownloadChunkAsync(chunkStart, chunkEnd, generation, ct);

                    if (downloaded > 0)
                    {
                        Interlocked.Add(ref totalDownloaded, downloaded);

                        // Progress log every 2MB
                        long current = Interlocked.Read(ref totalDownloaded);
                        if (current - lastLogBytes >= 2 * 1024 * 1024)
                        {
                            double elapsed = sw.Elapsed.TotalSeconds + 0.001;
                            double speed = current / 1024.0 / elapsed;
                            double progress = (double)(chunkStart + downloaded) / _contentLength * 100;
                            Debug.WriteLine($"[MemoryFirst] {current / 1024}KB @ {speed:F0}KB/s ({progress:F1}%)");
                            Interlocked.Exchange(ref lastLogBytes, current);
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);

            activeTasks.Add(task);

            // Cleanup завершённых задач
            activeTasks.RemoveAll(t => t.IsCompleted);
        }

        // Ждём завершения всех задач
        await Task.WhenAll(activeTasks);

        if (totalDownloaded > 0)
        {
            double elapsed = sw.Elapsed.TotalSeconds + 0.001;
            double speed = totalDownloaded / 1024.0 / elapsed;
            Debug.WriteLine($"[MemoryFirst] Parallel download done: {totalDownloaded / 1024}KB ({speed:F0}KB/s)");
        }
    }

    /// <summary>
    /// Скачать один chunk
    /// </summary>
    private async Task<long> DownloadChunkAsync(long chunkStart, long chunkEnd, int generation, CancellationToken ct)
    {
        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount < maxRetries && !ct.IsCancellationRequested && !_disposing)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(chunkStart, chunkEnd);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    retryCount++;
                    await Task.Delay(300 * retryCount, ct);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[_effectiveBlockSize];
                long chunkPosition = chunkStart;
                long downloaded = 0;

                while (chunkPosition <= chunkEnd && !ct.IsCancellationRequested && !_disposing)
                {
                    // ✅ Проверяем поколение БЕЗ await
                    bool shouldReturn = false;
                    lock (_downloadLock)
                    {
                        if (_downloadGeneration != generation)
                        {
                            shouldReturn = true;
                        }
                    }

                    if (shouldReturn) return downloaded;

                    int toRead = (int)Math.Min(_effectiveBlockSize, chunkEnd - chunkPosition + 1);
                    int totalRead = 0;

                    while (totalRead < toRead)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(totalRead, toRead - totalRead), ct);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (totalRead > 0)
                    {
                        long blockIndex = chunkPosition / _effectiveBlockSize;
                        var blockData = new byte[totalRead];
                        Buffer.BlockCopy(buffer, 0, blockData, 0, totalRead);

                        _ramBlocks[blockIndex] = blockData;
                        _blockSizes[blockIndex] = totalRead;

                        if (!_disposing)
                        {
                            _diskChannel.Writer.TryWrite((chunkPosition, blockData, totalRead));
                        }

                        _dataAvailable.Set();

                        downloaded += totalRead;

                        // ✅ Atomic update позиции
                        long currentMax = Volatile.Read(ref _downloadPosition);
                        long newPos = chunkPosition + totalRead;
                        while (newPos > currentMax)
                        {
                            long original = Interlocked.CompareExchange(ref _downloadPosition, newPos, currentMax);
                            if (original == currentMax) break;
                            currentMax = Volatile.Read(ref _downloadPosition);
                        }

                        Interlocked.Add(ref _downloadedBytes, totalRead);

                        // Trim RAM
                        if (_ramBlocks.Count > MaxRamBlocks)
                        {
                            TrimRamCache();
                        }
                    }

                    chunkPosition += totalRead;
                    if (totalRead < toRead) break;
                }

                return downloaded;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                retryCount++;
                await Task.Delay(300 * retryCount, ct);
            }
            catch (HttpRequestException)
            {
                retryCount++;
                await Task.Delay(500 * retryCount, ct);
            }
        }

        return 0;
    }

    /// <summary>
    /// Обычный download для маленьких файлов
    /// </summary>
    private async Task ContinuousDownloadLoopAsync(long startPosition, int generation, CancellationToken ct)
    {
        long bytesDownloaded = 0;
        long lastLogBytes = 0;
        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount < maxRetries && !ct.IsCancellationRequested && !_disposing)
        {
            try
            {
                Debug.WriteLine($"[MemoryFirst] Download starting from {startPosition / 1024}KB");

                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(startPosition + bytesDownloaded, null);

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestCts.CancelAfter(TimeSpan.FromSeconds(15));

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    retryCount++;
                    await Task.Delay(500 * retryCount, ct);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[_effectiveBlockSize];
                long position = startPosition + bytesDownloaded;

                while (!ct.IsCancellationRequested && !_disposing && position < _contentLength)
                {
                    // === READ-AHEAD CHECK ===
                    long readPos = Volatile.Read(ref _position);
                    long aheadBytes = position - readPos;

                    if (aheadBytes > ReadAheadBytes && position > readPos)
                    {
                        // Пауза - не качаем слишком далеко вперёд!
                        await Task.Delay(100, ct);
                        continue;
                    }

                    lock (_downloadLock)
                    {
                        if (_downloadGeneration != generation) return;
                    }

                    int toRead = (int)Math.Min(_effectiveBlockSize, _contentLength - position);
                    int totalRead = 0;

                    while (totalRead < toRead && !ct.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(totalRead, toRead - totalRead), ct);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (totalRead > 0)
                    {
                        long blockIndex = position / _effectiveBlockSize;
                        var blockData = new byte[totalRead];
                        Buffer.BlockCopy(buffer, 0, blockData, 0, totalRead);

                        _ramBlocks[blockIndex] = blockData;
                        _blockSizes[blockIndex] = totalRead;

                        if (!_disposing)
                        {
                            _diskChannel.Writer.TryWrite((position, blockData, totalRead));
                        }

                        _dataAvailable.Set();

                        bytesDownloaded += totalRead;
                        Volatile.Write(ref _downloadPosition, position + totalRead);
                        Interlocked.Add(ref _downloadedBytes, totalRead);

                        if (bytesDownloaded - lastLogBytes >= 1024 * 1024)
                        {
                            double elapsed = _downloadStopwatch.Elapsed.TotalSeconds + 0.001;
                            double speed = bytesDownloaded / 1024.0 / elapsed;
                            Debug.WriteLine($"[MemoryFirst] Downloaded {bytesDownloaded / 1024}KB @ {speed:F0}KB/s");
                            lastLogBytes = bytesDownloaded;
                        }

                        if (_ramBlocks.Count > MaxRamBlocks)
                        {
                            TrimRamCache();
                        }
                    }

                    position += totalRead;
                    if (totalRead < toRead) break;
                }

                if (bytesDownloaded > 0)
                {
                    double elapsed = _downloadStopwatch.Elapsed.TotalSeconds + 0.001;
                    double speed = bytesDownloaded / 1024.0 / elapsed;
                    Debug.WriteLine($"[MemoryFirst] Download complete: {bytesDownloaded / 1024}KB ({speed:F0}KB/s)");
                }
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                retryCount++;
                Debug.WriteLine($"[MemoryFirst] Request timeout, retry {retryCount}/{maxRetries}");
                await Task.Delay(300 * retryCount, ct);
            }
            catch (HttpRequestException ex)
            {
                retryCount++;
                Debug.WriteLine($"[MemoryFirst] HTTP error: {ex.Message}, retry {retryCount}/{maxRetries}");
                await Task.Delay(500 * retryCount, ct);
            }
        }
    }

    private long FindNextMissingPosition(long startPosition)
    {
        long pos = (startPosition / _effectiveBlockSize) * _effectiveBlockSize;

        while (pos < _contentLength)
        {
            long blockEnd = Math.Min(pos + _effectiveBlockSize, _contentLength);

            if (_ramBlocks.ContainsKey(pos / _effectiveBlockSize))
            {
                pos = blockEnd;
                continue;
            }

            if (!_diskRanges.IsRangeComplete(pos, blockEnd))
            {
                return pos;
            }

            pos = blockEnd;
        }

        return _contentLength;
    }

    private void TrimRamCache()
    {
        if (_ramBlocks.Count <= MaxRamBlocks / 2) return;

        long currentPos = Volatile.Read(ref _position);
        long currentBlock = currentPos / _effectiveBlockSize;

        var toRemove = _ramBlocks.Keys
            .Where(blockIndex =>
            {
                // Не удаляем блоки рядом с текущей позицией
                if (blockIndex >= currentBlock - 4 && blockIndex <= currentBlock + 32)
                    return false;

                // Удаляем только если есть на диске
                long blockPos = blockIndex * _effectiveBlockSize;
                return _diskRanges.IsRangeComplete(blockPos, Math.Min(blockPos + _effectiveBlockSize, _contentLength));
            })
            .OrderByDescending(b => Math.Abs(b - currentBlock))
            .Take(_ramBlocks.Count / 3)
            .ToList();

        foreach (var blockIndex in toRemove)
        {
            _ramBlocks.TryRemove(blockIndex, out _);
            _blockSizes.TryRemove(blockIndex, out _);
        }
    }

    #endregion

    #region === DISK WRITER ===

    private async Task DiskWriterLoopAsync()
    {
        try
        {
            await foreach (var (position, data, length) in _diskChannel.Reader.ReadAllAsync(_disposeCts.Token))
            {
                if (_disposing || _cacheFile == null) continue;

                try
                {
                    _fileLock.EnterWriteLock();
                    try
                    {
                        if (_cacheFile == null) continue;
                        _cacheFile.Seek(position, SeekOrigin.Begin);
                        _cacheFile.Write(data, 0, length);
                    }
                    finally
                    {
                        _fileLock.ExitWriteLock();
                    }

                    _diskRanges.MarkComplete(position, position + length);
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }

        if (!_disposing)
        {
            try
            {
                _fileLock.EnterWriteLock();
                try { _cacheFile?.Flush(); } finally { _fileLock.ExitWriteLock(); }
            }
            catch { }

            try { _cacheManager.UpdateRanges(_trackId, _diskRanges); } catch { }
        }
    }

    #endregion

    #region === DISPOSE ===

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposing = true;

        Debug.WriteLine($"[MemoryFirst] Disposing ({DownloadProgress:F1}% buffered)");

        if (disposing)
        {
            try { _disposeCts.Cancel(); } catch { }
            try { _downloadCts?.Cancel(); } catch { }
            try { _dataAvailable.Set(); } catch { }
            try { _diskChannel.Writer.TryComplete(); } catch { }
            try { _diskWriterTask.Wait(500); } catch { }
            try { _cacheManager.UpdateRanges(_trackId, _diskRanges); } catch { }

            _fileLock.EnterWriteLock();
            try
            {
                if (_cacheFile != null)
                {
                    try { _cacheFile.Flush(); } catch { }
                    try { _cacheFile.Dispose(); } catch { }
                    _cacheFile = null;
                }
            }
            finally
            {
                _fileLock.ExitWriteLock();
            }

            _ramBlocks.Clear();
            _blockSizes.Clear();

            try { _dataAvailable.Dispose(); } catch { }
            try { _fileLock.Dispose(); } catch { }
            try { _disposeCts.Dispose(); } catch { }
            try { _downloadCts?.Dispose(); } catch { }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    #endregion
}