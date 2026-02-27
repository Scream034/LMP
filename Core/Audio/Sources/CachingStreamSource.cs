using System.Collections.Concurrent;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using LMP.Core.Models;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник аудио с сегментным кэшированием и HTTP Range-request загрузкой.
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Данные загружаются чанками размера <see cref="_config"/>.<see cref="StreamingConfig.ChunkSizeBytes"/></item>
///   <item>Чанки кэшируются в RAM (<see cref="_ramChunks"/>) и на диск (<see cref="_cacheManager"/>)</item>
///   <item>Фоновый preload loop обеспечивает опережающую загрузку</item>
///   <item>Seek реализован через epoch-based cancellation</item>
///   <item>Suspend/Resume приостанавливает фоновую загрузку при сворачивании окна</item>
/// </list>
/// 
/// <para><b>Partial class structure:</b></para>
/// <list type="bullet">
///   <item><c>CachingStreamSource.cs</c> — ядро: поля, init, read frames, suspend/resume, dispose</item>
///   <item><c>CachingStreamSource.Chunks.cs</c> — загрузка чанков с retry и circuit breaker</item>
///   <item><c>CachingStreamSource.Seeking.cs</c> — seek с epoch-based cancellation</item>
///   <item><c>CachingStreamSource.Preload.cs</c> — фоновая загрузка и буферизация</item>
///   <item><c>CachingStreamSource.ReadStream.cs</c> — Stream-обёртка для парсеров</item>
/// </list>
/// </summary>
public sealed partial class CachingStreamSource : IAudioSource
{
    #region Fields

    // ── Configuration ──
    private readonly StreamingConfig _config;

    // ── Identity ──
    private readonly string _cacheKey;
    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly AudioFormat _format;
    private readonly int _bitrate;

    // ── Derived from config ──
    private readonly int _chunkSize;
    private readonly int _totalChunks;

    // ── Dependencies ──
    private readonly HttpClient _httpClient;
    private readonly AudioCacheManager _cacheManager;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    // ── Parsing ──
    private CacheEntry? _cacheEntry;
    private IContainerParser? _parser;
    private AsyncCachingReadStream? _readStream;

    // ── Chunk storage ──
    private readonly ConcurrentDictionary<int, byte[]> _ramChunks = new();
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSlots;

    // ── Epoch-based cancellation ──
    private volatile int _downloadEpoch;
    private CancellationTokenSource? _downloadCts;
    private readonly Lock _epochLock = new();

    // ── Lifetime ──
    private CancellationTokenSource? _lifetimeCts;
    private Task? _preloadTask;

    // ── Position tracking ──
    private int _currentChunk;
    private long _positionMs;
    private string _currentUrl;
    private int _backgroundChunksLoaded;

    // ── State flags ──
    private volatile bool _initialized;
    private volatile bool _disposed;
    private volatile bool _isOfflineMode;

    /// <summary>
    /// ManualResetEventSlim для блокировки preload loop при suspend.
    /// Set = работаем, Reset = приостановлены.
    /// </summary>
    private readonly ManualResetEventSlim _suspendGate = new(true);

    #endregion

    #region Properties

    /// <inheritdoc/>
    public long DurationMs => _parser?.DurationMs ?? _cacheEntry?.DurationMs ?? -1;

    /// <inheritdoc/>
    public long PositionMs => Volatile.Read(ref _positionMs);

