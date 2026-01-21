// Services/MemoryFirstCachingStream.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.Channels;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Adaptive Streaming Buffer v13 - Priority-based downloading.
/// Ключевая идея: VLC говорит нам что ему нужно через Read/Seek, мы качаем это приоритетно.
/// </summary>
public sealed class MemoryFirstCachingStream : Stream
{
    #region Constants

    private const int ChunkSize = 128 * 1024;         // 128KB - один HTTP запрос
    private const int MinPreBuffer = 128 * 1024;      // Только один чанк для старта
    private const int ReadAheadChunks = 3;            // Только 3 чанка вперёд (384KB)
    private const int MaxConcurrentDownloads = 3;     // Меньше параллелизма
    private const int ReadTimeoutMs = 5000;           // 5 секунд
    private const int MaxRamChunks = 128;             // 16MB в RAM (достаточно)
    private const int ChunkDownloadTimeoutMs = 20000; // 20 секунд - единый таймаут на скачивание
    private const int ProgressLogIntervalBytes = 6 * 1024 * 1024; // Логировать каждые 6MB

    #endregion

    #region State

    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly RangeMap _diskRanges;
    private readonly int _totalChunks;

    // Sparse storage - только загруженные чанки в памяти
    private readonly ConcurrentDictionary<int, byte[]> _chunks = new();
    private readonly ConcurrentDictionary<int, Task> _pendingDownloads = new();
    private readonly SemaphoreSlim _downloadSemaphore;

    // Приоритетная очередь для скачивания (меньше = выше приоритет)
    private readonly PriorityQueue<int, int> _downloadQueue = new();
    private readonly object _queueLock = new();
    private readonly HashSet<int> _queuedChunks = new();

    // Disk I/O
    private FileStream? _cacheFile;
    private readonly ReaderWriterLockSlim _fileLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Channel<(long Position, byte[] Data, int Length)> _diskChannel;
    private readonly Task _diskWriterTask;
    private Task? _downloadLoop;

    // Состояние
    private long _position;
    private long _bytesDownloaded;
    private volatile bool _downloadComplete;
    private CancellationTokenSource _cts;
    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;
    private volatile bool _disposing;

