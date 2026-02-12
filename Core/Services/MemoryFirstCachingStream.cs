// Core/Services/Streaming/MemoryFirstCachingStream.cs

using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using LMP.Core.Models;

namespace LMP.Core.Services.Streaming;

/// <summary>
/// ФИНАЛЬНАЯ АРХИТЕКТУРА:
/// - Read() ВСЕГДА неблокирующий — отдаёт данные если есть, планирует загрузку если нет
/// - Throttling ТОЛЬКО в PreloadLoop — качает строго [playback .. playback+ahead]
/// - Если VLC запросил чанк вне окна — планируем ТОЛЬКО ЕГО (urgent), но НЕ соседей
/// - _activeDownloads ограничен MaxConcurrentDownloads слотами
/// - При смене трека — инвалидируем сессию (FullStop)
/// - При seek — НЕ инвалидируем (просто меняем позиции)
/// </summary>
public sealed class MemoryFirstCachingStream : MediaStreamBase
{
    #region Session (только для смены трека)

    private sealed class LoadSession : IDisposable
    {
        public int Id { get; }
        public CancellationTokenSource Cts { get; }
        public CancellationToken Token => Cts.Token;

        public LoadSession(int id, CancellationToken parentToken)
        {
            Id = id;
            Cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        }

        public void Cancel() { try { Cts.Cancel(); } catch { } }
        public void Dispose() { try { Cts.Dispose(); } catch { } }
    }

    #endregion

    #region Fields

    private readonly string _cacheId;
    private readonly string _originalTrackId;
    private readonly StreamCacheManager? _cacheManager;
    private readonly StreamingConfig _config;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;
    private readonly Func<long>? _getPlaybackTimeMs;
    private readonly long _totalDurationMs;

    private string _currentUrl;
    private DateTime _urlFetchedAt;
    private readonly SemaphoreSlim _urlLock = new(1, 1);

    private readonly int _chunkSize;
    private readonly int _totalChunks;
    private readonly ConcurrentDictionary<int, byte[]> _ramChunks = new();
    private readonly ConcurrentBitArray _downloadedChunks;
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSlots;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private Task? _preloadTask;
    private Task? _flushTask;

    private long _bytesDownloaded;
    private int _ramChunkCount;
    private volatile int _playbackChunk = -1;
    private volatile int _readHead;

    private volatile int _sessionId;
    private LoadSession? _currentSession;
    private readonly Lock _sessionLock = new();

    private readonly ManualResetEventSlim _dataSignal = new(false);

    private const int UrlRefreshIntervalMs = 300_000;
    private const int WaitPollIntervalMs = 50;
    private const int SlotTimeoutMs = 500;

    #endregion

    #region Properties

    public override double DownloadProgress =>
        _totalChunks == 0 ? 0 : Math.Min((double)_downloadedChunks.PopCount() / _totalChunks * 100, 100);

    public override long BufferedBytes => Volatile.Read(ref _bytesDownloaded);
    public override bool IsFullyDownloaded => _downloadedChunks.PopCount() >= _totalChunks;

    #endregion

    #region Constructor

    public MemoryFirstCachingStream(
        string cacheId, string url, long contentLength, HttpClient http,
        StreamCacheManager? cacheManager, StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        string? originalTrackId = null,
        Func<long>? getPlaybackTimeMs = null,
        long totalDurationMs = 0)
        : base(http)
    {
        if (contentLength <= 0)
            throw new ArgumentException("Content length must be positive", nameof(contentLength));

        _cacheId = cacheId;
        _originalTrackId = originalTrackId ?? cacheId;
        _currentUrl = url;
        _cacheManager = cacheManager;
        _config = config;
        _urlRefresher = urlRefresher;
        _getPlaybackTimeMs = getPlaybackTimeMs;
        _totalDurationMs = totalDurationMs;

        TotalLength = contentLength;
        _urlFetchedAt = DateTime.UtcNow;

        _chunkSize = config.ChunkSizeBytes;
        _totalChunks = (int)Math.Ceiling((double)contentLength / _chunkSize);
        _downloadedChunks = new ConcurrentBitArray(_totalChunks);
        _downloadSlots = new SemaphoreSlim(config.MaxConcurrentDownloads);

        _currentSession = new LoadSession(0, DisposeCts.Token);

        Log.Debug($"[Stream] Created: {_totalChunks} chunks × {_chunkSize / 1024}KB, ahead={config.MaxDownloadAheadChunks}, fullTrack={config.DownloadFullTrack}");
    }

