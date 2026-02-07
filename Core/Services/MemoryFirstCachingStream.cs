using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Кэширующий поток с приоритетом RAM-буфера.
/// Загружает данные чанками, сохраняет на диск для переиспользования.
/// Поддерживает throttling для экономии трафика и prioritized downloading.
/// </summary>
public sealed class MemoryFirstCachingStream : Stream
{
    #region Static Constants

    /// <summary>
    /// Константы, не зависящие от профиля стриминга.
    /// </summary>
    private static class Invariants
    {
        /// <summary>Максимум попыток открытия файла кэша.</summary>
        public const int MaxOpenRetries = 10;

        /// <summary>Базовая задержка между попытками открытия (мс).</summary>
        public const int RetryDelayBaseMs = 100;

        /// <summary>Таймаут ожидания flush при dispose (мс).</summary>
        public const int FlushTimeoutMs = 1000;

        /// <summary>Таймаут ожидания семафора (мс).</summary>
        public const int SemaphoreTimeoutMs = 2000;

        /// <summary>Интервал проверки при паузе (мс).</summary>
        public const int PauseCheckIntervalMs = 500;

        /// <summary>Интервал idle-цикла загрузки (мс).</summary>
        public const int IdleLoopMs = 100;

        /// <summary>Интервал проверки prebuffer (мс).</summary>
        public const int PreBufferCheckMs = 150;

        /// <summary>Приоритет: header-чанки (начало файла).</summary>
        public const int PriorityHeader = 0;

        /// <summary>Приоритет: tail-чанки (конец файла).</summary>
        public const int PriorityTail = 1;

        /// <summary>Приоритет: чанки для текущего воспроизведения.</summary>
        public const int PriorityPlayback = 2;

        /// <summary>Приоритет: близкие чанки.</summary>
        public const int PriorityNear = 10;

        /// <summary>Приоритет: далёкие чанки (фоновая загрузка).</summary>
        public const int PriorityFar = 1000;

        /// <summary>Множитель для хранения чанков позади.</summary>
        public const int ChunksToKeepMultiplier = 2;

        /// <summary>Время ожидания после seek для сброса target (мс).</summary>
        public const int SeekTargetResetDelayMs = 500;
    }

    #endregion

    #region Fields

    // Config values (cached from StreamingConfig)
    private readonly int _chunkSize;
    private readonly int _readAheadChunks;
    private readonly int _maxConcurrentDownloads;
    private readonly int _maxRamChunks;
    private readonly int _downloadTimeoutMs;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;
    private readonly int _initialBufferSeconds;
    private readonly int _initialReadAheadChunks;
    private readonly int _headerChunks;
    private readonly int _tailChunks;
    private readonly int _maxReadAheadFromPlayback;
    private readonly int _maxDownloadAheadChunks;
    private readonly int _chunksToKeepBehind;
    private readonly int _bufferExtendIntervalMs;
    private readonly int _maxReadBlockMs;
    private readonly int _readPollIntervalMs;
    private readonly int _saveThresholdBytes;
    private readonly int _urgentBoostMultiplier;
    private readonly int _diskChannelCapacity;

    // Computed values
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
    private volatile bool _playbackStarted;
    private volatile int _lastScheduledFromChunk;

    /// <summary>
    /// Целевой чанк после seek. -1 если нет активного seek.
    /// Используется для правильного throttling после перемотки.
    /// </summary>
    private int _seekTargetChunk = -1;

    private readonly SemaphoreSlim _urgentDownloadSemaphore;
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

    private CancellationTokenSource _readCts = new();
    private readonly Lock _readCtsLock = new();

    private readonly Task _diskWriterTask;
    private readonly Task _bufferExtenderTask;
    private Task? _downloadLoop;
    private FileStream? _cacheFile;

    private int _cacheHits;
    private int _cacheMisses;

    #endregion

    #region Stream Properties

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _contentLength;