    // Сигнализация о новых данных
    private readonly ManualResetEventSlim _dataAvailable = new(false);

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
            long bytes = Volatile.Read(ref _bytesDownloaded);
            return Math.Min((double)bytes / _contentLength * 100, 100);
        }
    }

    public bool IsFullyDownloaded => _downloadComplete;

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
        _cts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);
        _totalChunks = (int)((_contentLength + ChunkSize - 1) / ChunkSize);

        // Загружаем метаданные или создаем новые, если их нет
        var meta = cacheManager.LoadOrCreateMetadata(trackId, url, contentLength);
        // Десериализуем карту скачанных диапазонов из метаданных
        _diskRanges = RangeMap.Deserialize(meta.RangesJson);

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
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

        _diskWriterTask = Task.Run(DiskWriterLoopAsync);

        // Подсчитываем уже скачанные байты из кэша
        _bytesDownloaded = _diskRanges.DownloadedBytes;

        long cachedKB = _diskRanges.DownloadedBytes / 1024;
        Log.Info($"Opened {trackId}, size: {contentLength / 1024 / 1024}MB, disk cache: {cachedKB}KB");
    }

    #endregion

    #region === PRE-BUFFER ===

    public async Task<bool> PreBufferAsync(CancellationToken ct)
    {
        if (_disposed) return false;

        // Сразу запускаем фоновый цикл обработки очереди
        StartDownloadLoop();

        // Если первый чанк уже есть на диске - отлично, ничего не ждем
        if (HasChunk(0))
        {
            Log.Info("PreBuffer: first chunk found in disk cache.");
            return true;
        }

        Log.Info($"PreBuffer: downloading first chunk ({ChunkSize / 1024}KB)...");
        var sw = Stopwatch.StartNew();

        // Ставим задачу на скачивание первого чанка
        EnqueueUrgent(0);

        // Теперь ждем, пока он не скачается
        while (!HasChunk(0))
        {
            // Ждем сигнала о поступлении НОВЫХ данных
            if (!_dataAvailable.Wait(1000, ct)) // Ждем до 1 секунды
            {
                // Если сигнала не было, проверяем общий таймаут
                if (sw.ElapsedMilliseconds > ChunkDownloadTimeoutMs)
                {
                    Log.Error($"PreBuffer timed out after {sw.ElapsedMilliseconds}ms waiting for chunk 0.");
                    return false;
                }
            }
            // Сбрасываем сигнал, чтобы ждать следующего поступления данных
            _dataAvailable.Reset();
        }

        sw.Stop();
        Log.Info($"PreBuffer OK in {sw.ElapsedMilliseconds}ms");
        return true;
    }

    private void StartDownloadLoop()
    {
        if (_downloadLoop != null) return;
        _downloadLoop = Task.Run(() => DownloadLoopAsync(_cts.Token), _cts.Token);
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

        int chunkIndex = (int)(pos / ChunkSize);
        int offsetInChunk = (int)(pos % ChunkSize);
        int toRead = Math.Min(count, ChunkSize - offsetInChunk);

        var sw = Stopwatch.StartNew();

        // Цикл ожидания: ждем, пока нужный чанк не появится
        while (!HasChunk(chunkIndex))
        {
            // 1. Ставим чанк в приоритетную очередь
            EnqueueUrgent(chunkIndex);

            // 2. Ждем сигнала о том, что какой-то чанк скачался
            if (!_dataAvailable.Wait(1000, _disposeCts.Token)) // Ждем до 1 секунды
            {
                // 3. Если долго нет данных, выходим по таймауту, чтобы не вешать VLC
                if (sw.ElapsedMilliseconds > ReadTimeoutMs)
                {
                    Log.Error($"READ TIMEOUT while waiting for chunk {chunkIndex} at pos {pos}");
                    return 0;
                }
            }
            _dataAvailable.Reset();
        }

        // К этому моменту чанк ГАРАНТИРОВАННО есть либо в RAM, либо на диске
        int bytesRead = TryReadChunk(chunkIndex, offsetInChunk, buffer, offset, toRead);

        if (bytesRead > 0)
        {
            Interlocked.Add(ref _position, bytesRead);
            EnqueueReadAhead(chunkIndex); // Планируем опережающую загрузку
        }

        return bytesRead;
    }

    private int TryReadChunk(int chunkIndex, int offsetInChunk, byte[] buffer, int bufferOffset, int count)
    {
        // 1. Пробуем RAM
        if (_chunks.TryGetValue(chunkIndex, out var chunk))
        {
            int available = Math.Min(count, chunk.Length - offsetInChunk);
            if (available > 0)
            {
                Buffer.BlockCopy(chunk, offsetInChunk, buffer, bufferOffset, available);
                return available;
            }
        }

        // 2. Пробуем диск
        long chunkStart = (long)chunkIndex * ChunkSize;
        long chunkEnd = Math.Min(chunkStart + ChunkSize, _contentLength);

        if (_diskRanges.IsRangeComplete(chunkStart, chunkEnd))
        {
            return ReadFromDisk(chunkStart + offsetInChunk, buffer, bufferOffset, count);
        }

        return 0;
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

        int oldChunk = (int)(oldPosition / ChunkSize);
        int newChunk = (int)(newPosition / ChunkSize);

        if (Math.Abs(newChunk - oldChunk) > 4)
        {
            Log.Debug($"VLC Seek: {oldPosition / 1024}KB → {newPosition / 1024}KB");
        }

        // Приоритетно качаем новый регион
        EnqueueUrgent(newChunk);

        Volatile.Write(ref _position, newPosition);
        return newPosition;
    }

    #endregion

    #region === PRIORITY QUEUE ===

    /// <summary>
    /// Срочная загрузка - VLC запросил эти данные прямо сейчас
    /// </summary>
    private void EnqueueUrgent(int chunkIndex)
    {
        lock (_queueLock)
        {
            // Добавляем текущий чанк с НАИВЫСШИМ приоритетом
            if (!HasChunk(chunkIndex) && !_pendingDownloads.ContainsKey(chunkIndex))
            {
                if (_queuedChunks.Add(chunkIndex))
                {
                    _downloadQueue.Enqueue(chunkIndex, 0); // Приоритет 0 = максимальный
                }
            }

            // Следующие 4 чанка тоже приоритетны
            for (int i = 1; i <= 4; i++)
            {
                int chunk = chunkIndex + i;
                if (chunk >= _totalChunks) break;
                if (HasChunk(chunk)) continue;
                if (_pendingDownloads.ContainsKey(chunk)) continue;

                if (_queuedChunks.Add(chunk))
                {
                    _downloadQueue.Enqueue(chunk, i); // Приоритет 1-4
                }
            }
        }
    }

    /// <summary>
    /// Опережающая загрузка - качаем вперёд от текущей позиции
    /// </summary>
    private void EnqueueReadAhead(int currentChunk)
    {
        lock (_queueLock)
        {
            for (int i = 1; i <= ReadAheadChunks; i++)
            {
                int chunk = currentChunk + i;
                if (chunk >= _totalChunks) break;
                if (HasChunk(chunk)) continue;
                if (_pendingDownloads.ContainsKey(chunk)) continue;

                if (_queuedChunks.Add(chunk))
                {
                    _downloadQueue.Enqueue(chunk, 100 + i); // Lower priority
                }
            }
        }
    }

    private bool HasChunk(int chunkIndex)
    {
        if (_chunks.ContainsKey(chunkIndex)) return true;

        long chunkStart = (long)chunkIndex * ChunkSize;
        long chunkEnd = Math.Min(chunkStart + ChunkSize, _contentLength);
        return _diskRanges.IsRangeComplete(chunkStart, chunkEnd);
    }

    #endregion

    #region === DOWNLOAD LOOP ===
    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastLogBytes = 0;

        while (!ct.IsCancellationRequested && !_disposing)
        {
            int chunkToDownload = -1;

            // 1. Всегда берем задачу из приоритетной очереди
            lock (_queueLock)
            {
                while (_downloadQueue.Count > 0)
                {
                    var candidate = _downloadQueue.Dequeue();
                    _queuedChunks.Remove(candidate);

                    if (!HasChunk(candidate) && !_pendingDownloads.ContainsKey(candidate))
                    {
                        chunkToDownload = candidate;
                        break;
                    }
                }
            }

            // 2. Если в очереди нет задач, просто ждем
            if (chunkToDownload < 0)
            {
                if (IsAllDownloaded())
                {
                    _downloadComplete = true;
                    // Не логируем для маленьких файлов, которые скачались мгновенно
                    if (sw.Elapsed.TotalSeconds > 1)
                        Log.Info($"Download complete in {sw.Elapsed.TotalSeconds:F1}s");
                    break; // Выходим из цикла
                }

                // Ждем появления новых запросов на скачивание
                try
                {
                    await Task.Delay(100, ct);
                }
                catch (OperationCanceledException)
                {
                    break; // Выходим, если стрим закрывается
                }
                continue;
            }

            // 4. Скачиваем чанк (с ограничением параллелизма)
            await _downloadSemaphore.WaitAsync(ct);
            _ = DownloadChunkWithReleaseAsync(chunkToDownload, ct);

            // 5. Логирование прогресса
            long currentBytes = Volatile.Read(ref _bytesDownloaded);
            if (currentBytes - lastLogBytes >= ProgressLogIntervalBytes)
            {
                lastLogBytes = currentBytes;
                double progress = (double)currentBytes / _contentLength * 100;
                // Скорость считаем по времени с начала загрузки
                double speed = currentBytes / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                Log.Info($"Download: {currentBytes / 1024}KB @ {speed:F0}KB/s ({progress:F1}%)");
            }
        }
    }

    private bool IsAllDownloaded()
    {
        for (int i = 0; i < _totalChunks; i++)
        {
            if (!HasChunk(i)) return false;
        }
        return true;
    }

    private async Task DownloadChunkWithReleaseAsync(int index, CancellationToken ct)
    {
        try
        {
            await DownloadChunkAsync(index, ct);
        }
        catch (OperationCanceledException)
        {
            // Это ожидаемое исключение при смене трека, игнорируем его
        }
        catch (Exception ex)
        {
            // Логируем только неожиданные ошибки
            Log.Warn($"[MemoryFirstCachingStream] Chunk download failed ({index}): {ex.Message}");
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private async Task DownloadChunkAsync(int index, CancellationToken ct)
    {
        if (HasChunk(index)) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDownloads.TryAdd(index, tcs.Task))
        {
            // Кто-то уже качает - ждём
            if (_pendingDownloads.TryGetValue(index, out var existing))
            {
                try { await existing.WaitAsync(ct); } catch { }
            }
            return;
        }

        try
        {
            long start = (long)index * ChunkSize;
            long end = Math.Min(start + ChunkSize - 1, _contentLength - 1);
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(start, end);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(ChunkDownloadTimeoutMs));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadAsByteArrayAsync(cts.Token);

            if (!_chunks.ContainsKey(index))
            {
                _chunks[index] = data;
                Interlocked.Add(ref _bytesDownloaded, data.Length);
                _dataAvailable.Set();

                // Записываем на диск
                if (!_disposing)
                {
                    _diskChannel.Writer.TryWrite((start, data, data.Length));
                }

                // Очистка RAM если слишком много чанков
                if (_chunks.Count > MaxRamChunks)
                {
                    TrimRamCache();
                }
            }

            tcs.SetResult();
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            Log.Warn($"Chunk {index} failed: {ex.Message}");
            tcs.TrySetException(ex);
        }
        finally
        {
            _pendingDownloads.TryRemove(index, out _);
        }
    }

    private void TrimRamCache()
    {
        if (_chunks.Count <= MaxRamChunks / 2) return;

        long currentPos = Volatile.Read(ref _position);
        int currentChunk = (int)(currentPos / ChunkSize);

        var toRemove = _chunks.Keys
            .Where(idx =>
            {
                // Не удаляем близкие к текущей позиции
                if (idx >= currentChunk - 4 && idx <= currentChunk + ReadAheadChunks * 2)
                    return false;

                // Удаляем только если есть на диске
                long chunkStart = (long)idx * ChunkSize;
                long chunkEnd = Math.Min(chunkStart + ChunkSize, _contentLength);
                return _diskRanges.IsRangeComplete(chunkStart, chunkEnd);
            })
            .OrderByDescending(idx => Math.Abs(idx - currentChunk))
            .Take(_chunks.Count / 3)
            .ToList();

        foreach (var idx in toRemove)
        {
            _chunks.TryRemove(idx, out _);
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

        // Финальное сохранение
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
            try { _cts.Cancel(); } catch { }
            try { _dataAvailable.Set(); } catch { }
            try { _diskChannel.Writer.TryComplete(); } catch { }
            try { _diskWriterTask.Wait(1000); } catch { }
            try { _downloadLoop?.Wait(500); } catch { }
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

            _chunks.Clear();
            _pendingDownloads.Clear();

            try { _dataAvailable.Dispose(); } catch { }
            try { _fileLock.Dispose(); } catch { }
            try { _disposeCts.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _downloadSemaphore.Dispose(); } catch { }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    #endregion
}