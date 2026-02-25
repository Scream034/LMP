using System.Collections.Concurrent;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник аудио с сегментным кэшированием и HTTP Range-request загрузкой.
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Данные загружаются чанками фиксированного размера (<see cref="ChunkSize"/>)</item>
///   <item>Чанки кэшируются в RAM (<see cref="_ramChunks"/>) и на диск (<see cref="_cacheManager"/>)</item>
///   <item>Фоновый preload loop обеспечивает опережающую загрузку</item>
///   <item>Seek реализован через epoch-based cancellation — все загрузки старой эпохи тихо умирают</item>
/// </list>
/// 
/// <para><b>Потокобезопасность:</b></para>
/// <list type="bullet">
///   <item>Публичные методы потокобезопасны</item>
///   <item><see cref="ReadAtAsync"/> может вызываться конкурентно из decoder loop</item>
///   <item>Seek отменяет текущие загрузки через epoch mechanism, не ломая decoder</item>
/// </list>
/// 
/// <para><b>Partial class structure:</b></para>
/// <list type="bullet">
///   <item><c>CachingStreamSource.cs</c> — ядро: поля, init, read frames, dispose</item>
///   <item><c>CachingStreamSource.Chunks.cs</c> — загрузка и управление чанками</item>
///   <item><c>CachingStreamSource.Seeking.cs</c> — seek с epoch-based cancellation</item>
///   <item><c>CachingStreamSource.Preload.cs</c> — фоновая загрузка и буферизация</item>
///   <item><c>CachingStreamSource.ReadStream.cs</c> — Stream-обёртка для парсеров</item>
/// </list>
/// </summary>
public sealed partial class CachingStreamSource : IAudioSource
{
    #region Fields

