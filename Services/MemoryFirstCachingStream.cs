using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.Channels;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Memory-First Caching Stream v10 - HEAD+TAIL prefetch for instant playback
/// 
/// ИЗМЕНЕНИЯ v10:
/// 1. Для больших файлов качаем HEAD + TAIL параллельно (moov atom fix!)
/// 2. ChunkSize уменьшен до 768KB для отзывчивости
/// 3. ParallelChunks увеличен до 4
/// 4. Read() не блокирует если данные есть в кэше
/// </summary>
public sealed class MemoryFirstCachingStream : Stream
{
    #region Constants

    private const int BlockSize = 128 * 1024;
    private const int SmallBlockSize = 64 * 1024;
    private const int MaxRamBlocks = 256;
    private const int ReadAheadBytes = 8 * 1024 * 1024;
    private const int ReadWaitTimeoutMs = 8000;
    private const int MinPreBufferBlocksSmall = 1;
    private const int PreBufferTimeoutMs = 3000;
    private const int LargeFileThreshold = 15 * 1024 * 1024;

    // ★★★ ОПТИМИЗИРОВАННЫЕ ЗНАЧЕНИЯ ★★★
    private const int ChunkSize = 768 * 1024;                    // 768KB (было 1.5MB)
    private const int ParallelChunks = 4;                        // 4 потока (было 3)
    private const int LargeFilePreBufferBytes = 256 * 1024;      // 256KB
    private const int HeadBytes = 512 * 1024;                    // 512KB начало
    private const int TailBytes = 512 * 1024;                    // 512KB конец (для moov/cues)
    private const long MinBytesBeforeReadAheadLimit = 8 * 1024 * 1024;

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

    private readonly ConcurrentDictionary<long, byte[]> _ramBlocks = new();
    private readonly ConcurrentDictionary<long, int> _blockSizes = new();

    private long _downloadPosition;
    private long _downloadedBytes;
    private volatile bool _downloadActive;
    private readonly object _downloadLock = new();
    private int _downloadGeneration;
    private CancellationTokenSource? _downloadCts;
    private readonly Stopwatch _downloadStopwatch = new();
    private DateTime _lastDownloadStart = DateTime.MinValue;

    private volatile bool _vlcStartedReading;
    private volatile bool _headTailDone;  // ★ Флаг что HEAD+TAIL скачаны

    private FileStream? _cacheFile;
    private readonly ReaderWriterLockSlim _fileLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Channel<(long Position, byte[] Data, int Length)> _diskChannel;
    private readonly Task _diskWriterTask;

    private readonly ManualResetEventSlim _dataAvailable = new(false);