    #endregion

    #region Session Control

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LoadSession? GetSession() => _currentSession?.Token.IsCancellationRequested == false && !IsDisposed ? _currentSession : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSessionValid(int expectedId) =>
        !IsDisposed && _sessionId == expectedId && !(_currentSession?.Token.IsCancellationRequested ?? true);

    public override void CancelPendingReads()
    {
        // Seek — только разблокируем
        _dataSignal.Set();
    }

    public void FullStop()
    {
        lock (_sessionLock)
        {
            Interlocked.Increment(ref _sessionId);
            _currentSession?.Cancel();
            _dataSignal.Set();
            _activeDownloads.Clear();
        }
    }

    #endregion

    #region Initialization

    public override async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return true;

            if (_cacheManager != null && await TryLoadFromCacheAsync(ct))
            {
                Log.Info($"[Stream] Loaded from cache: {_cacheId}");
                _initialized = true;
                return true;
            }

            _initialized = true;

            var session = GetSession();
            if (session != null)
            {
                _preloadTask = Task.Run(() => PreloadLoopAsync(session), session.Token);
                _flushTask = Task.Run(() => FlushLoopAsync(session), session.Token);
            }

            Log.Debug($"[Stream] Initialized: {_cacheId}");
            return true;
        }
        finally { _initLock.Release(); }
    }

    public override async ValueTask<bool> PreBufferAsync(CancellationToken ct = default)
    {
        if (!await InitializeAsync(ct)) return false;

        var session = GetSession();
        if (session == null) return false;

        for (int i = 0; i < Math.Min(_config.InitialReadAheadChunks, _totalChunks); i++)
            StartDownload(i, session);

        var deadline = DateTime.UtcNow.AddMilliseconds(_config.DownloadTimeoutMs);

        while (!HasChunk(0))
        {
            if (!IsSessionValid(session.Id) || ct.IsCancellationRequested) return false;
            if (DateTime.UtcNow > deadline) { Log.Warn("[Stream] PreBuffer timeout"); return false; }
            _dataSignal.Wait(100);
            _dataSignal.Reset();
        }

        Log.Debug($"[Stream] PreBuffer complete: {_downloadedChunks.PopCount()}/{_config.InitialReadAheadChunks} chunks");
        return true;
    }

    #endregion

    #region Read — Always non-blocking

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (IsDisposed || !_initialized) return 0;

        var session = GetSession();
        if (session == null) return 0;

        long pos = CurrentPosition;
        if (pos >= TotalLength) return 0;

        count = (int)Math.Min(count, TotalLength - pos);
        if (count <= 0) return 0;

        int chunkIndex = (int)(pos / _chunkSize);
        int offsetInChunk = (int)(pos % _chunkSize);

        if (chunkIndex > _readHead)
            _readHead = chunkIndex;

        // Если есть — отдаём мгновенно
        if (!HasChunk(chunkIndex))
        {
            // Планируем ТОЛЬКО этот чанк (urgent)
            StartDownload(chunkIndex, session);

            // Ждём с таймаутом
            if (!WaitForChunkData(chunkIndex, _config.DownloadTimeoutMs))
                return 0;
        }

        var data = GetChunkData(chunkIndex);
        if (data == null) return 0;

        int available = Math.Min(count, data.Length - offsetInChunk);
        if (available <= 0)
        {
            if (chunkIndex + 1 < _totalChunks)
            {
                CurrentPosition = (long)(chunkIndex + 1) * _chunkSize;
                return Read(buffer, offset, count);
            }
            return 0;
        }

        Buffer.BlockCopy(data, offsetInChunk, buffer, offset, available);
        CurrentPosition = pos + available;
        return available;
    }

    private bool WaitForChunkData(int chunkIndex, int timeoutMs)
    {
        if (HasChunk(chunkIndex)) return true;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (!HasChunk(chunkIndex))
        {
            if (IsDisposed) return false;
            if (DateTime.UtcNow > deadline) return false;

            _dataSignal.Wait(WaitPollIntervalMs);
            _dataSignal.Reset();
        }

        return true;
    }

    #endregion

    #region Seek — NO session invalidation

    protected override void OnSeekInternal(long positionMs)
    {
        long bytePosition = 0;
        if (_totalDurationMs > 0 && TotalLength > 0)
        {
            double progress = Math.Clamp((double)positionMs / _totalDurationMs, 0, 1);
            bytePosition = (long)(progress * TotalLength);
        }

        int newChunk = Math.Clamp((int)(bytePosition / _chunkSize), 0, Math.Max(0, _totalChunks - 1));

        _readHead = newChunk;
        _playbackChunk = newChunk;
        CurrentPosition = bytePosition;

        _dataSignal.Set();

        Log.Debug($"[Stream] Seek → chunk {newChunk}");

        var session = GetSession();
        if (session != null)
            Task.Run(() => PlanDownloadsFromPlayback(session));
    }

    #endregion

    #region Playback Tracking & Preload

    private void RefreshPlaybackChunk()
    {
        if (_getPlaybackTimeMs == null || _totalDurationMs <= 0) return;

        long ms = _getPlaybackTimeMs();
        if (ms <= 0) return;

        double progress = (double)ms / _totalDurationMs;
        long bytePos = (long)(progress * TotalLength);
        int chunk = Math.Clamp((int)(bytePos / _chunkSize), 0, _totalChunks - 1);

        if (chunk != _playbackChunk)
            _playbackChunk = chunk;
    }

    /// <summary>
    /// Планирует загрузки СТРОГО в окне [playback .. playback+ahead].
    /// </summary>
    private void PlanDownloadsFromPlayback(LoadSession session)
    {
        if (!IsSessionValid(session.Id)) return;

        RefreshPlaybackChunk();

        int start = _playbackChunk >= 0 ? _playbackChunk : 0;
        int end = Math.Min(start + _config.MaxDownloadAheadChunks, _totalChunks - 1);

        for (int i = start; i <= end; i++)
        {
            if (!IsSessionValid(session.Id)) return;
            if (!HasChunk(i) && !_activeDownloads.ContainsKey(i))
                StartDownload(i, session);
        }
    }

    private void StartDownload(int index, LoadSession session)
    {
        if (index < 0 || index >= _totalChunks) return;
        if (HasChunk(index) || _activeDownloads.ContainsKey(index)) return;
        if (!IsSessionValid(session.Id)) return;

        var task = Task.Run(() => DownloadChunkAsync(index, session));
        _activeDownloads.TryAdd(index, task);
    }

    private async Task PreloadLoopAsync(LoadSession session)
    {
        while (IsSessionValid(session.Id))
        {
            try
            {
                await Task.Delay(_config.BufferExtendIntervalMs, session.Token);

                if (IsPaused || !PlaybackStarted || !IsSessionValid(session.Id)) continue;

                RefreshPlaybackChunk();

                if (_config.DownloadFullTrack)
                {
                    // Качаем всё
                    int scheduled = 0;
                    for (int i = 0; i < _totalChunks && scheduled < _config.MaxConcurrentDownloads * 2; i++)
                    {
                        if (!HasChunk(i) && !_activeDownloads.ContainsKey(i))
                        {
                            StartDownload(i, session);
                            scheduled++;
                        }
                    }
                }
                else
                {
                    // Качаем ТОЛЬКО окно
                    PlanDownloadsFromPlayback(session);
                    
                    // Отменяем загрузки ВНЕ окна
                    CancelDownloadsOutsideWindow();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"[Stream] Preload error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Отменяет активные загрузки которые слишком далеко от playback.
    /// НЕ трогает загрузки в окне [playback-behind .. playback+ahead*2].
    /// </summary>
    private void CancelDownloadsOutsideWindow()
    {
        int pb = _playbackChunk;
        if (pb < 0) return;

        int minAllowed = pb - _config.ChunksToKeepBehind;
        int maxAllowed = pb + _config.MaxDownloadAheadChunks * 2;

        var toCancel = _activeDownloads.Keys
            .Where(i => i < minAllowed || i > maxAllowed)
            .ToList();

        foreach (var idx in toCancel)
        {
            if (_activeDownloads.TryRemove(idx, out _))
                Log.Trace($"[Stream] Cancelled download: chunk {idx} (outside window)");
        }
    }

    #endregion

    #region Chunks

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasChunk(int index) => _downloadedChunks.Get(index);

    private byte[]? GetChunkData(int index)
    {
        if (_ramChunks.TryGetValue(index, out var data)) return data;
        if (_cacheManager != null)
        {
            data = ReadChunkFromDisk(index);
            if (data != null)
            {
                TryPromoteToRam(index, data);
                return data;
            }
        }
        return null;
    }

    private byte[]? ReadChunkFromDisk(int index)
    {
        try
        {
            var cachePath = StreamCacheManager.GetCachePath(_cacheId);
            if (!File.Exists(cachePath)) return null;

            long start = (long)index * _chunkSize;
            int size = (int)Math.Min(_chunkSize, TotalLength - start);

            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[size];
            return fs.Read(buf, 0, size) == size ? buf : null;
        }
        catch { return null; }
    }

    private void TryPromoteToRam(int index, byte[] data)
    {
        if (Volatile.Read(ref _ramChunkCount) < _config.MaxRamChunks && _ramChunks.TryAdd(index, data))
            Interlocked.Increment(ref _ramChunkCount);
    }

    private void StoreChunk(int index, byte[] data)
    {
        _downloadedChunks.Set(index, true);
        Interlocked.Add(ref _bytesDownloaded, data.Length);
        if (_ramChunks.TryAdd(index, data))
            Interlocked.Increment(ref _ramChunkCount);
        WriteChunkToDisk(index, data);
        _dataSignal.Set();
    }

    private void WriteChunkToDisk(int index, byte[] data)
    {
        if (_cacheManager == null) return;
        try
        {
            var cachePath = StreamCacheManager.GetCachePath(_cacheId);
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            long off = (long)index * _chunkSize;
            using var fs = new FileStream(cachePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            fs.Seek(off, SeekOrigin.Begin);
            fs.Write(data, 0, data.Length);
        }
        catch (Exception ex) { Log.Warn($"[Stream] WriteChunk {index} failed: {ex.Message}"); }
    }

    #endregion

    #region Download

    private async Task DownloadChunkAsync(int index, LoadSession session)
    {
        var ct = session.Token;
        if (HasChunk(index) || !IsSessionValid(session.Id))
        {
            _activeDownloads.TryRemove(index, out _);
            return;
        }

        bool gotSlot = false;
        try
        {
            gotSlot = await _downloadSlots.WaitAsync(SlotTimeoutMs, ct);
            if (!gotSlot || HasChunk(index) || !IsSessionValid(session.Id)) return;

            long start = (long)index * _chunkSize;
            long end = Math.Min(start + _chunkSize - 1, TotalLength - 1);

            for (int retry = 0; retry <= _config.MaxRetries; retry++)
            {
                if (!IsSessionValid(session.Id)) return;

                try
                {
                    await EnsureFreshUrlAsync(ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_config.DownloadTimeoutMs);

                    using var request = CreateRequest(HttpMethod.Get, _currentUrl, range: (start, end));
                    request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");

                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Log.Warn($"[Stream] Chunk {index} got 403");
                        await RefreshUrlAsync(ct);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    if (!IsSessionValid(session.Id)) return;

                    var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
                    if (!IsSessionValid(session.Id)) return;

                    StoreChunk(index, data);
                    Log.Debug($"[Stream] Chunk {index}: {data.Length} bytes");
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (retry < _config.MaxRetries)
                {
                    if (!IsSessionValid(session.Id)) return;
                    Log.Warn($"[Stream] Chunk {index} retry {retry + 1}: {ex.Message}");
                    try { await Task.Delay(_config.RetryDelayMs * (retry + 1), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            _activeDownloads.TryRemove(index, out _);
            if (gotSlot) try { _downloadSlots.Release(); } catch { }
        }
    }

    #endregion

    #region URL & Flush

    private async Task EnsureFreshUrlAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _urlFetchedAt).TotalMilliseconds < UrlRefreshIntervalMs) return;
        await RefreshUrlAsync(ct);
    }

    private async Task RefreshUrlAsync(CancellationToken ct)
    {
        if (_urlRefresher == null) return;
        bool gotLock = false;
        try
        {
            gotLock = await _urlLock.WaitAsync(1000, ct);
            if (!gotLock || (DateTime.UtcNow - _urlFetchedAt).TotalMilliseconds < 10_000) return;

            var newUrl = await _urlRefresher(ct);
            if (!string.IsNullOrEmpty(newUrl))
            {
                _currentUrl = newUrl;
                _urlFetchedAt = DateTime.UtcNow;
                Log.Debug("[Stream] URL refreshed");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"[Stream] URL refresh failed: {ex.Message}"); }
        finally { if (gotLock) try { _urlLock.Release(); } catch { } }
    }

    private async Task FlushLoopAsync(LoadSession session)
    {
        while (IsSessionValid(session.Id))
        {
            try
            {
                await Task.Delay(5000, session.Token);
                if (Volatile.Read(ref _ramChunkCount) > _config.MaxRamChunks)
                    FlushOldChunksFromRam();
                if (IsFullyDownloaded) { await FinalizeAsync(); break; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"[Stream] Flush error: {ex.Message}"); }
        }
    }

    private void FlushOldChunksFromRam()
    {
        int center = Math.Max(_readHead, _playbackChunk);
        var toFlush = _ramChunks.Keys
            .Where(i => i < center - _config.ChunksToKeepBehind || i > center + _config.MaxDownloadAheadChunks * 2)
            .OrderByDescending(i => Math.Abs(i - center))
            .Take(_config.MaxRamChunks / 4).ToList();

        int flushed = 0;
        foreach (var idx in toFlush)
            if (_ramChunks.TryRemove(idx, out _)) { Interlocked.Decrement(ref _ramChunkCount); flushed++; }

        if (flushed > 0) Log.Trace($"[Stream] Flushed {flushed} RAM chunks");
    }

    private async Task FinalizeAsync()
    {
        if (_cacheManager == null) return;
        try
        {
            var meta = StreamCacheManager.TryGetMetadata(_cacheId);
            if (meta != null)
            {
                var ranges = new RangeMap();
                ranges.MarkComplete(0, TotalLength);
                meta.RangesJson = ranges.Serialize();
                meta.LastAccessedAt = DateTime.UtcNow;
                StreamCacheManager.SaveMetadata(_cacheId, meta);
            }
            _cacheManager.TriggerCacheCompleted(_cacheId, _originalTrackId);
            Log.Info($"[Stream] Finalized: {_cacheId}");
        }
        catch (Exception ex) { Log.Warn($"[Stream] Finalize failed: {ex.Message}"); }
    }

    private async Task<bool> TryLoadFromCacheAsync(CancellationToken ct)
    {
        if (_cacheManager == null) return false;
        try
        {
            var meta = StreamCacheManager.TryGetMetadata(_cacheId);
            if (meta?.ContentLength != TotalLength) return false;

            var cachePath = StreamCacheManager.GetCachePath(_cacheId);
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length != TotalLength) return false;

            var ranges = RangeMap.Deserialize(meta.RangesJson);
            if (!ranges.IsFullyDownloaded(TotalLength)) return false;

            for (int i = 0; i < _totalChunks; i++)
                _downloadedChunks.Set(i, true);

            Volatile.Write(ref _bytesDownloaded, TotalLength);
            return true;
        }
        catch (Exception ex) { Log.Warn($"[Stream] Cache load failed: {ex.Message}"); return false; }
    }

    public override IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_totalChunks == 0 || TotalLength == 0) return [];

        var ranges = new List<(double, double)>();
        int? rs = null, re = null;

        for (int i = 0; i < _totalChunks; i++)
        {
            if (HasChunk(i)) { rs ??= i; re = i; }
            else if (rs != null) { AddRange(rs.Value, re!.Value); rs = null; }
        }

        if (rs != null) AddRange(rs.Value, re!.Value);
        return ranges;

        void AddRange(int s, int e)
        {
            double sp = (double)((long)s * _chunkSize) / TotalLength;
            double ep = (double)Math.Min((long)(e + 1) * _chunkSize, TotalLength) / TotalLength;
            ranges.Add((sp, Math.Min(ep, 1.0)));
        }
    }

    #endregion

    #region Lifecycle

    protected override void OnPlaybackStarted() => Log.Debug("[Stream] Playback started");

    protected override void OnPauseStateChanged(bool paused)
    {
        if (!paused)
        {
            RefreshPlaybackChunk();
            var session = GetSession();
            if (session != null) PlanDownloadsFromPlayback(session);
        }
    }

    public override void ReleaseRamBuffers()
    {
        int center = Math.Max(_readHead, _playbackChunk);
        int released = 0;

        foreach (var idx in _ramChunks.Keys.Where(i => Math.Abs(i - center) > 3).ToList())
            if (_ramChunks.TryRemove(idx, out _)) { Interlocked.Decrement(ref _ramChunkCount); released++; }

        if (released > 0) { GC.Collect(1, GCCollectionMode.Optimized, false); Log.Debug($"[Stream] Released {released} RAM chunks"); }
    }

    protected override void OnDispose()
    {
        FullStop();

        if (_cacheManager != null && IsFullyDownloaded)
        {
            try
            {
                var meta = StreamCacheManager.TryGetMetadata(_cacheId);
                if (meta != null)
                {
                    var ranges = new RangeMap();
                    ranges.MarkComplete(0, TotalLength);
                    meta.RangesJson = ranges.Serialize();
                    StreamCacheManager.SaveMetadata(_cacheId, meta);
                }
            }
            catch { }
        }

        _ramChunks.Clear();
        try { _downloadSlots.Dispose(); } catch { }
        try { _urlLock.Dispose(); } catch { }
        try { _initLock.Dispose(); } catch { }
        try { _dataSignal.Dispose(); } catch { }

        lock (_sessionLock) { try { _currentSession?.Dispose(); } catch { } _currentSession = null; }

        Log.Debug($"[Stream] Disposed: {_cacheId}");
    }

    #endregion
}

#region ConcurrentBitArray
internal sealed class ConcurrentBitArray
{
    private readonly int[] _data;
    private readonly int _length;

    public ConcurrentBitArray(int length)
    {
        _length = length;
        _data = new int[(length + 31) / 32];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index) =>
        (uint)index < (uint)_length && (Volatile.Read(ref _data[index >> 5]) & (1 << (index & 31))) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, bool value)
    {
        if ((uint)index >= (uint)_length) return;
        int word = index >> 5, bit = 1 << (index & 31), current, desired;
        do
        {
            current = Volatile.Read(ref _data[word]);
            desired = value ? (current | bit) : (current & ~bit);
            if (desired == current) return;
        } while (Interlocked.CompareExchange(ref _data[word], desired, current) != current);
    }

    public int PopCount()
    {
        int count = 0;
        for (int i = 0; i < _data.Length; i++)
            count += BitOperations.PopCount((uint)Volatile.Read(ref _data[i]));
        return Math.Min(count, _length);
    }
}
#endregion