using System.Collections.Concurrent;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using LMP.Core.Exceptions;

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
    /// <summary>Короткая задержка перед окончательной утилизацией ресурсов (мс).</summary>
    private const int DisposalDelayMs = 32;

    /// <summary>Таймаут ожидания открытия playback gate (мс).</summary>
    private const int PlaybackGateTimeoutMs = 128;

    /// <summary>Критический таймаут playback gate (мс).</summary>
    private const int PlaybackGateCriticalTimeoutMs = 512;

    /// <summary>Количество повторов чтения при смене эпохи.</summary>
    private const int ReadAtMaxEpochRetries = 3;

    /// <summary>Задержка между retry чтения при смене эпохи (мс).</summary>
    private const int ReadAtEpochRetryDelayMs = 30;

    /// <summary>Количество последовательных 403 перед открытием circuit breaker.</summary>
    private const int MaxRefreshFailuresBeforeCircuitBreak = 2;

    /// <summary>Минимальная граница seek clamp.</summary>
    private const long SeekLowerBound = 0;

    /// <summary>Смещение для последнего байта контента.</summary>
    private const long SeekEndOffset = 1;

    /// <summary>Конец диапазона буферизации.</summary>
    private const double BufferedRangeEnd = 1.0;

    /// <summary>Задержка перед утилизацией CTS предыдущей эпохи (мс).</summary>
    private const int DeferredEpochDisposeDelayMs = 2000;

    /// <summary>Таймаут ожидания завершения preload-таска при Dispose (мс).</summary>
    private const int PreloadTaskDisposeWaitTimeoutMs = 1000;

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
    /// <remarks>
    /// <para>Значение — <see cref="Lazy{T}"/> над <see cref="Task{TResult}"/>.</para>
    /// <para>Почему не просто <see cref="Task"/>: при схеме check-then-act
    /// (<c>TryGetValue</c> → создать Task → <c>TryAdd</c>) проигравший поток уже успевает
    /// стартовать второй HTTP-запрос для того же чанка. Обёртка <see cref="Lazy{T}"/>
    /// откладывает запуск до первого обращения к <see cref="Lazy{T}.Value"/>, поэтому
    /// проигравшие в <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, TValue)"/>
    /// создают только неиспользованный lazy-объект без сетевой активности.</para>
    /// </remarks>
    private readonly ConcurrentDictionary<int, Lazy<Task<ChunkDownloadResult>>> _activeDownloads = new();

    private readonly SemaphoreSlim _downloadSlots;

    // ── Epoch-based cancellation ──
    private long _downloadEpoch;
    private CancellationTokenSource? _downloadCts;
    private readonly Lock _epochLock = new();

    /// <summary>
    /// Легковесный затвор для управления фоновым циклом предзагрузки.
    /// Set = воспроизведение активно, Reset = плеер на паузе.
    /// Начинается в состоянии Set (true), чтобы разрешить первичную буферизацию при старте.
    /// </summary>
    private readonly ManualResetEventSlim _playbackGate = new(initialState: true);

    // ── Lifetime ──
    private CancellationTokenSource? _lifetimeCts;
    private Task? _preloadTask;

    // ── Position tracking ──
    private int _currentChunk;
    private long _positionMs;
    private string _currentUrl;
    private int _backgroundChunksLoaded;

    /// <summary>
    /// Holds the last exception captured during a chunk download attempt.
    /// Used to diagnose early warnings on retry thresholds.
    /// </summary>
    private Exception? _lastDownloadException;

    // ── State flags ──
    private volatile bool _initialized;
    private volatile bool _disposed;

    // ── Latency Tracking & Adaptive Preloading ──
    private readonly object _latencyLock = new();
    private double _latency0;
    private double _latency1;
    private double _latency2;

    private double _estimatedBandwidthBytesPerSec;

    /// <summary>
    /// Текущая расчетная скорость загрузки данных из сети (байт/сек).
    /// </summary>
    public double EstimatedSpeedBytesPerSec
    {
        get
        {
            lock (_latencyLock)
            {
                return _estimatedBandwidthBytesPerSec;
            }
        }
    }

    /// <summary>
    /// Текущая средняя задержка сети (мс).
    /// </summary>
    public double AveragePingMs
    {
        get
        {
            lock (_latencyLock) return GetAverageLatencyInternal();
        }
    }

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

    /// <summary>
    /// Парсер контейнера. Доступен для диагностики (resync detection).
    /// </summary>
    internal IContainerParser? Parser => _parser;

    /// <summary>
    /// Проверяет, доступен ли чанк для заданной позиции seek.
    /// </summary>
    /// <remarks>
    /// Используется <see cref="AudioPlayer"/> в <c>ComputeSeekWarmupParams</c>
    /// для определения, нужно ли ждать HTTP-загрузку перед resume.
    /// Если целевой чанк уже в RAM или на диске — seek может resume мгновенно.
    /// </remarks>
    /// <param name="positionMs">Позиция в миллисекундах.</param>
    /// <returns><c>true</c> если чанк доступен в RAM или на диске.</returns>
    public bool IsTargetChunkAvailable(long positionMs)
    {
        if (_parser == null || !_initialized) return false;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return false;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));
        int targetChunk = Math.Clamp((int)(targetBytePos / _chunkSize), 0, _totalChunks - 1);

        return IsChunkAvailable(targetChunk);
    }

    /// <summary>
    /// Global event triggered when an audio source encounters non-fatal network issues (e.g., slow download or retries).
    /// </summary>
    public static event Action<string, Exception>? OnSourceWarning;

    private void RaiseSourceWarning(int chunkIndex, Exception ex)
    {
        try
        {
            OnSourceWarning?.Invoke(_trackId, ex);
        }
        catch (Exception logEx)
        {
            Log.Warn($"[CachingSource] Failed to raise warning event: {logEx.Message}");
        }
    }
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

            // Примиряем ChunkSize: если в кэше уже есть чанки, используем строго
            // оригинальный сохранённый ChunkSize для предотвращения дрейфа побайтовой адресации.
            if (_cacheEntry.DownloadedChunks > 0
                && _cacheEntry.TotalChunks > 0
                && _cacheEntry.ChunkSize > 0
                && _cacheEntry.ChunkSize != _chunkSize)
            {
                Log.Info($"[CachingSource] ChunkSize reconciled: " +
                         $"config={_chunkSize}B → cached={_cacheEntry.ChunkSize}B " +
                         $"(totalChunks: {_totalChunks}→{_cacheEntry.TotalChunks})");

                _chunkSize = _cacheEntry.ChunkSize;
                _totalChunks = _cacheEntry.TotalChunks;
            }

            if (_cacheEntry.DownloadedChunks > 0)
            {
                Log.Info($"[CachingSource] Resuming: " +
                         $"{_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks} chunks");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            // ПАРАЛЛЕЛЬНАЯ ИНИЦИАЛИЗАЦИЯ
            // Chunk 0 нужен для ParseHeaders (EBML + Segment + Tracks <= 128KB).
            await EnsureChunkAsync(0, _lifetimeCts.Token, isCritical: true).ConfigureAwait(false);

            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            int totalInitial = Math.Min(_config.InitialChunksToLoad, _cacheEntry.TotalChunks);
            var parseTask = _parser.ParseHeadersAsync(ct).AsTask();
            var remainingTask = totalInitial > 1
                ? LoadChunkRangeAsync(1, totalInitial, _lifetimeCts.Token)
                : Task.CompletedTask;

            await Task.WhenAll(parseTask, remainingTask).ConfigureAwait(false);

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
    /// Загружает диапазон чанков [<paramref name="from"/>, <paramref name="count"/>) параллельно.
    /// </summary>
    /// <param name="from">Первый индекс чанка включительно.</param>
    /// <param name="count">Верхняя граница (исключительно).</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task LoadChunkRangeAsync(int from, int count, CancellationToken ct)
    {
        var tasks = new Task[count - from];
        for (int i = from; i < count; i++)
        {
            // Стартовые чанки критичны
            tasks[i - from] = EnsureChunkAsync(i, ct, isCritical: true);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
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
    /// <remarks>
    /// <para><b>Self-Healing Pipeline:</b> Оборачивает чтение в try-catch для перехвата 
    /// <see cref="ParserCorruptionException"/>. Если парсер находит мусор, запускается хирургическое
    /// восстановление чанка из сети без прерывания воспроизведения.</para>
    /// </remarks>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        int maxHealingAttempts = 3;

        for (int attempt = 0; attempt < maxHealingAttempts; attempt++)
        {
            try
            {
                var frame = await _parser.ReadNextFrameAsync(ct).ConfigureAwait(false);
                if (frame != null)
                {
                    Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
                    UpdateCurrentChunk();
                }
                return frame;
            }
            catch (ParserCorruptionException ex)
            {
                // Запускаем процесс самовосстановления конвейера
                await HealCorruptionAsync(ex.AbsoluteBytePosition, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidDataException("Unrecoverable container corruption after max healing attempts.");
    }

    /// <summary>
    /// Стратегия исцеления. Если есть сеть — перекачивает чанк и заставляет парсер перечитать его.
    /// Если сети нет — помечает чанк как мертвый и перепрыгивает его, заставляя парсер сделать Resync.
    /// </summary>
    private async Task HealCorruptionAsync(long absoluteBytePosition, CancellationToken ct)
    {
        int chunkIndex = (int)(absoluteBytePosition / _chunkSize);
        Log.Warn($"[SelfHealing] Corruption detected at byte {absoluteBytePosition} (Chunk {chunkIndex})");

        if (_cacheEntry == null || _readStream == null || _parser == null) return;

        // 1. Инвалидируем битый чанк в кэше и RAM
        _cacheManager.InvalidateChunk(_cacheKey, chunkIndex);
        if (_ramChunks.TryRemove(chunkIndex, out var badChunk))
        {
            badChunk.Dispose();
        }

        // 2. Стратегия: Попытка экстренного онлайн-восстановления
        var healResult = await EnsureChunkAsync(chunkIndex, ct, isCritical: true).ConfigureAwait(false);

        if (healResult == ChunkDownloadResult.Success)
        {
            Log.Info($"[SelfHealing] Chunk {chunkIndex} healed seamlessly from network.");

            // Откатываем поток к началу исправленного чанка
            long safeStart = (long)chunkIndex * _chunkSize;
            _readStream.SeekAndCancelPendingReads(safeStart);

            // Говорим парсеру сбросить внутренние битые стейты и приготовиться читать чисто
            _parser.RequireResync();
        }
        else
        {
            // 3. Fallback: Оффлайн деградация
            Log.Warn($"[SelfHealing] Network unavailable. Marking chunk {chunkIndex} as dead for this session. Glitch expected.");
            _cacheEntry.MarkChunkCorruptedOffline(chunkIndex);

            // Перепрыгиваем "мертвую зону" к следующей границе чанка
            long nextChunkBoundary = (long)(chunkIndex + 1) * _chunkSize;

            if (nextChunkBoundary >= _contentLength)
            {
                _readStream.SeekAndCancelPendingReads(_contentLength); // Конец файла
            }
            else
            {
                _readStream.SeekAndCancelPendingReads(nextChunkBoundary);
                _parser.RequireResync(); // Парсер будет искать ближайший Cluster/moof
            }
        }
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
    /// Откладывает <see cref="IDisposable.Dispose"/> для
    /// <see cref="CancellationTokenSource"/> на указанный интервал.
    /// </summary>
    /// <remarks>
    /// <para><b>Почему dispose не сразу:</b></para>
    /// <para>После <see cref="CancellationTokenSource.Cancel"/> in-flight continuations
    /// ещё могут завершать обработку и обращаться к токену/связанным регистрациям.
    /// Немедленный <c>Dispose()</c> сужает race window и способен привести к
    /// <see cref="ObjectDisposedException"/> в конкурентных путях чтения/seek.</para>
    /// <para>Фоновая отложенная утилизация изолирована от UI-контекста и не требует
    /// синхронного ожидания завершения сетевых операций.</para>
    /// </remarks>
    /// <param name="cts">CTS для отложенного освобождения.</param>
    /// <param name="delayMs">Задержка перед dispose в миллисекундах.</param>
    private static void DeferDisposeCancellationTokenSource(CancellationTokenSource? cts, int delayMs)
    {
        if (cts == null) return;

        ThreadPool.UnsafeQueueUserWorkItem(static async state =>
        {
            var (source, delay) = ((CancellationTokenSource Source, int DelayMs))state!;

            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch
            {
                // Игнорируем: dispose best-effort.
            }

            try { source.Dispose(); }
            catch (ObjectDisposedException) { }
        }, (cts, delayMs));
    }

    /// <summary>
    /// Отменяет все загрузки текущей эпохи и создаёт новую.
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
                // Отменяем в ThreadPool, чтобы не задерживать конвейер
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { }
                }, oldCts);

                // Dispose откладывается: in-flight continuations ещё могут
                // завершать обработку отмены и держать связанные регистрации.
                DeferDisposeCancellationTokenSource(oldCts, DeferredEpochDisposeDelayMs);
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

    /// <summary>
    /// Мгновенно отменяет все активные операции чтения на потоке без уничтожения источника.
    /// Предотвращает мёртвые 600мс блокировки при быстрой смене эпох (rapid seeks).
    /// </summary>
    public void CancelActiveReads()
    {
        _readStream?.CancelActiveReads();
    }

    #endregion

    #region Public Buffer Management

    /// <inheritdoc/>
    public void ReleaseRamBuffers()
    {
        int current = Volatile.Read(ref _currentChunk);

        // Синхронизируем вытеснение с адаптивным окном, чтобы не удалить предзагруженное
        GetAdaptivePreloadParams(out _, out _, out int evictionDistance);

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

    /// <inheritdoc/>
    public void SetPlaybackActive(bool active)
    {
        if (_disposed) return;

        if (active)
            _playbackGate.Set();
        else
            _playbackGate.Reset();

        // НАМЕРЕННО не вызываем ResetDownloadEpoch здесь.
        // Epoch reset = только seek. Pause/Resume не меняют позицию потока.
        Log.Debug($"[CachingSource] Playback active state updated: {active}");
    }

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

    /// <summary>
    /// Общая преамбула dispose: снимает блокировки gates, отменяет epoch CTS и lifetime CTS.
    /// Единый источник истины для обоих путей Dispose / DisposeAsync.
    /// </summary>
    private void BeginDispose()
    {
        _suspendGate.Set();
        _playbackGate.Set();

        CancellationTokenSource? downloadCtsToDispose;

        lock (_epochLock)
        {
            downloadCtsToDispose = _downloadCts;
            _downloadCts = null;
        }

        if (downloadCtsToDispose != null)
        {
            try { downloadCtsToDispose.Cancel(); } catch (ObjectDisposedException) { }
            DeferDisposeCancellationTokenSource(downloadCtsToDispose, DeferredEpochDisposeDelayMs);
        }

        try { _lifetimeCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Общий эпилог dispose: освобождает lifetime CTS, read stream, RAM-чанки, семафоры, gates.
    /// Вызывается после ожидания preload task и dispose парсера.
    /// </summary>
    private void DisposeSharedResources()
    {
        try { _lifetimeCts?.Dispose(); } catch (ObjectDisposedException) { }

        _readStream?.Dispose();
        DisposeAllRamChunks();

        try { _refreshLock.Dispose(); } catch (ObjectDisposedException) { }
        try { _downloadSlots.Dispose(); } catch (ObjectDisposedException) { }

        _suspendGate.Dispose();
        _playbackGate.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();

        if (_preloadTask is { IsCompleted: false })
        {
            try { _preloadTask.Wait(TimeSpan.FromMilliseconds(PlaybackGateTimeoutMs)); }
            catch { }
        }

        _parser?.Dispose();
        DisposeSharedResources();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();

        if (_preloadTask != null)
        {
            try
            {
                await _preloadTask
                    .WaitAsync(TimeSpan.FromMilliseconds(PreloadTaskDisposeWaitTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        // Даём фоновым потокам завершить свои проверки на отмену.
        await Task.Delay(DisposalDelayMs).ConfigureAwait(false);

        if (_parser != null)
            await _parser.DisposeAsync().ConfigureAwait(false);

        DisposeSharedResources();
    }

    #endregion
}