    private long _position;

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
        Log.Info($"Opened {trackId}, size: {contentLength / 1024 / 1024}MB, " +
                        $"disk cache: {cachedKB}KB, large: {_isLargeFile}");
    }

    #endregion

    #region === PRE-BUFFER ===

    public async Task<bool> PreBufferAsync(int requestedBytes, CancellationToken ct)
    {
        if (_disposed) return false;

        int minBytes = _isLargeFile
            ? LargeFilePreBufferBytes
            : MinPreBufferBlocksSmall * _effectiveBlockSize;

        // Проверяем disk cache для начала
        if (_diskRanges.IsRangeComplete(0, minBytes))
        {
            // Для больших файлов проверяем также конец (moov atom)
            if (_isLargeFile)
            {
                long tailStart = Math.Max(0, _contentLength - TailBytes);
                if (!_diskRanges.IsRangeComplete(tailStart, _contentLength))
                {
                    // Нужно докачать хвост
                    StartContinuousDownload(0);
                }
                else
                {
                    _headTailDone = true;
                }
            }

            Log.Info($"PreBuffer: disk cache hit ({_diskRanges.DownloadedBytes / 1024}KB)");

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

        StartContinuousDownload(0);

        var sw = Stopwatch.StartNew();
        Log.Info($"PreBuffer: waiting for {minBytes / 1024}KB...");

        try
        {
            while (!ct.IsCancellationRequested && !_disposing)
            {
                // Для больших файлов ждём и HEAD и TAIL
                bool headReady = HasDataInRange(0, minBytes);
                bool tailReady = !_isLargeFile || _headTailDone;

                if (headReady && tailReady)
                {
                    Log.Info($"PreBuffer OK in {sw.ElapsedMilliseconds}ms (head+tail ready)");
                    return true;
                }

                if (sw.ElapsedMilliseconds > PreBufferTimeoutMs)
                {
                    long downloaded = Volatile.Read(ref _downloadedBytes);
                    bool hasEnough = downloaded >= _effectiveBlockSize * 3;
                    Log.Info($"PreBuffer timeout, downloaded: {downloaded / 1024}KB, starting: {hasEnough}");
                    return hasEnough;
                }

                await Task.Delay(30, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return Volatile.Read(ref _downloadedBytes) > 0;
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

        if (!_vlcStartedReading)
        {
            _vlcStartedReading = true;
            Log.Debug($"VLC started reading at position {pos / 1024}KB");
        }

        int totalRead = 0;
        int waitAttempts = 0;
        int maxWaitAttempts = ReadWaitTimeoutMs / 50;  // Быстрее проверяем

        while (totalRead < count && !_disposed && waitAttempts < maxWaitAttempts)
        {
            long currentPos = pos + totalRead;
            int remaining = count - totalRead;

            long blockIndex = currentPos / _effectiveBlockSize;
            int offsetInBlock = (int)(currentPos % _effectiveBlockSize);
            int toRead = Math.Min(remaining, _effectiveBlockSize - offsetInBlock);

            // 1. Try RAM (мгновенно)
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

            // 2. Try Disk (быстро)
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

            // 3. Данных нет - ждём
            waitAttempts++;
            _dataAvailable.Reset();
            _dataAvailable.Wait(50);

            if (waitAttempts % 10 == 0)
            {
                EnsureDownloadRunning(currentPos);
            }
        }

        if (waitAttempts >= maxWaitAttempts && totalRead == 0)
        {
            Log.Warn($"READ TIMEOUT at {pos / 1024}KB, needed block {pos / _effectiveBlockSize}");
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
        Volatile.Write(ref _position, newPosition);

        // Проверяем нужен ли перезапуск download
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

    #region === DOWNLOAD LOGIC ===

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

        Volatile.Write(ref _downloadPosition, fromPosition);
        _downloadStopwatch.Restart();

        _ = Task.Run(async () =>
        {
            try
            {
                if (_isLargeFile)
                {
                    if (fromPosition == 0 && !_headTailDone)
                    {
                        // ★★★ HEAD + TAIL ПАРАЛЛЕЛЬНО ★★★
                        await DownloadHeadAndTailAsync(myGeneration, ct);
                    }

                    // Продолжаем chunked для середины
                    long nextPos = FindNextMissingPosition(HeadBytes);
                    if (nextPos < _contentLength && !ct.IsCancellationRequested)
                    {
                        await ParallelChunkedDownloadAsync(nextPos, myGeneration, ct);
                    }
                }
                else
                {
                    await ContinuousDownloadLoopAsync(fromPosition, myGeneration, ct);
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

    /// <summary>
    /// ★★★ Скачивает HEAD и TAIL параллельно для моментального старта ★★★
    /// </summary>
    private async Task DownloadHeadAndTailAsync(int generation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        long tailStart = Math.Max(HeadBytes, _contentLength - TailBytes);

        Log.Info($"HEAD+TAIL download: 0-{HeadBytes / 1024}KB and {tailStart / 1024}KB-{_contentLength / 1024}KB");

        // Параллельно качаем начало и конец
        var headTask = DownloadRangeAsync(0, HeadBytes, generation, ct);
        var tailTask = DownloadRangeAsync(tailStart, _contentLength, generation, ct);

        try
        {
            await Task.WhenAll(headTask, tailTask);
        }
        catch (OperationCanceledException) { }

        _headTailDone = true;
        _dataAvailable.Set();

        Log.Info($"HEAD+TAIL done in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Скачивает указанный диапазон
    /// </summary>
    private async Task<long> DownloadRangeAsync(long start, long end, int generation, CancellationToken ct)
    {
        if (start >= end) return 0;

        // Пропускаем уже скачанное
        if (_diskRanges.IsRangeComplete(start, end))
        {
            return end - start;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(start, end - 1);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode) return 0;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[_effectiveBlockSize];
            long position = start;
            long downloaded = 0;

            while (position < end && !ct.IsCancellationRequested && !_disposing)
            {
                lock (_downloadLock)
                {
                    if (_downloadGeneration != generation) return downloaded;
                }

                int toRead = (int)Math.Min(_effectiveBlockSize, end - position);
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
                    downloaded += totalRead;

                    Interlocked.Add(ref _downloadedBytes, totalRead);
                }

                position += totalRead;
                if (totalRead < toRead) break;
            }

            return downloaded;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"DownloadRange error: {ex.Message}");
            return 0;
        }
    }

    private async Task ParallelChunkedDownloadAsync(long startPosition, int generation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Log.Info($"PARALLEL CHUNKED from {startPosition / 1024}KB");

        long position = startPosition;
        var activeTasks = new List<Task>();
        var semaphore = new SemaphoreSlim(ParallelChunks, ParallelChunks);
        long totalDownloaded = 0;
        long lastLogBytes = 0;

        // Не качаем зону TAIL (она уже скачана)
        long tailStart = Math.Max(HeadBytes, _contentLength - TailBytes);

        while (position < tailStart && !ct.IsCancellationRequested && !_disposing)
        {
            long readPos = Volatile.Read(ref _position);
            long downloaded = Volatile.Read(ref _downloadedBytes);
            long aheadBytes = position - readPos;

            bool shouldLimit = _vlcStartedReading &&
                               downloaded > MinBytesBeforeReadAheadLimit &&
                               aheadBytes > ReadAheadBytes &&
                               position > readPos;

            if (shouldLimit)
            {
                await Task.Delay(100, ct);
                continue;
            }

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
                await Task.WhenAll(activeTasks);
                return;
            }

            // Пропускаем скачанные диапазоны
            long chunkEnd = Math.Min(position + ChunkSize, tailStart);
            if (_diskRanges.IsRangeComplete(position, chunkEnd))
            {
                position = chunkEnd;
                continue;
            }

            await semaphore.WaitAsync(ct);

            long chunkStart = position;
            position = chunkEnd;

            var task = Task.Run(async () =>
            {
                try
                {
                    long chunkDownloaded = await DownloadChunkAsync(chunkStart, chunkEnd - 1, generation, ct);

                    if (chunkDownloaded > 0)
                    {
                        Interlocked.Add(ref totalDownloaded, chunkDownloaded);

                        long current = Interlocked.Read(ref totalDownloaded);
                        if (current - lastLogBytes >= 3 * 1024 * 1024)
                        {
                            double elapsed = sw.Elapsed.TotalSeconds + 0.001;
                            double speed = current / 1024.0 / elapsed;
                            double progress = DownloadProgress;
                            Log.Info($"{current / 1024}KB @ {speed:F0}KB/s ({progress:F1}%)");
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
            activeTasks.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(activeTasks);

        if (totalDownloaded > 0)
        {
            double elapsed = sw.Elapsed.TotalSeconds + 0.001;
            double speed = totalDownloaded / 1024.0 / elapsed;
            Log.Info($"Chunked done: {totalDownloaded / 1024}KB ({speed:F0}KB/s)");
        }
    }

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
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    retryCount++;
                    await Task.Delay(150 * retryCount, ct);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[_effectiveBlockSize];
                long chunkPosition = chunkStart;
                long downloaded = 0;

                while (chunkPosition <= chunkEnd && !ct.IsCancellationRequested && !_disposing)
                {
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

                        long currentMax = Volatile.Read(ref _downloadPosition);
                        long newPos = chunkPosition + totalRead;
                        while (newPos > currentMax)
                        {
                            long original = Interlocked.CompareExchange(ref _downloadPosition, newPos, currentMax);
                            if (original == currentMax) break;
                            currentMax = Volatile.Read(ref _downloadPosition);
                        }

                        Interlocked.Add(ref _downloadedBytes, totalRead);

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
                await Task.Delay(150 * retryCount, ct);
            }
            catch (HttpRequestException)
            {
                retryCount++;
                await Task.Delay(200 * retryCount, ct);
            }
        }

        return 0;
    }

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
                Log.Info($"Download from {startPosition / 1024}KB");

                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(startPosition + bytesDownloaded, null);

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestCts.CancelAfter(TimeSpan.FromSeconds(15));

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    retryCount++;
                    await Task.Delay(300 * retryCount, ct);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[_effectiveBlockSize];
                long position = startPosition + bytesDownloaded;

                while (!ct.IsCancellationRequested && !_disposing && position < _contentLength)
                {
                    long readPos = Volatile.Read(ref _position);
                    long downloaded = Volatile.Read(ref _downloadedBytes);
                    long aheadBytes = position - readPos;

                    bool shouldLimit = _vlcStartedReading &&
                                       downloaded > MinBytesBeforeReadAheadLimit &&
                                       aheadBytes > ReadAheadBytes &&
                                       position > readPos;

                    if (shouldLimit)
                    {
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
                            Log.Info($"Downloaded {bytesDownloaded / 1024}KB @ {speed:F0}KB/s");
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
                    Log.Info($"Download complete: {bytesDownloaded / 1024}KB ({speed:F0}KB/s)");
                }
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                retryCount++;
                await Task.Delay(200 * retryCount, ct);
            }
            catch (HttpRequestException ex)
            {
                retryCount++;
                Log.Info($"HTTP error: {ex.Message}, retry {retryCount}/{maxRetries}");
                await Task.Delay(300 * retryCount, ct);
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
                if (blockIndex >= currentBlock - 4 && blockIndex <= currentBlock + 32)
                    return false;

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

        Log.Info($"Disposing ({DownloadProgress:F1}% buffered)");

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