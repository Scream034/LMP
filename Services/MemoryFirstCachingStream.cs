using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.Channels;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Memory-First Caching Stream v5 - Fixed for large files.
/// 
/// ИЗМЕНЕНИЯ:
/// 1. Использование disk cache при повторном воспроизведении
/// 2. Меньше логов для seek
/// 3. Быстрый старт с минимальным буфером
/// 4. HttpClient с KeepAlive для больших файлов
/// </summary>
public sealed class MemoryFirstCachingStream : Stream
{
    #region Constants

    private const int BlockSize = 64 * 1024;           // 64KB блоки
    private const int MinPreBufferBlocks = 4;          // 256KB минимум для старта
    private const int MaxRamBlocks = 512;              // 32MB в RAM
    private const int ReadWaitTimeoutMs = 3000;        // 3 сек макс ожидание в Read
    private const int PreBufferTimeoutMs = 3000;       // 3 сек на prebuffer (уменьшено!)

    #endregion

    #region State

    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly RangeMap _diskRanges;

    // === RAM BUFFER ===
    private readonly ConcurrentDictionary<long, byte[]> _ramBlocks = new();
    private readonly ConcurrentDictionary<long, int> _blockSizes = new();

    // === DOWNLOAD STATE ===
    private long _downloadPosition;
    private long _downloadedBytes;
    private volatile bool _downloadActive;
    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;
    private readonly Stopwatch _downloadStopwatch = new();

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

            // Сначала disk, потом RAM
            long diskBytes = _diskRanges.DownloadedBytes;
            long ramBytes = Volatile.Read(ref _downloadedBytes);