    /// <inheritdoc/>
    public override long Position
    {
        get => Volatile.Read(ref _position);
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>
    /// Прогресс загрузки в процентах (0-100).
    /// Корректно учитывает прерывистый кэш (RAM + диск).
    /// </summary>
    public double DownloadProgress
    {
        get
        {
            if (_contentLength <= 0) return 0;
            if (_downloadComplete) return 100;

            return Math.Min((double)BufferedBytes / _contentLength * 100, 100);
        }
    }

    /// <summary>
    /// Трек полностью загружен и закэширован.
    /// </summary>
    public bool IsFullyDownloaded => _downloadComplete;

    /// <summary>
    /// Количество байт, реально доступных для чтения.
    /// </summary>
    public long BufferedBytes
    {
        get
        {
            if (_downloadComplete) return _contentLength;

            long total = 0;
            for (int i = 0; i < _totalChunks; i++)
            {
                if (HasChunk(i)) // Этот метод проверяет и RAM и DiskRanges атомарно
                {
                    long chunkStart = (long)i * _chunkSize;
                    long chunkEnd = Math.Min(chunkStart + _chunkSize, _contentLength);
                    total += chunkEnd - chunkStart;
                }
            }
            return total;
        }
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Создаёт кэширующий поток для аудио.
    /// </summary>
    public MemoryFirstCachingStream(
        string cacheId,
        string url,
        long contentLength,
        HttpClient http,
        StreamCacheManager cacheManager,
        StreamingConfig config,
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

        // Извлекаем все значения из конфига
        _chunkSize = config.ChunkSizeBytes;
        _readAheadChunks = config.ReadAheadChunks;
        _maxConcurrentDownloads = config.MaxConcurrentDownloads;
        _maxRamChunks = Math.Max(config.MaxRamChunks, 10);
        _downloadTimeoutMs = config.DownloadTimeoutMs;
        _maxRetries = config.MaxRetries;
        _retryDelayMs = config.RetryDelayMs;
        _downloadFullTrack = config.DownloadFullTrack;
        _initialBufferSeconds = config.InitialBufferSeconds;
        _initialReadAheadChunks = config.InitialReadAheadChunks;
        _headerChunks = config.HeaderChunks;
        _tailChunks = config.TailChunks;
        _maxReadAheadFromPlayback = config.MaxReadAheadFromPlayback;
        _maxDownloadAheadChunks = config.MaxDownloadAheadChunks;
        _chunksToKeepBehind = config.ChunksToKeepBehind;
        _bufferExtendIntervalMs = config.BufferExtendIntervalMs;
        _maxReadBlockMs = config.MaxReadBlockMs;
        _readPollIntervalMs = config.ReadPollIntervalMs;
        _saveThresholdBytes = config.SaveThresholdBytes;
        _urgentBoostMultiplier = config.UrgentBoostMultiplier;
        _diskChannelCapacity = config.DiskChannelCapacity;

        _cachePath = StreamCacheManager.GetCachePath(_cacheId);
        _downloadCts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        _urgentDownloadSemaphore = new SemaphoreSlim(
            Math.Max(_maxConcurrentDownloads * _urgentBoostMultiplier, 4));
        _totalChunks = (int)((_contentLength + _chunkSize - 1) / _chunkSize);
        _tailStartChunk = Math.Max(0, _totalChunks - _tailChunks);

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
            new BoundedChannelOptions(_diskChannelCapacity)
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

    private CancellationToken GetReadToken()
    {
        lock (_readCtsLock) { return _readCts.Token; }
    }

    /// <summary>
    /// Получает текущую позицию воспроизведения с учётом seek.
    /// </summary>
    private int GetEffectivePlaybackChunk()
    {
        // Сначала проверяем seek target (более актуален)
        int seekTarget = _seekTargetChunk;
        if (seekTarget >= 0)
        {
            return seekTarget;
        }

        // Fallback на реальную позицию от VLC
        if (_getPlaybackTimeMs == null || _totalDurationMs <= 0) return 0;
        long currentMs = _getPlaybackTimeMs();
        return currentMs <= 0 ? 0 : TimeToChunk(currentMs);
    }

    private int CalculatePriority(int chunkIndex)
    {
        if (chunkIndex < _headerChunks)
            return Invariants.PriorityHeader;

        if (chunkIndex >= _tailStartChunk)
            return Invariants.PriorityTail;

        int effectivePlayback = GetEffectivePlaybackChunk();
        int distance = chunkIndex - effectivePlayback;

        if (distance <= 0)
            return Invariants.PriorityPlayback;

        if (distance <= _maxDownloadAheadChunks)
            return Invariants.PriorityNear + distance;

        return Invariants.PriorityFar + distance;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Пребуферизация начальных данных перед воспроизведением.
    /// </summary>
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
            ScheduleInitialChunks();

            if (HasChunk(0))
            {
                Log.Debug($"[Buffer] Chunk 0 ready");
                return true;
            }

            while (!HasChunk(0))
            {
                if (token.IsCancellationRequested) return false;
                if (!_dataAvailable.Wait(Invariants.PreBufferCheckMs, token))
                    if (sw.ElapsedMilliseconds > _downloadTimeoutMs) return false;
                if (!HasChunk(0)) _dataAvailable.Reset();
            }

            Log.Debug($"[Buffer] PreBuffer complete in {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Уведомление о начале воспроизведения.
    /// </summary>
    public void NotifyPlaybackStarted()
    {
        if (_playbackStarted) return;

        _playbackStarted = true;
        _seekTargetChunk = -1; // Сбрасываем seek target только при СТАРТЕ воспроизведения
        Log.Debug($"[Buffer] Playback started, unlocking throttled reads");
        _dataAvailable.Set();
    }

    /// <summary>
    /// Уведомление о seek (перемотке).
    /// Запоминает целевую позицию для правильной работы загрузки.
    /// </summary>
    public void NotifySeek(long positionMs)
    {
        int newChunk = TimeToChunk(positionMs);

        // Запоминаем целевой чанк
        _seekTargetChunk = newChunk;

        Log.Debug($"[Buffer] NotifySeek: {positionMs}ms → chunk {newChunk}");

        // Планируем загрузку от новой позиции с высоким приоритетом
        ScheduleChunksFromSeek(newChunk);
        _dataAvailable.Set();

        // ДОБАВЛЕНО: Сбрасываем seek target через задержку
        _ = Task.Run(async () =>
        {
            await Task.Delay(Invariants.SeekTargetResetDelayMs);
            if (_seekTargetChunk == newChunk) // Только если не было нового seek
            {
                _seekTargetChunk = -1;
                Log.Debug($"[Buffer] Seek target reset after timeout");
            }
        });
    }

    /// <summary>
    /// Отменяет ожидающие операции чтения.
    /// </summary>
    public void CancelPendingReads()
    {
        long pos = Volatile.Read(ref _position);
        Log.Debug($"[Buffer] CancelPendingReads (pos={pos})");

        lock (_readCtsLock)
        {
            try
            {
                var old = _readCts;
                _readCts = new CancellationTokenSource();
                old.Cancel();
                old.Dispose();
            }
            catch { }
        }

        _dataAvailable.Set();
    }

    /// <summary>
    /// Уведомление о паузе/возобновлении воспроизведения.
    /// </summary>
    public void NotifyPaused(bool paused)
    {
        if (_isPaused == paused) return;
        _isPaused = paused;
        Log.Debug($"[Buffer] Pause={paused}");
    }

    /// <summary>
    /// Включает загрузку всего трека (для Ultra профиля).
    /// </summary>
    public void EnableFullDownload()
    {
        if (_downloadFullTrack) return;
        _downloadFullTrack = true;
        ScheduleChunksFrom(0);
        Log.Debug($"[Buffer] Full download enabled");
    }

    /// <summary>
    /// Освобождает RAM-буферы при сворачивании приложения.
    /// </summary>
    public void ReleaseRamBuffers()
    {
        if (_disposed) return;

        long freed = 0;
        int effectivePlayback = GetEffectivePlaybackChunk();

        foreach (var kvp in _chunks)
        {
            int idx = kvp.Key;

            if (idx < _headerChunks || idx >= _tailStartChunk) continue;
            if (Math.Abs(idx - effectivePlayback) <= _chunksToKeepBehind) continue;

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

    /// <summary>
    /// Возвращает закэшированные диапазоны для визуализации прогресса.
    /// </summary>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_contentLength <= 0) return [];
        if (_downloadComplete) return [(0.0, 1.0)];

        // Собираем все закэшированные чанки
        var cachedChunks = new SortedSet<int>();

        // RAM-чанки
        foreach (var idx in _chunks.Keys)
        {
            cachedChunks.Add(idx);
        }

        // Диск-чанки
        for (int i = 0; i < _totalChunks; i++)
        {
            if (cachedChunks.Contains(i)) continue;

            long start = (long)i * _chunkSize;
            long end = Math.Min(start + _chunkSize, _contentLength);

            if (_diskRanges.IsRangeComplete(start, end))
            {
                cachedChunks.Add(i);
            }
        }

        if (cachedChunks.Count == 0) return [];

        // Группируем последовательные чанки в диапазоны
        var ranges = new List<(double, double)>();
        int? rangeStart = null;
        int? rangeLast = null;

        foreach (var idx in cachedChunks)
        {
            if (rangeStart == null)
            {
                rangeStart = idx;
                rangeLast = idx;
            }
            else if (idx == rangeLast + 1)
            {
                rangeLast = idx;
            }
            else
            {
                // Закрываем текущий диапазон
                AddChunkRange(ranges, rangeStart.Value, rangeLast!.Value);
                rangeStart = idx;
                rangeLast = idx;
            }
        }

        // Добавляем последний диапазон
        if (rangeStart != null)
        {
            AddChunkRange(ranges, rangeStart.Value, rangeLast!.Value);
        }

        return ranges;

        void AddChunkRange(List<(double, double)> list, int startChunk, int endChunk)
        {
            double startPct = (double)((long)startChunk * _chunkSize) / _contentLength;
            double endPct = Math.Min((double)(((long)endChunk + 1) * _chunkSize) / _contentLength, 1.0);
            list.Add((startPct, endPct));
        }
    }

    #endregion

    #region Scheduling

    private void ScheduleInitialChunks()
    {
        lock (_queueLock)
        {
            // Header чанки
            for (int i = 0; i < Math.Min(_headerChunks, _totalChunks); i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, Invariants.PriorityHeader);
            }

            // Tail чанки
            for (int i = _tailStartChunk; i < _totalChunks; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, Invariants.PriorityTail);
            }

            // Начальный буфер
            int initialLimit = Math.Min(_headerChunks + SecondsToChunks(_initialBufferSeconds), _tailStartChunk);
            for (int i = _headerChunks; i < initialLimit; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, Invariants.PriorityNear + i);
            }
        }

        Log.Debug($"[Buffer] Initial chunks scheduled: headers=0-{_headerChunks - 1}, " +
                  $"tails={_tailStartChunk}-{_totalChunks - 1}");
    }

    private void ScheduleChunksFrom(int fromChunk)
    {
        if (fromChunk == _lastScheduledFromChunk) return;
        _lastScheduledFromChunk = fromChunk;

        lock (_queueLock)
        {
            // Headers
            for (int i = 0; i < Math.Min(_headerChunks, _totalChunks); i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, Invariants.PriorityHeader);
            }

            // Tails
            for (int i = _tailStartChunk; i < _totalChunks; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, Invariants.PriorityTail);
            }

            // От позиции в пределах окна
            int limit = Math.Min(fromChunk + _maxDownloadAheadChunks, _tailStartChunk);
            for (int i = Math.Max(fromChunk, _headerChunks); i < limit; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, CalculatePriority(i));
            }
        }
    }

