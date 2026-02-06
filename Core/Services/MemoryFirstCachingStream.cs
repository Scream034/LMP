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
    #region Configuration

    private static class Config
    {
        public const int MaxOpenRetries = 10;
        public const int RetryDelayBaseMs = 100;
        public const int FlushTimeoutMs = 1000;
        public const int SemaphoreTimeoutMs = 2000;
        public const int SaveThresholdBytes = 128 * 1024;
        public const int ChannelCapacity = 64;
        public const int MaxRetries = 2;
        public const int RetryDelayMs = 500;
        public const int PriorityUrgent = 0;
        public const int PriorityReadAhead = 50;
        public const int TailChunksForCues = 3;
        public const int ChunkWaitMs = 300;
        public const int PauseCheckIntervalMs = 500;
        public const int IdleLoopMs = 100;
        public const int PreBufferCheckMs = 150;
        public const int InitialBufferSeconds = 15;
        public const int ReadAheadSeconds = 15;
        public const int SeekBufferSeconds = 10;
        public const int ExtendIntervalMs = 500;
        public const int ChunksToKeepBehind = 2;
        public const int ChunksToKeepMultiplier = 2;

        // Таймауты для ожидания чанков
        public const int CriticalWaitMs = 30000;    // 30 сек
        public const int NonCriticalWaitMs = 60000; // 60 сек

        // НОВОЕ: Для срочных чанков - больше параллелизма
        public const int UrgentBoostMultiplier = 2;
    }

    #endregion

    #region Fields

    private readonly int _chunkSize;
    private readonly int _readAheadChunks;
    private readonly int _maxConcurrentDownloads;
    private readonly int _maxRamChunks;
    private readonly int _downloadTimeoutMs;
    private readonly int _estimatedBitrate;
    private readonly long _totalDurationMs;
    private readonly Func<long>? _getPlaybackTimeMs;

    private readonly string _cacheId;
    private readonly string _originalTrackId;
    private string _url;
    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly int _totalChunks;
    private readonly int _tailStartChunk;

    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    private readonly ConcurrentDictionary<int, byte[]> _chunks = new();
    private readonly ConcurrentDictionary<int, Task> _pendingDownloads = new();
    private readonly RangeMap _diskRanges;

    private long _position;
    private long _bytesDownloaded;
    private volatile bool _downloadComplete;
    private volatile bool _disposed;
    private volatile bool _disposing;
    private volatile bool _isPaused;
    private volatile bool _downloadFullTrack;
    private volatile int _targetBufferChunk = -1;
    private volatile int _lastPlaybackChunk;
    private volatile int _prevExtendPlayback = -1; // Для BufferExtender
    private volatile bool _hasUrgentWork;

    // Добавляем второй семафор для срочных загрузок
    private readonly SemaphoreSlim _urgentDownloadSemaphore;
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly SemaphoreSlim _fileSemaphore = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Lock _queueLock = new();
    private readonly Lock _extendLock = new();

    private readonly PriorityQueue<int, int> _downloadQueue = new();
    private readonly HashSet<int> _queuedChunks = [];

    private readonly Channel<(long Pos, byte[] Data, int Len)> _diskChannel;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationTokenSource _downloadCts;

    private readonly Task _diskWriterTask;
    private readonly Task _bufferExtenderTask;
    private Task? _downloadLoop;
    private FileStream? _cacheFile;

    private int _cacheHits;
    private int _cacheMisses;
    private int _vlcRequestsIgnored;

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

    public double DownloadProgress => _contentLength <= 0 ? 0
        : Math.Min((double)Volatile.Read(ref _bytesDownloaded) / _contentLength * 100, 100);

    public bool IsFullyDownloaded => _downloadComplete;

    #endregion

    #region Constructor

    public MemoryFirstCachingStream(
        string cacheId, string url, long contentLength, HttpClient http,
        StreamCacheManager cacheManager, StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        string? originalTrackId = null,
        Func<long>? getPlaybackTimeMs = null,
        long totalDurationMs = 0)
    {
        _cacheId = cacheId;
        _originalTrackId = originalTrackId ?? cacheId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;
        _getPlaybackTimeMs = getPlaybackTimeMs;
        _totalDurationMs = totalDurationMs > 0 ? totalDurationMs : 1;

        _chunkSize = config.ChunkSize;
        _readAheadChunks = config.ReadAheadChunks;
        _maxConcurrentDownloads = config.MaxConcurrentDownloads;
        _maxRamChunks = Math.Max(config.MaxRamChunks, 10);
        _downloadTimeoutMs = config.DownloadTimeoutMs;
        _downloadFullTrack = config.DownloadFullTrack;

        _cachePath = StreamCacheManager.GetCachePath(_cacheId);
        _downloadCts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        // НОВОЕ: Для срочных чанков - в 2 раза больше слотов
        _urgentDownloadSemaphore = new SemaphoreSlim(
            Math.Max(_maxConcurrentDownloads * Config.UrgentBoostMultiplier, 4));
        _totalChunks = (int)((_contentLength + _chunkSize - 1) / _chunkSize);
        _tailStartChunk = Math.Max(0, _totalChunks - Config.TailChunksForCues);

        var meta = StreamCacheManager.TryGetMetadata(cacheId);
        _estimatedBitrate = meta?.Bitrate ?? 128;

        Log.Debug($"[Buffer] Init: id={_cacheId}, chunks={_totalChunks}, " +
                  $"duration={totalDurationMs}ms, tail={_tailStartChunk}-{_totalChunks - 1}");

        meta ??= StreamCacheManager.LoadOrCreateMetadata(cacheId, url, contentLength);
        _diskRanges = RangeMap.Deserialize(meta.RangesJson);
        _bytesDownloaded = _diskRanges.DownloadedBytes;

        if (_diskRanges.IsFullyDownloaded(_contentLength))
        {
            _downloadComplete = true;
            _cacheManager.TriggerCacheCompleted(_cacheId, _originalTrackId);
            Log.Debug($"[Buffer] Already fully cached");
        }

        _cacheFile = OpenCacheFile(_cachePath);
        if (_cacheFile != null && _cacheFile.Length < _contentLength)
            _cacheFile.SetLength(_contentLength);

        _diskChannel = Channel.CreateBounded<(long, byte[], int)>(
            new BoundedChannelOptions(Config.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

        _diskWriterTask = Task.Run(DiskWriterLoopAsync);
        _bufferExtenderTask = Task.Run(BufferExtenderLoopAsync);

        MemoryDiagnostics.TrackInstance("Stream.Active");
        MemoryDiagnostics.TrackBytes("Stream.TotalSize", _contentLength);
    }

    #endregion

    #region Chunk Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasChunk(int idx)
    {
        if (_chunks.ContainsKey(idx)) return true;
        long start = (long)idx * _chunkSize;
        long end = Math.Min(start + _chunkSize, _contentLength);
        return _diskRanges.IsRangeComplete(start, end);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CanEnqueue(int idx) =>
        !HasChunk(idx) && !_pendingDownloads.ContainsKey(idx) && !_queuedChunks.Contains(idx);

    /// <summary>Проверяет нужно ли качать чанк (между playback и target, или tail)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInDownloadRange(int idx)
    {
        // Tail чанки - всегда
        if (idx >= _tailStartChunk) return true;

        // Между playback и target
        return idx >= _lastPlaybackChunk && idx <= _targetBufferChunk;
    }

    private int TimeToChunk(long timeMs)
    {
        if (_totalDurationMs <= 0 || _contentLength <= 0) return 0;
        if (timeMs <= 0) return 0;
        if (timeMs >= _totalDurationMs) return _totalChunks - 1;
        return Math.Clamp((int)((double)timeMs / _totalDurationMs * _contentLength / _chunkSize), 0, _totalChunks - 1);
    }

    private int SecondsToChunks(int seconds) =>
        _estimatedBitrate <= 0
            ? Math.Max(1, seconds / 2)
            : Math.Max(1, (int)Math.Ceiling(_estimatedBitrate * 1000.0 / 8.0 * seconds / _chunkSize));

    private int GetPlaybackChunk()
    {
        if (_getPlaybackTimeMs == null || _totalDurationMs <= 0)
            return _lastPlaybackChunk;

        long currentMs = _getPlaybackTimeMs();
        if (currentMs <= 0) return _lastPlaybackChunk;

        int chunk = Math.Clamp((int)((double)currentMs / _totalDurationMs * _contentLength / _chunkSize), 0, _totalChunks - 1);

        if (chunk > _lastPlaybackChunk)
            _lastPlaybackChunk = chunk;

        return _lastPlaybackChunk;
    }

    private int CountCachedInRange(int from, int to)
    {
        int count = 0;
        for (int i = from; i <= to && i < _totalChunks; i++)
            if (HasChunk(i)) count++;
        return count;
    }

    private bool AreTailChunksCached()
    {
        for (int i = _tailStartChunk; i < _totalChunks; i++)
            if (!HasChunk(i)) return false;
        return true;
    }

    #endregion

    #region Public API

    public async ValueTask<bool> PreBufferAsync(CancellationToken ct)
    {
        if (_disposed || _disposing) return false;

        var sw = Stopwatch.StartNew();
        Log.Debug($"[Buffer] PreBuffer start");

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token, _disposeCts.Token);
            var token = linked.Token;

            _downloadLoop ??= Task.Run(() => DownloadLoopAsync(token), token);

            // Минимальный начальный target - только для старта
            UpdateTarget(0, 5, "Initial"); // 5 секунд вместо 15
            ScheduleChunks(0);

            if (HasChunk(0))
            {
                Log.Debug($"[Buffer] Chunk 0 ready, target={_targetBufferChunk}");
                return true;
            }

            while (!HasChunk(0))
            {
                if (token.IsCancellationRequested) return false;
                if (!_dataAvailable.Wait(Config.PreBufferCheckMs, token))
                    if (sw.ElapsedMilliseconds > _downloadTimeoutMs) return false;
                if (!HasChunk(0)) _dataAvailable.Reset();
            }

            Log.Debug($"[Buffer] PreBuffer complete in {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    public void NotifySeek(long positionMs)
    {
        Log.Debug($"[Buffer] NotifySeek: {positionMs}ms (duration={_totalDurationMs}ms)");

        int newChunk = TimeToChunk(positionMs);
        Log.Debug($"[Buffer] NotifySeek → chunk {newChunk}");

        _lastPlaybackChunk = newChunk;
        _prevExtendPlayback = newChunk;

        // Устанавливаем target НАЧИНАЯ с новой позиции
        lock (_extendLock)
        {
            // Сбрасываем target если seekнули ВПЕРЁД за него
            if (newChunk > _targetBufferChunk)
            {
                _targetBufferChunk = newChunk - 1; // UpdateTarget расширит
            }
        }

        int newTarget = UpdateTarget(newChunk, Config.SeekBufferSeconds, "Seek");
        int rangeSize = Math.Max(1, newTarget - newChunk + 1);
        int alreadyCached = CountCachedInRange(newChunk, newTarget);
        bool allCached = alreadyCached >= rangeSize && AreTailChunksCached();

        if (allCached)
        {
            _hasUrgentWork = false;
            Log.Debug($"[Buffer] NotifySeek done: all cached ({alreadyCached}/{rangeSize}), target={_targetBufferChunk}");
            return;
        }

        // Очищаем очередь
        lock (_queueLock)
        {
            _downloadQueue.Clear();
            _queuedChunks.Clear();
        }

        int queued = ScheduleChunks(newChunk);
        _hasUrgentWork = queued > 0;

        Log.Debug($"[Buffer] NotifySeek done: queue={queued}, target={_targetBufferChunk}, " +
                  $"pending={_pendingDownloads.Count}, cached={alreadyCached}/{rangeSize}, hasUrgent={_hasUrgentWork}");
    }

    public void CancelPendingReads()
    {
        Log.Debug($"[Buffer] CancelPendingReads");
        try { _disposeCts.Cancel(); } catch { }
    }

    public void NotifyPaused(bool paused)
    {
        if (_isPaused == paused) return;
        _isPaused = paused;
        Log.Debug($"[Buffer] Pause={paused}, playback={_lastPlaybackChunk}, target={_targetBufferChunk}");
    }

    public void EnableFullDownload()
    {
        if (_downloadFullTrack) return;
        _downloadFullTrack = true;
        _targetBufferChunk = _totalChunks - 1;
        ScheduleChunks(0);
        Log.Debug($"[Buffer] Full download enabled");
    }

    public void ReleaseRamBuffers()
    {
        if (_disposed) return;

        long freed = 0;
        foreach (var kvp in _chunks)
        {
            int idx = kvp.Key;
            long start = (long)idx * _chunkSize;
            long end = Math.Min(start + _chunkSize, _contentLength);

            if (_diskRanges.IsRangeComplete(start, end) && _chunks.TryRemove(idx, out var buffer))
            {
                freed += buffer.Length;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (freed > 0)
            MemoryDiagnostics.UntrackBytes("Stream.RAMChunks", freed);
    }

    #endregion

    #region Scheduling

    private int UpdateTarget(int fromChunk, int seconds, string reason)
    {
        if (_downloadFullTrack || _downloadComplete)
            return _targetBufferChunk;

        lock (_extendLock)
        {
            int newTarget = Math.Min(fromChunk + SecondsToChunks(seconds), _tailStartChunk - 1);

            if (newTarget > _targetBufferChunk)
            {
                Log.Debug($"[Buffer] {reason}: from={fromChunk}, target {_targetBufferChunk} → {newTarget}");
                _targetBufferChunk = newTarget;
            }

            return _targetBufferChunk;
        }
    }

    private int ScheduleChunks(int fromChunk)
    {
        int added = 0;

        lock (_queueLock)
        {
            // Tail-чанки (высший приоритет)
            for (int i = _tailStartChunk; i < _totalChunks; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                {
                    _downloadQueue.Enqueue(i, Config.PriorityUrgent + 1);
                    added++;
                }
            }

            // Чанки от fromChunk до target
            int limit = Math.Min(_targetBufferChunk + 1, _tailStartChunk);

            // ОПТИМИЗАЦИЯ: Первые 3 чанка - максимальный приоритет
            int urgentCount = Math.Min(3, limit - fromChunk);

            for (int i = fromChunk; i < limit; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                {
                    int priority;
                    if (i < fromChunk + urgentCount)
                    {
                        // Первые чанки - срочные
                        priority = Config.PriorityUrgent + (i - fromChunk);
                    }
                    else
                    {
                        // Остальные - обычные
                        priority = Config.PriorityReadAhead + (i - fromChunk);
                    }

                    _downloadQueue.Enqueue(i, priority);
                    added++;
                }
            }
        }

        return added;
    }

    #endregion

    #region Buffer Extender

    private async Task BufferExtenderLoopAsync()
    {
        await Task.Delay(300, _disposeCts.Token).ConfigureAwait(false);

        try
        {
            while (!_disposeCts.IsCancellationRequested && !_downloadComplete && !_disposing)
            {
                if (!_isPaused && !_downloadFullTrack)
                {
                    int currentPlayback = GetPlaybackChunk();

                    // Расширяем ТОЛЬКО если playback ПРОДВИНУЛСЯ ВПЕРЁД
                    if (currentPlayback > _prevExtendPlayback)
                    {
                        _prevExtendPlayback = currentPlayback;

                        int margin = SecondsToChunks(3);
                        if (currentPlayback + margin >= _targetBufferChunk)
                        {
                            int oldTarget = _targetBufferChunk;
                            UpdateTarget(currentPlayback, Config.ReadAheadSeconds, "Timer");

                            if (_targetBufferChunk > oldTarget)
                            {
                                int queued = ScheduleChunks(currentPlayback);
                                if (queued > 0) _hasUrgentWork = true;
                            }
                        }
                    }
                }

                await Task.Delay(Config.ExtendIntervalMs, _disposeCts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Stream Implementation

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed || _disposing || _disposeCts.IsCancellationRequested)
            return 0;

        long pos = Volatile.Read(ref _position);
        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, _contentLength - pos);
        if (count <= 0) return 0;

        int chunkIndex = (int)(pos / _chunkSize);
        int offsetInChunk = (int)(pos % _chunkSize);
        int toRead = Math.Min(count, _chunkSize - offsetInChunk);

        try
        {
            // Быстрый путь
            if (HasChunk(chunkIndex))
            {
                Interlocked.Increment(ref _cacheHits);
                return ReadAndAdvance(chunkIndex, offsetInChunk, buffer, offset, toRead);
            }

            Interlocked.Increment(ref _cacheMisses);

            // Проверяем находится ли чанк в диапазоне [playback, target] или tail
            bool isInRange = IsInDownloadRange(chunkIndex);

            if (isInRange)
            {
                // В пределах нужного диапазона - качаем
                EnqueueUrgentIfNeeded(chunkIndex);
                Log.Debug($"[Buffer] Need chunk {chunkIndex} (playback={_lastPlaybackChunk}, target={_targetBufferChunk})");
            }
            else
            {
                // VLC lookahead за пределами диапазона - НЕ качаем
                Interlocked.Increment(ref _vlcRequestsIgnored);
                if (_vlcRequestsIgnored % 20 == 1)
                {
                    Log.Debug($"[Buffer] Ignoring VLC chunk {chunkIndex} " +
                             $"(range={_lastPlaybackChunk}-{_targetBufferChunk})");
                }
            }

            // Ждём чанк с разными таймаутами
            int maxWaitMs = isInRange ? Config.CriticalWaitMs : Config.NonCriticalWaitMs;
            var sw = Stopwatch.StartNew();

            while (!HasChunk(chunkIndex))
            {
                if (_disposed || _disposing) return 0;

                if (sw.ElapsedMilliseconds > maxWaitMs)
                {
                    if (isInRange)
                        Log.Error($"[Buffer] Timeout waiting for chunk {chunkIndex}");
                    return 0;
                }

                try { _dataAvailable.Wait(Config.ChunkWaitMs, _disposeCts.Token); }
                catch { return 0; }

                if (!HasChunk(chunkIndex)) _dataAvailable.Reset();
            }

            return ReadAndAdvance(chunkIndex, offsetInChunk, buffer, offset, toRead);
        }
        catch (Exception ex)
        {
            Log.Error($"[Buffer] Read error: {ex.Message}");
            return 0;
        }
    }

    private void EnqueueUrgentIfNeeded(int chunkIndex)
    {
        lock (_queueLock)
        {
            if (CanEnqueue(chunkIndex) && _queuedChunks.Add(chunkIndex))
            {
                _downloadQueue.Enqueue(chunkIndex, Config.PriorityUrgent);
                _hasUrgentWork = true;
            }
        }
    }

    private int ReadAndAdvance(int idx, int off, byte[] buf, int bufOff, int count)
    {
        int bytesRead = ReadChunk(idx, off, buf, bufOff, count);
        if (bytesRead > 0) Interlocked.Add(ref _position, bytesRead);
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
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        newPos = Math.Clamp(newPos, 0, _contentLength);
        long oldPos = Interlocked.Exchange(ref _position, newPos);

        int oldChunk = (int)(oldPos / _chunkSize);
        int newChunk = (int)(newPos / _chunkSize);

        if (Math.Abs(newChunk - oldChunk) > 2)
            Log.Debug($"[Buffer] Stream.Seek: {oldChunk} → {newChunk}");

        return newPos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    #endregion

    #region Chunk Reading

    private int ReadChunk(int idx, int off, byte[] buf, int bufOff, int count)
    {
        if (_chunks.TryGetValue(idx, out var chunk))
        {
            int usefulLen = idx == _totalChunks - 1
                ? (int)(_contentLength - ((long)idx * _chunkSize))
                : _chunkSize;
            int available = Math.Min(count, usefulLen - off);
            if (available > 0) Buffer.BlockCopy(chunk, off, buf, bufOff, available);
            return available;
        }

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
            if (!_fileSemaphore.Wait(Config.ChunkWaitMs, _disposeCts.Token))
                return 0;

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

    #region Download Loop

    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        Log.Debug($"[Buffer] Download loop started");

        while (!ct.IsCancellationRequested && !_disposing)
        {
            bool shouldWork = _hasUrgentWork || _downloadFullTrack || !_isPaused;

            if (!shouldWork)
            {
                await Task.Delay(Config.PauseCheckIntervalMs, ct).ConfigureAwait(false);
                continue;
            }

            (int chunk, int priority) = DequeueNextChunk();

            if (chunk < 0)
            {
                if (IsAllDownloaded())
                {
                    _downloadComplete = true;
                    Log.Debug($"[Buffer] FULLY CACHED ({_totalChunks} chunks), ignored={_vlcRequestsIgnored}");
                    break;
                }

                await Task.Delay(Config.IdleLoopMs, ct);
                continue;
            }

            if (HasChunk(chunk)) continue;

            Log.Debug($"[Buffer] Downloading chunk {chunk}");

            // ОПТИМИЗАЦИЯ: Срочные чанки используют отдельный семафор
            bool isUrgent = priority <= Config.PriorityUrgent + 3;
            var semaphore = isUrgent ? _urgentDownloadSemaphore : _downloadSemaphore;

            try { await semaphore.WaitAsync(ct); }
            catch { break; }

            _ = DownloadChunkAsync(chunk, semaphore, ct);
        }

        Log.Debug($"[Buffer] Download loop ended");
    }

    private (int chunk, int priority) DequeueNextChunk()
    {
        lock (_queueLock)
        {
            while (_downloadQueue.Count > 0)
            {
                if (!_downloadQueue.TryDequeue(out var chunk, out var priority))
                    continue;

                _queuedChunks.Remove(chunk);

                if (!HasChunk(chunk) && !_pendingDownloads.ContainsKey(chunk))
                    return (chunk, priority);
            }

            if (_pendingDownloads.Count == 0)
                _hasUrgentWork = false;

            return (-1, int.MaxValue);
        }
    }

    private async Task DownloadChunkAsync(int idx, SemaphoreSlim semaphore, CancellationToken ct)
    {
        byte[]? buffer = null;
        int retry = 0;

        try
        {
            if (HasChunk(idx) || _disposing) return;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingDownloads.TryAdd(idx, tcs.Task)) return;

            while (retry <= Config.MaxRetries)
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

                    if (resp.StatusCode == HttpStatusCode.Forbidden &&
                        retry < Config.MaxRetries &&
                        _urlRefresher != null)
                    {
                        await RefreshUrlAsync(cts.Token);
                        retry++;
                        continue;
                    }

                    resp.EnsureSuccessStatusCode();

                    buffer = ArrayPool<byte>.Shared.Rent(_chunkSize);
                    using var netStream = await resp.Content.ReadAsStreamAsync(cts.Token);

                    int totalRead = 0, bytesRead;
                    while ((bytesRead = await netStream.ReadAsync(
                        buffer.AsMemory(totalRead, _chunkSize - totalRead), cts.Token)) > 0)
                    {
                        totalRead += bytesRead;
                    }

                    if (!_chunks.ContainsKey(idx) && !_disposing)
                    {
                        _chunks[idx] = buffer;
                        Interlocked.Add(ref _bytesDownloaded, totalRead);
                        _dataAvailable.Set();

                        MemoryDiagnostics.TrackBytes("Stream.RAMChunks", buffer.Length);

                        if (_cacheFile != null && !_disposing)
                        {
                            var diskBuf = ArrayPool<byte>.Shared.Rent(totalRead);
                            Buffer.BlockCopy(buffer, 0, diskBuf, 0, totalRead);
                            await _diskChannel.Writer.WriteAsync((start, diskBuf, totalRead), cts.Token);
                        }

                        buffer = null;

                        if (_chunks.Count > _maxRamChunks)
                            TrimRamCache();
                    }

                    tcs.SetResult();
                    break;
                }
                catch (Exception ex) when (retry < Config.MaxRetries && ex is not OperationCanceledException)
                {
                    Log.Warn($"[Buffer] Chunk {idx} retry {retry + 1}: {ex.Message}");
                    await Task.Delay(Config.RetryDelayMs, ct);
                    retry++;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[Buffer] Chunk {idx} failed: {ex.Message}");
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            _pendingDownloads.TryRemove(idx, out _);
            semaphore.Release(); // ВАЖНО: Освобождаем правильный семафор
        }
    }

    private async ValueTask RefreshUrlAsync(CancellationToken ct)
    {
        if (!await _refreshLock.WaitAsync(Config.ChunkWaitMs, ct)) return;
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

        int playback = _lastPlaybackChunk;
        int keepStart = playback - Config.ChunksToKeepBehind;
        int keepEnd = playback + _readAheadChunks * Config.ChunksToKeepMultiplier;

        foreach (var key in _chunks.Keys)
        {
            if (key >= _tailStartChunk) continue;
            if (key >= keepStart && key <= keepEnd) continue;

            long start = (long)key * _chunkSize;
            if (!_diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength)))
                continue;

            if (_chunks.TryRemove(key, out var buf))
            {
                MemoryDiagnostics.UntrackBytes("Stream.RAMChunks", buf.Length);
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
                    if (_disposing || _cacheFile == null)
                    {
                        ArrayPool<byte>.Shared.Return(data);
                        continue;
                    }

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

                    if (!_downloadComplete && _diskRanges.IsFullyDownloaded(_contentLength))
                    {
                        _downloadComplete = true;
                        SaveRanges();
                        Log.Debug($"[Buffer] FULLY CACHED ({_totalChunks} chunks)");
                        _cacheManager.TriggerCacheCompleted(_cacheId, _originalTrackId);
                    }
                    else if (bytesWritten >= Config.SaveThresholdBytes)
                    {
                        SaveRanges();
                        bytesWritten = 0;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error($"[Buffer] Disk write error: {ex.Message}"); }
                finally { ArrayPool<byte>.Shared.Return(data); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
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
        try { StreamCacheManager.UpdateRanges(_cacheId, _diskRanges); }
        catch { }
    }

    #endregion

    #region File Helpers

    private static FileStream? OpenCacheFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch { return null; }
        }

        for (int attempt = 1; attempt <= Config.MaxOpenRetries; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            catch (IOException) when (attempt < Config.MaxOpenRetries)
            {
                Thread.Sleep(Config.RetryDelayBaseMs * attempt);
            }
            catch { return null; }
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

        Log.Debug($"[Buffer] Disposing: hits={_cacheHits}, misses={_cacheMisses}, ignored={_vlcRequestsIgnored}");

        if (disposing)
        {
            MemoryDiagnostics.UntrackInstance("Stream.Active");
            MemoryDiagnostics.UntrackBytes("Stream.TotalSize", _contentLength);

            Try(_downloadCts.Cancel);
            Try(_disposeCts.Cancel);
            Try(() => _diskChannel.Writer.TryComplete());

            while (_diskChannel.Reader.TryRead(out var item))
                ArrayPool<byte>.Shared.Return(item.Data);

            SaveRanges();
            _dataAvailable.Set();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAny(_diskWriterTask, Task.Delay(Config.FlushTimeoutMs));
                    await Task.WhenAny(_bufferExtenderTask, Task.Delay(500));

                    await _fileSemaphore.WaitAsync(Config.SemaphoreTimeoutMs);
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
                    Try(_urgentDownloadSemaphore.Dispose);
                    Try(_refreshLock.Dispose);
                    Try(_downloadCts.Dispose);
                    Try(_disposeCts.Dispose);
                    Try(_dataAvailable.Dispose);
                }
            });
        }

        base.Dispose(disposing);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Try(Action a) { try { a(); } catch { } }

    #endregion
}