    /// <inheritdoc/>
    public bool CanSeek => true;

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; }

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? 0;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? 0;

    /// <summary>Прогресс буферизации (0–100%).</summary>
    public double BufferProgress => _isOfflineMode ? 100 : (_cacheEntry?.DownloadProgress ?? 0);

    /// <summary>Полностью ли загружен трек.</summary>
    public bool IsFullyBuffered => _cacheEntry?.IsComplete ?? _isOfflineMode;

    /// <summary>Играем ли из полного кэша (без сети).</summary>
    public bool IsOfflineMode => _isOfflineMode;

    /// <summary>Объём скачанных данных в байтах.</summary>
    public long DownloadedBytes => (_cacheEntry?.DownloadedChunks ?? 0) * (long)_chunkSize;

    /// <summary>Битрейт (kbps).</summary>
    public int Bitrate => _cacheEntry?.Bitrate ?? _bitrate;

    #endregion

    #region Constructor

    /// <summary>
    /// Создаёт источник с кэширующим HTTP-стримингом.
    /// </summary>
    public CachingStreamSource(
        string cacheKey,
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _config = config;
        _cacheKey = cacheKey;
        _trackId = trackId;
        _url = url;
        _currentUrl = url;
        _contentLength = contentLength;
        _format = format;
        _bitrate = bitrate;
        _httpClient = httpClient;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;
        Codec = codec;

        // Derived
        _chunkSize = config.ChunkSizeBytes;
        _totalChunks = (int)Math.Ceiling((double)contentLength / _chunkSize);
        _downloadSlots = new SemaphoreSlim(config.MaxConcurrentDownloads);
    }

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return true;

        try
        {
            // Полный кэш — офлайн
            if (_cacheManager.IsFullyCached(_cacheKey))
            {
                Log.Info($"[CachingSource] Using fully cached: {_cacheKey}");
                _isOfflineMode = true;
                return await InitializeFromCacheAsync(ct);
            }

            // Создаём/обновляем запись в кэше
            _cacheEntry = _cacheManager.CreateOrUpdate(
                _cacheKey, _trackId, _url, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format),
                _bitrate,
                chunkSize: _chunkSize);

            if (_cacheEntry.DownloadedChunks > 0)
            {
                Log.Info($"[CachingSource] Resuming: " +
                         $"{_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks} chunks");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            // Начальные чанки для парсинга
            await LoadInitialChunksAsync(_lifetimeCts.Token);

            // Парсер
            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            if (!await _parser.ParseHeadersAsync(ct))
                throw new InvalidOperationException("Failed to parse container headers");

            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;

            _initialized = true;

            // Фоновый preload
            _preloadTask = Task.Run(
                () => PreloadLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);

            Log.Info($"[CachingSource] Initialized: duration={DurationMs}ms, " +
                     $"cached={_cacheEntry.DownloadProgress:F0}%");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachingSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    private async Task<bool> InitializeFromCacheAsync(CancellationToken ct)
    {
        var stream = _cacheManager.OpenCachedStream(_cacheKey);
        if (stream == null)
        {
            _isOfflineMode = false;
            return await InitializeAsync(ct);
        }

        _cacheEntry = _cacheManager.GetCacheInfo(_cacheKey);
        _readStream = new AsyncCachingReadStream(this, stream);
        _parser = CreateParser(_readStream);

        if (!await _parser.ParseHeadersAsync(ct))
            return false;

        Codec = _parser.Codec;
        _initialized = true;
        return true;
    }

    private IContainerParser CreateParser(Stream stream) => _format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };

    private void InitializeFirstEpoch()
    {
        lock (_epochLock)
        {
            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();
            _downloadEpoch = 1;
        }
    }

    private async Task LoadInitialChunksAsync(CancellationToken ct)
    {
        if (_cacheEntry == null) return;

        int count = Math.Min(_config.InitialChunksToLoad, _cacheEntry.TotalChunks);
        var tasks = new Task[count];

        for (int i = 0; i < count; i++)
            tasks[i] = EnsureChunkAsync(i, ct);

        await Task.WhenAll(tasks);
    }

    #endregion

    #region Reading

    /// <inheritdoc/>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        try
        {
            var frame = await _parser.ReadNextFrameAsync(ct);
            if (frame == null)
                return null;

            Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
            UpdateCurrentChunk();
            return frame;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException) when (!_disposed && !_isOfflineMode)
        {
            await EnsureChunkAsync(_currentChunk, ct);
            return await ReadFrameAsync(ct);
        }
    }

    private void UpdateCurrentChunk()
    {
        if (!_isOfflineMode && _readStream != null)
            _currentChunk = (int)(_readStream.Position / _chunkSize);
    }

    #endregion

    #region Epoch-Based Cancellation

    /// <summary>
    /// Отменяет все загрузки текущей эпохи, создаёт новую.
    /// </summary>
    private CancellationToken ResetDownloadEpoch()
    {
        lock (_epochLock)
        {
            var oldCts = _downloadCts;

            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();

            Interlocked.Increment(ref _downloadEpoch);

            if (oldCts != null)
            {
                // Запускаем мягкую отмену.
                // НЕ вызываем Cancel() сразу. Даем HttpClient 50мс чтобы завершить
                // текущее чтение, а затем вызываем Cancel.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 1. Сначала отменяем через CancelAfter, чтобы внутренний таймер
                        // HttpClient мог аккуратно завершить сокеты.
                        oldCts.CancelAfter(50);

                        // 2. Ждем, чтобы HTTP запросы реально упали с TaskCanceledException
                        await Task.Delay(200);
                    }
                    catch { }
                    finally
                    {
                        try { oldCts.Dispose(); } catch { }
                    }
                });
            }

            return _downloadCts.Token;
        }
    }

    /// <summary>
    /// Graceful отмена CTS: сначала CancelAfter с коротким таймаутом,
    /// затем dispose. Предотвращает crash в SslStream при обрыве TLS фрейма.
    /// </summary>
    private static async Task GracefulCancelAsync(CancellationTokenSource cts)
    {
        try
        {
            // Даём 50мс на завершение текущего TLS фрейма
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            // Ждём чтобы Cancel успел пройти
            await Task.Delay(60);
        }
        catch (ObjectDisposedException) { }

        try { cts.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>CancellationToken текущей эпохи загрузки.</summary>
    private CancellationToken CurrentDownloadToken
    {
        get
        {
            lock (_epochLock)
            {
                return _downloadCts?.Token ?? CancellationToken.None;
            }
        }
    }

    #endregion

    #region Public Buffer Management

    /// <inheritdoc/>
    public void ReleaseRamBuffers()
    {
        int current = Volatile.Read(ref _currentChunk);

        foreach (int idx in _ramChunks.Keys
                     .Where(i => Math.Abs(i - current) > _config.RamEvictionDistance)
                     .ToList())
        {
            _ramChunks.TryRemove(idx, out _);
        }
    }

    /// <inheritdoc/>
    public void CancelPendingOperations()
    {
        _lifetimeCts?.Cancel();
    }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Разблокируем suspend gate чтобы preload loop мог завершиться
        _suspendGate.Set();

        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { _downloadCts?.Dispose(); }
            catch (ObjectDisposedException) { }
            _downloadCts = null;
        }

        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _lifetimeCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        _parser?.Dispose();
        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
        _suspendGate.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Разблокируем suspend gate чтобы preload loop мог завершиться
        _suspendGate.Set();

        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { _downloadCts?.Dispose(); }
            catch (ObjectDisposedException) { }
            _downloadCts = null;
        }

        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (_preloadTask != null)
        {
            try { await _preloadTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }

        try { _lifetimeCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        if (_parser != null)
            await _parser.DisposeAsync();

        _refreshLock.Dispose();
        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
        _suspendGate.Dispose();
    }

    #endregion
}