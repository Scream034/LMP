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
    private readonly long _contentLength;
    private readonly AudioFormat _format;
    private readonly int _bitrate;

    // ── Derived from config ──
    private int _chunkSize;
    private int _totalChunks;

    // ── Dependencies ──
    private readonly HttpClient _httpClient;
    private readonly AudioCacheManager _cacheManager;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    // ── Parsing ──

    /// <summary>
    /// Метаданные кэша текущего трека. Гарантированно не null после успешного
    /// <see cref="InitializeAsync"/>; null-guard при обращении снаружи init — программная ошибка.
    /// </summary>
    private CacheEntry? _cacheEntry;

    private IContainerParser? _parser;
    private AsyncCachingReadStream? _readStream;

    // ── Chunk storage ──

    /// <summary>
    /// RAM-кэш чанков. Ключ = индекс чанка. Значение = данные + реальная длина.
    /// </summary>
    private readonly ConcurrentDictionary<int, ChunkData> _ramChunks = new();

    /// <summary>
    /// Словарь активных загрузок для дедупликации параллельных запросов одного чанка.
    /// </summary>
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();

    private readonly SemaphoreSlim _downloadSlots;

    // ── Epoch-based cancellation ──
    private long _downloadEpoch;
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

    /// <summary>
    /// ManualResetEventSlim для блокировки preload loop при suspend.
    /// Set = работаем, Reset = приостановлены.
    /// </summary>
    private readonly ManualResetEventSlim _suspendGate = new(initialState: true);

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

    /// <summary>Прогресс буферизации (0–100%). Считается на основе загруженных чанков.</summary>
    public double BufferProgress => _cacheEntry?.DownloadProgress ?? 0;

    /// <summary>Полностью ли загружен трек на диск.</summary>
    public bool IsFullyBuffered => _cacheEntry?.IsComplete ?? false;

    /// <summary>Объём скачанных данных в байтах.</summary>
    public long DownloadedBytes => (_cacheEntry?.DownloadedChunks ?? 0) * (long)_chunkSize;

    /// <summary>Битрейт (kbps).</summary>
    public int Bitrate => _cacheEntry?.Bitrate ?? _bitrate;

    #endregion

    #region Constructor

    /// <summary>
    /// Создаёт источник с кэширующим HTTP-стримингом.
    /// </summary>
    /// <param name="cacheKey">Уникальный ключ кэша (trackId + format + bitrate).</param>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="url">Исходный URL потока.</param>
    /// <param name="contentLength">Полный размер контента в байтах.</param>
    /// <param name="format">Аудио-формат контейнера.</param>
    /// <param name="codec">Аудио-кодек.</param>
    /// <param name="bitrate">Битрейт в kbps.</param>
    /// <param name="httpClient">HTTP-клиент для загрузки чанков.</param>
    /// <param name="cacheManager">Менеджер дискового кэша.</param>
    /// <param name="config">Конфигурация стриминга.</param>
    /// <param name="urlRefresher">
    /// Делегат обновления URL при истечении срока действия (403). Может быть null
    /// если URL статичен (локальные источники, тесты).
    /// </param>
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
        _currentUrl = url;
        _contentLength = contentLength;
        _format = format;
        _bitrate = bitrate;
        _httpClient = httpClient;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;
        Codec = codec;

        _chunkSize = config.ChunkSizeBytes;
        _totalChunks = contentLength > 0
            ? (int)Math.Ceiling((double)contentLength / _chunkSize)
            : 1;

        _downloadSlots = new SemaphoreSlim(config.MaxConcurrentDownloads);
    }

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            _cacheEntry = _cacheManager.CreateOrUpdate(
                _cacheKey, _trackId, _currentUrl, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format),
                _bitrate,
                chunkSize: _chunkSize);

            // Сверяем ChunkSize с уже существующей записью кэша во избежание рассинхронизации побайтовой адресации на диске.
            if (_cacheEntry.DownloadedChunks > 0)
            {
                if (_cacheEntry.ChunkSize > 0 && _cacheEntry.ChunkSize != _chunkSize)
                {
                    Log.Info($"[CachingSource] Locking chunk size to cached entry: " +
                             $"requested={_chunkSize}B → locked={_cacheEntry.ChunkSize}B " +
                             $"(totalChunks: {_totalChunks}→{_cacheEntry.TotalChunks})");

                    _chunkSize = _cacheEntry.ChunkSize;
                    _totalChunks = _cacheEntry.TotalChunks;
                }
            }
            else
            {
                bool sizeChanged = _cacheEntry.TotalSize != _contentLength;
                bool chunkSizeChanged = _cacheEntry.ChunkSize != _chunkSize;

                if (sizeChanged || chunkSizeChanged)
                {
                    Log.Info($"[CachingSource] Updating empty cache entry schema: " +
                             $"size={_cacheEntry.TotalSize}B→{_contentLength}B, " +
                             $"chunkSize={_cacheEntry.ChunkSize}B→{_chunkSize}B, " +
                             $"totalChunks={_cacheEntry.TotalChunks}→{_totalChunks}");

                    _cacheEntry.TotalSize = _contentLength;
                    _cacheEntry.ChunkSize = _chunkSize;
                    _cacheEntry.TotalChunks = _totalChunks;
                    _cacheEntry.ResetChunkMask();
                }
            }

            // Подписываемся на событие очистки глобального кэша для динамического сброса маски чанков
            _cacheManager.OnCacheCleared += HandleGlobalCacheCleared;

            if (_cacheEntry.DownloadedChunks > 0)
            {
                Log.Info($"[CachingSource] Resuming: " +
                         $"{_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks} chunks");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            await EnsureChunkAsync(0, _lifetimeCts.Token);

            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            int totalInitial = Math.Min(_config.InitialChunksToLoad, _cacheEntry.TotalChunks);
            var parseTask = _parser.ParseHeadersAsync(ct).AsTask();
            var remainingTask = totalInitial > 1
                ? LoadChunkRangeAsync(1, totalInitial, _lifetimeCts.Token)
                : Task.CompletedTask;

            await Task.WhenAll(parseTask, remainingTask);

            if (!parseTask.Result)
                throw new InvalidOperationException("Failed to parse container headers");

            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;

            _initialized = true;

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

    /// <summary>
    /// Обрабатывает событие полной очистки глобального дискового кэша.
    /// Сбрасывает маску дисковых сегментов и безопасно освобождает RAM-буферы (кроме воспроизводимого в данный момент),
    /// переводя источник в режим чистого сетевого стриминга без прерывания звука.
    /// </summary>
    private void HandleGlobalCacheCleared()
    {
        if (_disposed || _cacheEntry == null) return;

        Log.Info($"[CachingSource] Cache cleared event received. Invalidating cache state for: {_cacheKey}");

        _cacheEntry.IsComplete = false;
        _cacheEntry.ResetChunkMask();

        // Очищаем RAM-буферы, кроме текущего воспроизводимого чанка, чтобы избежать заикания декодера
        int current = Volatile.Read(ref _currentChunk);
        foreach (var key in _ramChunks.Keys)
        {
            if (key != current && _ramChunks.TryRemove(key, out var chunk))
            {
                chunk.Dispose();
            }
        }

        // Сбрасываем эпохи для отмены зависших или выполняющихся дисковых операций
        ResetDownloadEpoch();
    }

    /// <summary>
    /// Загружает диапазон чанков [<paramref name="from"/>, <paramref name="count"/>) параллельно.
    /// </summary>
    /// <param name="from">Первый индекс чанка включительно.</param>
    /// <param name="count">Верхняя граница (исключительно).</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task LoadChunkRangeAsync(int from, int count, CancellationToken ct)
    {
        var tasks = new Task[count - from];
        for (int i = from; i < count; i++)
            tasks[i - from] = EnsureChunkAsync(i, ct);
        await Task.WhenAll(tasks);
    }

    /// <summary>Выбирает парсер контейнера на основе формата трека.</summary>
    /// <param name="stream">Поток данных для парсинга.</param>
    /// <returns>Экземпляр парсера.</returns>
    private IContainerParser CreateParser(Stream stream) => _format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };

    /// <summary>
    /// Инициализирует первую эпоху загрузки. Вызывается один раз в <see cref="InitializeAsync"/>.
    /// </summary>
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

    #endregion

    #region Reading

    /// <inheritdoc/>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        // Убран рекурсивный catch (IOException), вызывавший бесконечный цикл при удалении или блокировке файлов.
        // Ошибки ввода-вывода теперь безопасно обрабатываются пайплайном с ограничением попыток.
        var frame = await _parser.ReadNextFrameAsync(ct);
        if (frame == null) return null;

        Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
        UpdateCurrentChunk();
        return frame;
    }

    /// <summary>
    /// Обновляет <see cref="_currentChunk"/> на основе текущей позиции потока.
    /// Используется preload loop для определения, какие чанки нужно подгружать следующими.
    /// </summary>
    private void UpdateCurrentChunk()
    {
        if (_readStream != null)
            _currentChunk = (int)(_readStream.Position / _chunkSize);
    }

    #endregion

    #region Epoch-Based Cancellation

    /// <summary>
    /// Отменяет все загрузки текущей эпохи и создаёт новую.
    /// Вызывается при seek — мгновенно прерывает все активные ReadAsync/SendAsync
    /// через <see cref="OperationCanceledException"/> без ожидания их завершения.
    /// </summary>
    /// <returns>CancellationToken новой эпохи.</returns>
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
                // Синхронный Cancel — все ReadAsync/SendAsync мгновенно
                // получат OperationCanceledException.
                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }

                // Dispose через background task — даём IO completion threads
                // время вернуть результат перед освобождением ресурсов.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    try { oldCts.Dispose(); } catch { }
                });
            }

            return _downloadCts.Token;
        }
    }

    /// <summary>CancellationToken текущей эпохи загрузки. Потокобезопасно.</summary>
    private CancellationToken CurrentDownloadToken
    {
        get
        {
            lock (_epochLock)
                return _downloadCts?.Token ?? CancellationToken.None;
        }
    }

    #endregion

    #region Public Buffer Management

    /// <inheritdoc/>
    public void ReleaseRamBuffers()
    {
        int current = Volatile.Read(ref _currentChunk);
        int evictionDistance = _config.RamEvictionDistance;

        // Итерируем по snapshot ключей напрямую — без LINQ, без промежуточного List.
        // ConcurrentDictionary.Keys возвращает snapshot, безопасный для итерации
        // при конкурентных модификациях.
        foreach (var kvp in _ramChunks)
        {
            if (Math.Abs(kvp.Key - current) > evictionDistance
                && _ramChunks.TryRemove(kvp.Key, out var evicted))
            {
                evicted.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public void CancelPendingOperations() => _lifetimeCts?.Cancel();

    #endregion

    #region Dispose

    /// <summary>
    /// Диспозит все чанки в RAM-кэше, возвращая арендованные буферы
    /// в <see cref="System.Buffers.MemoryPool{T}.Shared"/>.
    /// </summary>
    private void DisposeAllRamChunks()
    {
        foreach (var kvp in _ramChunks)
        {
            if (_ramChunks.TryRemove(kvp.Key, out var chunk))
                chunk.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _suspendGate.Set();

        // Отписываемся от глобального события очистки кэша
        _cacheManager.OnCacheCleared -= HandleGlobalCacheCleared;

        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _downloadCts?.Dispose(); } catch (ObjectDisposedException) { }
            _downloadCts = null;
        }

        try { _lifetimeCts?.Cancel(); } catch (ObjectDisposedException) { }

        if (_preloadTask is { IsCompleted: false })
        {
            try { _preloadTask.Wait(TimeSpan.FromMilliseconds(250)); }
            catch { }
        }

        try { _lifetimeCts?.Dispose(); } catch (ObjectDisposedException) { }

        _parser?.Dispose();
        _readStream?.Dispose();
        DisposeAllRamChunks();

        try { _refreshLock.Dispose(); } catch (ObjectDisposedException) { }
        try { _downloadSlots.Dispose(); } catch (ObjectDisposedException) { }
        _suspendGate.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _suspendGate.Set();

        // Отписываемся от глобального события очистки кэша
        _cacheManager.OnCacheCleared -= HandleGlobalCacheCleared;

        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _downloadCts?.Dispose(); } catch (ObjectDisposedException) { }
            _downloadCts = null;
        }

        try { _lifetimeCts?.Cancel(); } catch (ObjectDisposedException) { }

        if (_preloadTask != null)
        {
            try { await _preloadTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }

        await Task.Delay(32);

        try { _lifetimeCts?.Dispose(); } catch (ObjectDisposedException) { }

        if (_parser != null) await _parser.DisposeAsync();

        _readStream?.Dispose();
        DisposeAllRamChunks();

        try { _refreshLock.Dispose(); } catch (ObjectDisposedException) { }
        try { _downloadSlots.Dispose(); } catch (ObjectDisposedException) { }
        _suspendGate.Dispose();
    }

    #endregion
}