    /// <summary>
    /// Планирует загрузку после seek с максимальным приоритетом.
    /// </summary>
    private void ScheduleChunksFromSeek(int targetChunk)
    {
        _lastScheduledFromChunk = -1; // Сбрасываем чтобы гарантировать планирование

        lock (_queueLock)
        {
            // Целевой чанк и окрестности с максимальным приоритетом
            int windowStart = Math.Max(0, targetChunk - 1);
            int windowEnd = Math.Min(_totalChunks - 1, targetChunk + _maxReadAheadFromPlayback);

            for (int i = windowStart; i <= windowEnd; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                {
                    // Чанки рядом с seek target - максимальный приоритет
                    int priority = Math.Abs(i - targetChunk) <= 2
                        ? Invariants.PriorityPlayback
                        : Invariants.PriorityNear + Math.Abs(i - targetChunk);
                    _downloadQueue.Enqueue(i, priority);
                }
            }

            // Headers и Tails тоже нужны
            for (int i = 0; i < Math.Min(_headerChunks, _totalChunks); i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, Invariants.PriorityHeader);
            }

            for (int i = _tailStartChunk; i < _totalChunks; i++)
            {
                if (CanEnqueue(i) && _queuedChunks.Add(i))
                    _downloadQueue.Enqueue(i, Invariants.PriorityTail);
            }
        }
    }

    #endregion

    #region Buffer Extender

    private async Task BufferExtenderLoopAsync()
    {
        await Task.Delay(_bufferExtendIntervalMs, _disposeCts.Token).ConfigureAwait(false);

        try
        {
            while (!_disposeCts.IsCancellationRequested && !_downloadComplete && !_disposing)
            {
                if (_downloadFullTrack)
                    ScheduleChunksFrom(0);
                else if (_playbackStarted)
                    ScheduleChunksFrom(GetEffectivePlaybackChunk());

                await Task.Delay(_bufferExtendIntervalMs, _disposeCts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Stream Implementation

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed || _disposing) return 0;

        long pos = Volatile.Read(ref _position);
        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, _contentLength - pos);
        if (count <= 0) return 0;

        int chunkIndex = (int)(pos / _chunkSize);
        int offsetInChunk = (int)(pos % _chunkSize);
        int toRead = Math.Min(count, _chunkSize - offsetInChunk);

        try
        {
            // ═══════════════════════════════════════════════════════════════
            // FAST PATH: Данные есть - отдаём МГНОВЕННО (никакого throttling!)
            // ═══════════════════════════════════════════════════════════════
            if (HasChunk(chunkIndex))
            {
                Interlocked.Increment(ref _cacheHits);
                return ReadAndAdvance(chunkIndex, offsetInChunk, buffer, offset, toRead);
            }

            // Чанка нет - нужно скачать
            Interlocked.Increment(ref _cacheMisses);

            int seekTarget = _seekTargetChunk;
            int effectivePlayback = GetEffectivePlaybackChunk();
            bool isMetadata = chunkIndex < _headerChunks || chunkIndex >= _tailStartChunk;

            // ═══════════════════════════════════════════════════════════════
            // УСТАРЕВШИЙ ЗАПРОС: VLC просит данные далеко от текущей позиции
            // НЕ возвращаем 0 (это вызовет EndReached!), а ждём КОРОТКО
            // ═══════════════════════════════════════════════════════════════
            if (!isMetadata && seekTarget >= 0)
            {
                int distanceFromSeek = Math.Abs(chunkIndex - seekTarget);

                // Если VLC просит чанк ОЧЕНЬ далеко от seek target (старый буфер VLC)
                if (distanceFromSeek > _maxReadAheadFromPlayback * 3)
                {
                    Log.Debug($"[Buffer] STALE request for chunk {chunkIndex} (seek={seekTarget}, distance={distanceFromSeek})");

                    // Ждём ОЧЕНЬ коротко - может данные появятся
                    var staleSw = Stopwatch.StartNew();
                    while (!HasChunk(chunkIndex) && staleSw.ElapsedMilliseconds < 500)
                    {
                        if (_disposed || _disposing) return 0;

                        // Если seek target изменился - выходим
                        if (_seekTargetChunk != seekTarget)
                        {
                            Log.Debug($"[Buffer] Seek target changed during stale wait");
                            return 0; // Тут 0 безопасен - новый seek уже в процессе
                        }

                        try { _dataAvailable.Wait(100, GetReadToken()); }
                        catch (OperationCanceledException) { return 0; }
                    }

                    // Если данные появились - отдаём
                    if (HasChunk(chunkIndex))
                    {
                        return ReadAndAdvance(chunkIndex, offsetInChunk, buffer, offset, toRead);
                    }

                    // Данных нет, но это устаревший запрос - скачиваем с низким приоритетом
                    // и ждём ещё немного
                    EnqueueWithPriority(chunkIndex, Invariants.PriorityFar + distanceFromSeek);
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // THROTTLING: Только для новых данных ВПЕРЕДИ playback
            // ═══════════════════════════════════════════════════════════════
            if (!isMetadata && _playbackStarted)
            {
                int maxAllowed = seekTarget >= 0
                    ? Math.Max(seekTarget, effectivePlayback) + _maxReadAheadFromPlayback
                    : effectivePlayback + _maxReadAheadFromPlayback;

                if (chunkIndex > maxAllowed)
                {
                    Log.Debug($"[Buffer] THROTTLE chunk {chunkIndex} (max={maxAllowed}, seek={seekTarget}, playback={effectivePlayback})");

                    var sw = Stopwatch.StartNew();
                    while (chunkIndex > maxAllowed)
                    {
                        if (_disposed || _disposing) return 0;

                        // Если seek изменился - пересчитываем
                        int newSeekTarget = _seekTargetChunk;
                        if (newSeekTarget != seekTarget && newSeekTarget >= 0)
                        {
                            // Новый seek! Проверяем актуальность текущего запроса
                            if (Math.Abs(chunkIndex - newSeekTarget) > _maxReadAheadFromPlayback * 2)
                            {
                                // Запрос устарел из-за нового seek
                                Log.Debug($"[Buffer] Chunk {chunkIndex} obsolete after new seek to {newSeekTarget}");
                                break;
                            }
                            seekTarget = newSeekTarget;
                        }

                        // Таймаут throttle - 3 секунды
                        if (sw.ElapsedMilliseconds > 3000)
                        {
                            Log.Debug($"[Buffer] Throttle timeout for chunk {chunkIndex}");
                            break;
                        }

                        try
                        {
                            _dataAvailable.Wait(200, GetReadToken());
                        }
                        catch (OperationCanceledException) { return 0; }

                        // Может чанк уже скачался?
                        if (HasChunk(chunkIndex)) break;

                        // Обновляем окно
                        effectivePlayback = GetEffectivePlaybackChunk();
                        maxAllowed = _seekTargetChunk >= 0
                            ? Math.Max(_seekTargetChunk, effectivePlayback) + _maxReadAheadFromPlayback
                            : effectivePlayback + _maxReadAheadFromPlayback;

                        _dataAvailable.Reset();
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // DOWNLOAD: Качаем нужный чанк
            // ═══════════════════════════════════════════════════════════════
            EnqueueWithPriority(chunkIndex, Invariants.PriorityPlayback);

            var waitSw = Stopwatch.StartNew();
            while (!HasChunk(chunkIndex))
            {
                if (_disposed || _disposing) return 0;

                if (waitSw.ElapsedMilliseconds > _maxReadBlockMs)
                {
                    Log.Warn($"[Buffer] Download timeout for chunk {chunkIndex}");
                    // НЕ возвращаем 0! Пробуем вернуть сколько есть
                    break;
                }

                try
                {
                    _dataAvailable.Wait(_readPollIntervalMs, GetReadToken());
                }
                catch (OperationCanceledException) { return 0; }

                if (!HasChunk(chunkIndex))
                {
                    _dataAvailable.Reset();
                    EnqueueWithPriority(chunkIndex, Invariants.PriorityPlayback);
                }
            }

            // Финальная проверка
            if (HasChunk(chunkIndex))
            {
                return ReadAndAdvance(chunkIndex, offsetInChunk, buffer, offset, toRead);
            }

            // Данных так и нет - возвращаем 0 (крайний случай)
            Log.Warn($"[Buffer] No data for chunk {chunkIndex} after all attempts");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"[Buffer] Read error: {ex.Message}");
            return 0;
        }
    }

    private void EnqueueWithPriority(int chunkIndex, int priority)
    {
        lock (_queueLock)
        {
            if (CanEnqueue(chunkIndex) && _queuedChunks.Add(chunkIndex))
                _downloadQueue.Enqueue(chunkIndex, priority);
        }
    }

    private int ReadAndAdvance(int idx, int off, byte[] buf, int bufOff, int count)
    {
        int bytesRead = ReadChunk(idx, off, buf, bufOff, count);
        if (bytesRead > 0)
        {
            Interlocked.Add(ref _position, bytesRead);
            Log.Trace($"[Buffer] Read {bytesRead} bytes from chunk {idx}, pos={Position}");
        }
        return bytesRead;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
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
            if (!_fileSemaphore.Wait(Invariants.SemaphoreTimeoutMs, _disposeCts.Token))
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
            (int chunk, int priority) = DequeueNextChunk();

            if (chunk < 0)
            {
                if (IsAllDownloaded())
                {
                    _downloadComplete = true;
                    Log.Debug($"[Buffer] FULLY CACHED ({_totalChunks} chunks)");
                    break;
                }

                await Task.Delay(Invariants.IdleLoopMs, ct);
                continue;
            }

            if (HasChunk(chunk)) continue;

            bool isUrgent = priority < Invariants.PriorityFar;
            var semaphore = isUrgent ? _urgentDownloadSemaphore : _downloadSemaphore;

            try { await semaphore.WaitAsync(ct); }
            catch { break; }

            Log.Debug($"[Buffer] Downloading chunk {chunk} (priority={priority}, urgent={isUrgent})");
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

            while (retry <= _maxRetries)
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
                        retry < _maxRetries &&
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
                catch (Exception ex) when (retry < _maxRetries && ex is not OperationCanceledException)
                {
                    Log.Warn($"[Buffer] Chunk {idx} retry {retry + 1}: {ex.Message}");
                    await Task.Delay(_retryDelayMs, ct);
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
            semaphore.Release();
        }
    }

    private async ValueTask RefreshUrlAsync(CancellationToken ct)
    {
        if (!await _refreshLock.WaitAsync(Invariants.SemaphoreTimeoutMs, ct)) return;
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

        int effectivePlayback = GetEffectivePlaybackChunk();
        int keepStart = Math.Max(0, effectivePlayback - _chunksToKeepBehind);
        int keepEnd = effectivePlayback + _readAheadChunks * Invariants.ChunksToKeepMultiplier;

        foreach (var key in _chunks.Keys)
        {
            if (key < _headerChunks) continue;
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
                    else if (bytesWritten >= _saveThresholdBytes)
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

        for (int attempt = 1; attempt <= Invariants.MaxOpenRetries; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            catch (IOException) when (attempt < Invariants.MaxOpenRetries)
            {
                Thread.Sleep(Invariants.RetryDelayBaseMs * attempt);
            }
            catch { return null; }
        }
        return null;
    }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposing = true;
        _disposed = true;

        Log.Debug($"[Buffer] Disposing: hits={_cacheHits}, misses={_cacheMisses}");

        if (disposing)
        {
            MemoryDiagnostics.UntrackInstance("Stream.Active");
            MemoryDiagnostics.UntrackBytes("Stream.TotalSize", _contentLength);

            Try(_downloadCts.Cancel);
            Try(_disposeCts.Cancel);

            lock (_readCtsLock)
            {
                Try(_readCts.Cancel);
                Try(_readCts.Dispose);
            }

            Try(() => _diskChannel.Writer.TryComplete());

            while (_diskChannel.Reader.TryRead(out var item))
                ArrayPool<byte>.Shared.Return(item.Data);

            SaveRanges();
            _dataAvailable.Set();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAny(_diskWriterTask, Task.Delay(Invariants.FlushTimeoutMs));
                    await Task.WhenAny(_bufferExtenderTask, Task.Delay(Invariants.PauseCheckIntervalMs));

                    await _fileSemaphore.WaitAsync(Invariants.SemaphoreTimeoutMs);
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