            // Берём максимум, но не больше contentLength
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
        Debug.WriteLine($"[MemoryFirst] Opened {trackId}, size: {contentLength / 1024 / 1024}MB, disk cache: {cachedKB}KB");
    }

    #endregion

    #region === PRE-BUFFER ===

    public async Task<bool> PreBufferAsync(int requestedBytes, CancellationToken ct)
    {
        if (_disposed) return false;

        int minBytes = MinPreBufferBlocks * BlockSize;  // 256KB

        // Проверяем disk cache - если есть, сразу готовы!
        if (_diskRanges.IsRangeComplete(0, minBytes))
        {
            Debug.WriteLine($"[MemoryFirst] PreBuffer: disk cache hit ({_diskRanges.DownloadedBytes / 1024}KB)");

            // Если не полностью скачан - запускаем фоновую докачку
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

        // Запускаем download с позиции 0
        StartContinuousDownload(0);

        var sw = Stopwatch.StartNew();

        Debug.WriteLine($"[MemoryFirst] PreBuffer: waiting for {minBytes / 1024}KB...");

        try
        {
            while (!ct.IsCancellationRequested && !_disposing)
            {
                // Проверяем RAM или Disk
                if (HasDataInRange(0, minBytes))
                {
                    Debug.WriteLine($"[MemoryFirst] PreBuffer OK in {sw.ElapsedMilliseconds}ms");
                    return true;
                }

                // Таймаут - но пробуем с тем что есть
                if (sw.ElapsedMilliseconds > PreBufferTimeoutMs)
                {
                    long downloaded = Volatile.Read(ref _downloadedBytes);
                    int ramBlocks = _ramBlocks.Count;
                    bool hasAny = downloaded > 0 || ramBlocks > 0;

                    Debug.WriteLine($"[MemoryFirst] PreBuffer timeout ({sw.ElapsedMilliseconds}ms), downloaded: {downloaded / 1024}KB, blocks: {ramBlocks}");
                    return hasAny;
                }

                await Task.Delay(30, ct);  // Быстрее проверяем
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
        // Check RAM blocks
        long startBlock = start / BlockSize;
        long endBlock = (start + count - 1) / BlockSize;

        for (long block = startBlock; block <= endBlock; block++)
        {
            long blockStart = block * BlockSize;
            long blockEnd = Math.Min(blockStart + BlockSize, _contentLength);

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
        const int maxWaitAttempts = 30;

        while (totalRead < count && !_disposed && waitAttempts < maxWaitAttempts)
        {
            long currentPos = pos + totalRead;
            int remaining = count - totalRead;

            long blockIndex = currentPos / BlockSize;
            int offsetInBlock = (int)(currentPos % BlockSize);
            int toRead = Math.Min(remaining, BlockSize - offsetInBlock);

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
            long blockStart = blockIndex * BlockSize;
            long blockEnd = Math.Min(blockStart + BlockSize, _contentLength);
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

            EnsureDownloadRunning(currentPos);
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
        if (_downloadActive) return;

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

        // Логируем только значимые seek'и (не в ту же позицию и не слишком часто)
        if (Math.Abs(newPosition - _lastLoggedSeekPos) > BlockSize * 4)
        {
            Debug.WriteLine($"[MemoryFirst] Seek {oldPosition / 1024}KB -> {newPosition / 1024}KB");
            _lastLoggedSeekPos = newPosition;
        }

        // Проверяем, нужно ли перезапустить download
        if (!_downloadActive || !HasDataInRange(newPosition, BlockSize * 4))
        {
            long nextMissing = FindNextMissingPosition(newPosition);
            if (nextMissing < _contentLength && nextMissing < newPosition + BlockSize * 8)
            {
                StartContinuousDownload(nextMissing);
            }
        }

        return newPosition;
    }

    #endregion

    #region === CONTINUOUS DOWNLOAD ===

    private void StartContinuousDownload(long fromPosition)
    {
        if (_disposing) return;

        // Don't restart if already downloading what we need
        if (_downloadActive)
        {
            long currentDownloadPos = Volatile.Read(ref _downloadPosition);
            long currentReadPos = Volatile.Read(ref _position);

            // Download is ahead of read - don't restart
            if (currentDownloadPos > currentReadPos && currentDownloadPos < currentReadPos + MaxRamBlocks * BlockSize)
            {
                return;
            }
        }

        // Cancel previous
        var oldCts = _downloadCts;
        _downloadCts = new CancellationTokenSource();
        try { oldCts?.Cancel(); } catch { }

        var ct = CancellationTokenSource.CreateLinkedTokenSource(
            _downloadCts.Token, _disposeCts.Token).Token;

        // Find actual start position (skip cached)
        long startPos = FindNextMissingPosition(fromPosition);

        if (startPos >= _contentLength)
        {
            Debug.WriteLine("[MemoryFirst] All data already cached");
            return;
        }

        Volatile.Write(ref _downloadPosition, startPos);
        _downloadStopwatch.Restart();

        _downloadTask = Task.Run(async () =>
        {
            await ContinuousDownloadLoopAsync(startPos, ct);
        });
    }

    private async Task ContinuousDownloadLoopAsync(long startPosition, CancellationToken ct)
    {
        _downloadActive = true;
        long bytesDownloaded = 0;
        long lastLogBytes = 0;

        try
        {
            Debug.WriteLine($"[MemoryFirst] Download starting from {startPosition / 1024}KB");

            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(startPosition, null);
            request.Headers.ConnectionClose = false;  // Keep-alive!

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[MemoryFirst] HTTP {response.StatusCode}");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[BlockSize];
            long position = startPosition;

            while (!ct.IsCancellationRequested && !_disposing && position < _contentLength)
            {
                int toRead = (int)Math.Min(BlockSize, _contentLength - position);
                int totalRead = 0;

                while (totalRead < toRead && !ct.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(totalRead, toRead - totalRead), ct);
                    if (read == 0) break;
                    totalRead += read;
                }

                if (totalRead > 0)
                {
                    long blockIndex = position / BlockSize;
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

                    // Progress log every 1MB
                    if (bytesDownloaded - lastLogBytes >= 1024 * 1024)
                    {
                        double elapsed = _downloadStopwatch.Elapsed.TotalSeconds + 0.001;
                        double speed = bytesDownloaded / 1024.0 / elapsed;
                        Debug.WriteLine($"[MemoryFirst] Downloaded {bytesDownloaded / 1024}KB @ {speed:F0}KB/s");
                        lastLogBytes = bytesDownloaded;
                    }

                    // Trim RAM
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
                Debug.WriteLine($"[MemoryFirst] Download complete: {bytesDownloaded / 1024}KB in {(int)(elapsed * 1000)}ms ({speed:F0}KB/s)");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[MemoryFirst] HTTP error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryFirst] Download error: {ex.Message}");
        }
        finally
        {
            _downloadActive = false;
            _dataAvailable.Set();
        }
    }

    private long FindNextMissingPosition(long startPosition)
    {
        long pos = (startPosition / BlockSize) * BlockSize;

        while (pos < _contentLength)
        {
            long blockEnd = Math.Min(pos + BlockSize, _contentLength);

            if (_ramBlocks.ContainsKey(pos / BlockSize))
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
        long currentBlock = currentPos / BlockSize;

        var toRemove = _ramBlocks.Keys
            .Where(blockIndex =>
            {
                if (blockIndex >= currentBlock - 4 && blockIndex <= currentBlock + MaxRamBlocks / 4)
                    return false;

                long blockPos = blockIndex * BlockSize;
                return _diskRanges.IsRangeComplete(blockPos, Math.Min(blockPos + BlockSize, _contentLength));
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

        // Final flush and save
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

        Debug.WriteLine($"[MemoryFirst] Disposing ({DownloadProgress:F1}% cached)");

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