    // ── Identity ──
    private readonly string _cacheKey;
    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly AudioFormat _format;
    private readonly int _bitrate;

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
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads);

    // ── Epoch-based cancellation ──
    // 
    // При каждом Seek инкрементируется _downloadEpoch и пересоздаётся _downloadCts.
    // Все загрузки привязаны к текущей эпохе — при смене эпохи старые тихо умирают.
    // Это заменяет проблемный _seekCts, который не был связан с preload loop.
    //
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
    public long DownloadedBytes => (_cacheEntry?.DownloadedChunks ?? 0) * (long)ChunkSize;

    /// <summary>Битрейт (kbps).</summary>
    public int Bitrate => _cacheEntry?.Bitrate ?? _bitrate;

    #endregion

    #region Constructor

    /// <summary>
    /// Создаёт источник с кэширующим HTTP-стримингом.
    /// </summary>
    /// <param name="cacheKey">Уникальный ключ кэша (trackId + format + bitrate).</param>
    /// <param name="trackId">ID трека.</param>
    /// <param name="url">URL потока (может протухнуть → <paramref name="urlRefresher"/>).</param>
    /// <param name="contentLength">Размер файла в байтах (из Content-Length / clen).</param>
    /// <param name="format">Контейнерный формат (WebM, MP4, Ogg).</param>
    /// <param name="codec">Аудиокодек (Opus, AAC).</param>
    /// <param name="bitrate">Битрейт в kbps.</param>
    /// <param name="httpClient">HTTP клиент для загрузки.</param>
    /// <param name="cacheManager">Менеджер дискового кэша.</param>
    /// <param name="urlRefresher">Callback для обновления протухшего URL (403).</param>
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
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
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
            // Полный кэш — работаем офлайн
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
                chunkSize: ChunkSize);

            if (_cacheEntry.DownloadedChunks > 0)
            {
                Log.Info($"[CachingSource] Resuming: " +
                         $"{_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks} chunks");
            }

            // Создаём lifetime CTS и первую эпоху загрузки
            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            // Загружаем начальные чанки для парсинга заголовков
            await LoadInitialChunksAsync(_lifetimeCts.Token);

            // Создаём стрим-обёртку и парсер
            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            if (!await _parser.ParseHeadersAsync(ct))
                throw new InvalidOperationException("Failed to parse container headers");

            // Обновляем метаданные
            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;

            _initialized = true;

            // Запускаем фоновый preload
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
    /// Инициализация из полного дискового кэша (офлайн-режим).
    /// </summary>
    private async Task<bool> InitializeFromCacheAsync(CancellationToken ct)
    {
        var stream = _cacheManager.OpenCachedStream(_cacheKey);
        if (stream == null)
        {
            // Кэш повреждён — переключаемся на онлайн
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

    /// <summary>
    /// Создаёт парсер контейнера по формату.
    /// </summary>
    private IContainerParser CreateParser(Stream stream) => _format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };

    /// <summary>
    /// Создаёт первую эпоху загрузки, связанную с lifetime CTS.
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

    /// <summary>
    /// Загружает начальные чанки для парсинга заголовков контейнера.
    /// </summary>
    private async Task LoadInitialChunksAsync(CancellationToken ct)
    {
        if (_cacheEntry == null) return;

        int count = Math.Min(InitialChunksToLoad, _cacheEntry.TotalChunks);
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
            // Сетевой сбой — пробуем дозагрузить текущий чанк и повторить
            await EnsureChunkAsync(_currentChunk, ct);
            return await ReadFrameAsync(ct);
        }
    }

    /// <summary>
    /// Обновляет номер текущего чанка по позиции стрима.
    /// </summary>
    private void UpdateCurrentChunk()
    {
        if (!_isOfflineMode && _readStream != null)
            _currentChunk = (int)(_readStream.Position / ChunkSize);
    }

    #endregion

    #region Epoch-Based Cancellation

    /// <summary>
    /// Отменяет все загрузки текущей эпохи и создаёт новую.
    /// Вызывается при каждом Seek.
    /// </summary>
    /// <returns>CancellationToken новой эпохи для запуска новых загрузок.</returns>
    /// <remarks>
    /// <para>Механизм работы:</para>
    /// <list type="number">
    ///   <item>Создаём новый CTS, связанный с lifetime</item>
    ///   <item>Инкрементируем epoch</item>
    ///   <item>Отменяем и dispose'им старый CTS</item>
    /// </list>
    /// <para>
    /// Порядок важен: новый CTS создаётся ДО отмены старого,
    /// чтобы <see cref="CurrentDownloadToken"/> никогда не возвращал отменённый токен.
    /// </para>
    /// </remarks>
    private CancellationToken ResetDownloadEpoch()
    {
        lock (_epochLock)
        {
            var oldCts = _downloadCts;

            // Новый CTS, связанный с lifetime
            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();

            Interlocked.Increment(ref _downloadEpoch);

            // Отменяем и dispose'им старый ПОСЛЕ создания нового
            try { oldCts?.Cancel(); }
            catch (ObjectDisposedException) { }

            try { oldCts?.Dispose(); }
            catch (ObjectDisposedException) { }

            return _downloadCts.Token;
        }
    }

    /// <summary>
    /// Возвращает CancellationToken текущей эпохи загрузки.
    /// </summary>
    /// <remarks>
    /// Используется фоновыми загрузками для привязки к текущей эпохе.
    /// При смене эпохи (seek) все загрузки с токеном старой эпохи получат отмену.
    /// </remarks>
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
                     .Where(i => Math.Abs(i - current) > RamEvictionDistance)
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

        // Отменяем все эпохи
        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); }
            catch (ObjectDisposedException) { }

            try { _downloadCts?.Dispose(); }
            catch (ObjectDisposedException) { }

            _downloadCts = null;
        }

        // Отменяем lifetime
        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        try { _lifetimeCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        _parser?.Dispose();
        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Отменяем все эпохи
        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); }
            catch (ObjectDisposedException) { }

            try { _downloadCts?.Dispose(); }
            catch (ObjectDisposedException) { }

            _downloadCts = null;
        }

        // Отменяем lifetime
        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        // Ждём preload loop
        if (_preloadTask != null)
        {
            try { await _preloadTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { /* Timeout or cancelled — OK */ }
        }

        try { _lifetimeCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        if (_parser != null)
            await _parser.DisposeAsync();

        _refreshLock.Dispose();
        _readStream?.Dispose();
        _ramChunks.Clear();
        _downloadSlots.Dispose();
    }

    #endregion
}