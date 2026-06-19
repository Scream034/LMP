using System.Collections.Concurrent;
using System.Threading.Channels;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Normalization;
using LMP.Core.Exceptions;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Services;

/// <summary>
/// Центральный движок аудио воспроизведения.
/// Координирует AudioPlayer, очередь треков, громкость и UI события.
/// </summary>
/// <remarks>
/// <para>Освобожден от наследования UI-класса ViewModelBase для строгого разделения слоев Core и UI </para>
/// </remarks>
public sealed partial class AudioEngine : ReactiveObject, ISuspendable, IDisposable, IAsyncDisposable
{
    #region Engine Command Types

    /// <summary>Маркерный интерфейс для typed commands AudioEngine.</summary>
    private interface IEngineCommand { }

    /// <summary>Воспроизвести конкретный трек с опциональной позиции (для бесшовного восстановления).</summary>
    /// <param name="Track">Информация о треке.</param>
    /// <param name="Session">ID сессии для отмены устаревших команд.</param>
    /// <param name="SeekPosition">Позиция для старта воспроизведения.</param>
    /// <param name="IsRetry">Флаг автоматического перезапуска при ошибке кэша.</param>
    private sealed record PlayTrackCommand(
        TrackInfo Track,
        int Session,
        TimeSpan? SeekPosition = null,
        bool IsRetry = false) : IEngineCommand;

    /// <summary>Запустить очередь с указанного трека.</summary>
    private sealed record StartQueueCommand(IEnumerable<TrackInfo> Tracks, TrackInfo StartTrack, int Session) : IEngineCommand;

    /// <summary>Воспроизвести текущий индекс очереди.</summary>
    private sealed record PlayCurrentIndexCommand(int Session) : IEngineCommand;

    /// <summary>Навигация вперёд/назад.</summary>
    private sealed record NavigateCommand(bool Forward, bool UserInitiated) : IEngineCommand;

    #endregion

    #region Constants

    private const int CommandQueueCapacity = 32;

    /// <summary>Базовый диапазон громкости (0-200 = 0-100% без boost).</summary>
    public const int VolumeNormalRange = 200;

    /// <summary>Максимальный gain (защита от перегрузки).</summary>
    public const float MaxGain = 4.0f;

    private const int QualitySwitchCooldownMs = 2000;

    /// <summary>
    /// Целевой объём локального contiguous префикса для Partial Cache Fast Start.
    /// </summary>
    private const int PartialCacheBootstrapTargetMs = 12_000;

    /// <summary>
    /// Нижняя граница contiguous bootstrap-префикса в байтах.
    /// </summary>
    private const int PartialCacheBootstrapMinBytes = 96 * 1024;

    /// <summary>
    /// Верхняя граница contiguous bootstrap-префикса в байтах.
    /// </summary>
    private const int PartialCacheBootstrapMaxBytes = 384 * 1024;

    /// <summary>
    /// Максимальное число автоматических попыток восстановления после
    /// recoverable <see cref="CacheInvalidatedException"/> для одного трека.
    /// </summary>
    private const int MaxCacheAutoRetries = 2;

    #endregion

    #region Dependencies

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly AudioPlayer _player;
    private readonly TrackRegistry _trackRegistry; // Добавлено внедрение зависимости L1-кэша

    #endregion

    #region Synchronization

    private readonly Channel<IEngineCommand> _commandQueue;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Lock _queueLock = new();

    private SessionGuard _session;
    private CancellationTokenSource? _sessionCts;
    private readonly Lock _sessionLock = new();

    /// <summary>
    /// Task цикла обработки команд (<see cref="ProcessCommandsAsync"/>).
    /// Сохраняется для детерминированного ожидания при dispose:
    /// без ожидания возможна гонка — команда уже извлечена из канала,
    /// но handler ещё не завершил мутацию состояния плеера.
    /// </summary>
    private Task? _commandProcessorTask;

    /// <summary>
    /// Task цикла сохранения громкости (<see cref="VolumeSaveLoopAsync"/>).
    /// Ожидается при dispose чтобы гарантировать flush последнего pending-write
    /// в персистентное хранилище до закрытия БД.
    /// </summary>
    private Task? _volumeSaveTask;

    /// <summary>
    /// Единственная активная задача подготовки воспроизведения.
    /// Гарантирует single-flight: при поступлении нового PlayTrackCoreAsync
    /// предыдущая задача отменяется через session CTS, и новая ожидается actor'ом.
    /// Хранится для детерминированного ожидания — исключает overlap
    /// нескольких параллельных ResolveStreamUrlAsync / _player.PlayAsync.
    /// </summary>
    private Task? _activePlayTask;

    #endregion

    #region Playback State

    private volatile bool _isSuspended;
    private DateTime _lastQualitySwitchTime = DateTime.MinValue;
    private string? _nTokenActiveTrackId;
    private string? _nTokenWarnedTrackId;
    private string? _sealedFailedTrackId;
    private volatile bool _isManualLoading;

    /// <summary>Очередь отложенных записей gain нормализации в БД.</summary>
    private readonly ConcurrentQueue<(string TrackId, float Gain)> _pendingGainWrites = new();

    /// <summary>
    /// Флаг завершённого dispose. Предотвращает double-dispose
    /// при вызове обоих путей (<see cref="DisposeAsync"/> и <see cref="Dispose(bool)"/>).
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Счётчик автоматических retry для текущего трека при recoverable cache ошибках.
    /// Сбрасывается при смене трека в <see cref="HandlePlayTrackAsync"/>
    /// и <see cref="HandleStartQueueAsync"/>.
    /// </summary>
    private int _cacheRetryCount;

    /// <summary>
    /// Признак того, что активный <see cref="Audio.Sources.CachingStreamSource"/>
    /// был реально приостановлен lifecycle-политикой движка.
    /// </summary>
    /// <remarks>
    /// Нужен для симметричного Resume: если suspend был пропущен из-за активного playback,
    /// нельзя безусловно дергать <c>Resume()</c> при возврате окна в активное состояние.
    /// </remarks>
    private int _sourceLifecycleSuspended;

    #endregion

    #region Observable Properties

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public AudioStreamInfo StreamInfo { get; private set; } = AudioStreamInfo.Empty;

    public bool IsPlaying => _player.State == PlaybackState.Playing;
    public bool IsPaused => _player.State == PlaybackState.Paused;

    /// <summary>
    /// Возвращает true, если плеер выполняет буферизацию, загрузку или находится в процессе перемещения.
    /// </summary>
    public bool IsLoading => _isManualLoading || _player.State is PlaybackState.Loading or PlaybackState.Buffering || _player.DetailedState == PlayerState.Seeking;

    public int CurrentQueueIndex => Volatile.Read(ref _currentIndex);
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    public TimeSpan CurrentPosition => _player.Position;
    public TimeSpan TotalDuration => _player.Duration;
    public double BufferProgress => _player.BufferProgress;
    public bool IsFullyBuffered => _player.IsFullyBuffered;

    /// <summary>Текущий gain после volume curve, boost и dB-коррекции.</summary>
    public float CurrentGain => _currentGain;

    #endregion

    #region Events

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<TimeSpan>? OnSeekCompleted;
    public event Action<bool, bool>? OnPlaybackStateChanged;
    public event Action? OnQueueChanged;
    public event Action<bool>? OnLoadingStateChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action<AudioStreamInfo>? OnStreamInfoChanged;
    public event Action<BufferState>? OnBufferStateChanged;
    public event Action? OnDeviceLost;
    public event Action? OnDeviceRestored;
    public event Action<Exception>? OnErrorOccurred;
    public event Action<NTokenWarningInfo>? OnNTokenDecryptionWarning;

    /// <summary>Контекст предупреждения о n-токене.</summary>
    public readonly record struct NTokenWarningInfo(TrackInfo? Track, bool WasSkipped);

    private readonly Action<TimeSpan> _positionChangedHandler;
    private readonly Action _raisePositionChangedOnUIDelegate;
    private long _currentPositionTicks;

    private readonly Action<BufferState> _bufferStateChangedHandler;
    private readonly Action _raiseBufferStateOnUIDelegate;
    private BufferState _currentBufferState;
    private readonly Lock _bufferStateLock = new();

    private readonly Action<TimeSpan> _seekCompletedHandler;
    private readonly Action _raiseSeekCompletedOnUIDelegate;
    private long _seekCompletedTicks;

    private readonly Action _deviceLostHandler;
    private readonly Action _raiseDeviceLostOnUIDelegate;

    private readonly Action _deviceRestoredHandler;
    private readonly Action _raiseDeviceRestoredOnUIDelegate;

    #endregion

    #region Constructor

    /// <summary>
    /// Инициализирует центральный движок воспроизведения.
    /// </summary>
    public AudioEngine(YoutubeProvider youtube, LibraryService library, TrackRegistry trackRegistry)
    {
        _youtube = youtube;
        _library = library;
        _trackRegistry = trackRegistry;

        ApplyStreamingProfile();

        // Настройка делегатов один раз при создании класса. 
        // Исключает аллокацию замыканий в куче (Gen 0) во время проигрывания.
        _positionChangedHandler = HandlePositionChangedInternal;
        _raisePositionChangedOnUIDelegate = () => OnPositionChanged?.Invoke(TimeSpan.FromTicks(Volatile.Read(ref _currentPositionTicks)));

        _bufferStateChangedHandler = HandleBufferStateChangedInternal;
        _raiseBufferStateOnUIDelegate = () => OnBufferStateChanged?.Invoke(GetLatestBufferState());

        _seekCompletedHandler = HandleSeekCompletedInternal;
        _raiseSeekCompletedOnUIDelegate = () => OnSeekCompleted?.Invoke(TimeSpan.FromTicks(Volatile.Read(ref _seekCompletedTicks)));

        _deviceLostHandler = HandleDeviceLostInternal;
        _raiseDeviceLostOnUIDelegate = () => OnDeviceLost?.Invoke();

        _deviceRestoredHandler = HandleDeviceRestoredInternal;
        _raiseDeviceRestoredOnUIDelegate = () => OnDeviceRestored?.Invoke();

        _player = new AudioPlayer(new AudioPlayerOptions
        {
            UrlRefreshCallback = RefreshUrlCallbackAsync,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(500), // Частота рассылки события снижена до 500мс для разгрузки UI-потока. Плавность слайдера обеспечивается экстраполяцией.
            MaxRetryAttempts = 3,
            UseNullBackend = false,
            OnPipelineConfiguring = ConfigurePipelineBeforeStart,
            OnGainLocked = HandleGainLocked
        });

        SubscribeToPlayerEvents();
        _youtube.OnNTokenDecryptionStarted += HandleNTokenDecryptionStarted;
        InitializeFromSettings();

        _commandQueue = Channel.CreateBounded<IEngineCommand>(
            new BoundedChannelOptions(CommandQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _commandProcessorTask = Task.Run(ProcessCommandsAsync);
        _volumeSaveTask = Task.Run(VolumeSaveLoopAsync);

        // Внедряем регистрацию службы в реестре жизненного цикла  [2]
        LifecycleRegistry.Instance?.RegisterBackgroundSuspendable(this);
    }

    /// <summary>
    /// Конфигурирует pipeline перед открытием gate: громкость, нормализация и кроссфейдер.
    /// </summary>
    private void ConfigurePipelineBeforeStart(AudioPipeline pipeline, string? trackId)
    {
        float volumeGain = ComputeFinalGain();
        _currentGain = volumeGain;
        _player.SetVolumeGain(volumeGain);

        var audioSettings = _library.Settings.Audio;
        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode);

        pipeline.Analyzer.Configure(normConfig);

        Log.Debug($"[AudioEngine] Configuring pipeline for '{trackId}'. Normalization: {normConfig.Enabled}, Mode: {normConfig.Mode}");

        if (normConfig.Enabled && !string.IsNullOrEmpty(trackId))
        {
            var registryTrack = _trackRegistry.TryGet(trackId) ?? _library.GetTrack(trackId);
            var currentTrack = CurrentTrack;

            var track = registryTrack ?? (currentTrack?.Id == trackId ? currentTrack : null);

            if (track != null)
            {
                float cachedGain = NormalizationGainResolver.Resolve(track, normConfig);

                // Детальная телеметрия для отладки pre-scan
                Log.Debug($"[AudioEngine] Track resolved: ID={track.Id}, Title='{track.Title}' " +
                          $"| Source: {(registryTrack != null ? "Registry" : "CurrentTrackFallback")} " +
                          $"| DB Cached Gain: {(float.IsNaN(track.CachedNormalizationGain) ? "NaN" : track.CachedNormalizationGain.ToString("F4"))} " +
                          $"| YT Loudness: {(float.IsNaN(track.YoutubeIntegratedLoudnessDb) ? "NaN" : track.YoutubeIntegratedLoudnessDb.ToString("F2") + "dB")} " +
                          $"| Resolved Gain: {(float.IsNaN(cachedGain) ? "NaN" : cachedGain.ToString("F4"))}");

                if (!float.IsNaN(cachedGain))
                {
                    pipeline.Analyzer.LockFromCachedGain(cachedGain);
                    Log.Info($"[AudioEngine] Normalization gain locked from cache: {cachedGain:F4}x for {trackId}");
                }
                else
                {
                    Log.Warn($"[AudioEngine] Normalization resolver returned NaN for {trackId}. EBU R128 Pre-scan is REQUIRED.");
                }
            }
            else
            {
                Log.Error($"[AudioEngine] ⚠ FAILED to resolve TrackInfo for '{trackId}' during pipeline configuration. Pre-scan will be triggered.");
            }
        }

        pipeline.SnapCrossfaderToGain();
    }

    private void SubscribeToPlayerEvents()
    {
        _player.Events.PositionChanged += _positionChangedHandler;
        _player.Events.StateChanged += HandlePlayerStateChanged;
        _player.Events.TrackEnded += HandlePlayerTrackEnded;
        _player.Events.StreamInfoChanged += HandleStreamInfoChanged;
        _player.Events.BufferStateChanged += _bufferStateChangedHandler;
        _player.Events.SeekCompleted += _seekCompletedHandler;
        _player.Events.DeviceLost += _deviceLostHandler;
        _player.Events.DeviceRestored += _deviceRestoredHandler;

        _player.Events.ErrorOccurred += err =>
        {
            if (CancellationHelper.IsCancellationLike(err.Exception)) return;
            if (err.Exception is AudioSourceException && CancellationHelper.IsCancellationLike(err.Exception?.InnerException)) return;

            var ex = err.Exception;
            if (ex is AudioDeviceException)
            {
                RaiseError(new AudioDeviceException(err.Message, ex?.InnerException));
            }
            else if (ex is CacheInvalidatedException cacheEx)
            {
                HandleCacheInvalidated(cacheEx);
            }
            else
            {
                RaiseError(new AudioException(err.Message, ex));
            }
        };
    }

    private void InitializeFromSettings()
    {
        var settings = _library.Settings;
        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;
        _volumePercent = settings.Volume > 0 ? (int)settings.Volume : 60;
        ApplyGainToPipeline();
    }

    private void ApplyStreamingProfile()
    {
        AudioSourceFactory.ApplyInternetProfile(_library.Settings.InternetProfile);
    }

    #endregion

    #region Event Handlers

    private void HandlePositionChangedInternal(TimeSpan pos)
    {
        Volatile.Write(ref _currentPositionTicks, pos.Ticks);
        RaiseOnUI(_raisePositionChangedOnUIDelegate);
    }

    private void HandleBufferStateChangedInternal(BufferState state)
    {
        lock (_bufferStateLock)
        {
            _currentBufferState = state;
        }
        RaiseOnUI(_raiseBufferStateOnUIDelegate);
    }

    private BufferState GetLatestBufferState()
    {
        lock (_bufferStateLock)
        {
            return _currentBufferState;
        }
    }

    private void HandleSeekCompletedInternal(TimeSpan t)
    {
        Volatile.Write(ref _seekCompletedTicks, t.Ticks);
        RaiseOnUI(_raiseSeekCompletedOnUIDelegate);
    }

    private void HandleDeviceLostInternal()
    {
        RaiseOnUI(_raiseDeviceLostOnUIDelegate);
    }

    private void HandleDeviceRestoredInternal()
    {
        RaiseOnUI(_raiseDeviceRestoredOnUIDelegate);
    }

    #endregion

    #region Session Management

    private int BeginNewSession()
    {
        int session = _session.BeginNew();
        lock (_sessionLock)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        }
        return session;
    }

    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock) return _sessionCts?.Token ?? _lifetimeCts.Token;
    }

    #endregion

    #region Failure Barrier

    private bool IsSealedFailedTrack(string? trackId)
    {
        var sealed_ = Interlocked.CompareExchange(ref _sealedFailedTrackId, null, null);
        return !string.IsNullOrEmpty(trackId) && !string.IsNullOrEmpty(sealed_)
            && string.Equals(sealed_, trackId, StringComparison.Ordinal);
    }

    private void ResetSealedFailedTrack() => Interlocked.Exchange(ref _sealedFailedTrackId, null);

    private void SealFailedTrack(string? trackId)
    {
        if (!string.IsNullOrEmpty(trackId))
            Interlocked.Exchange(ref _sealedFailedTrackId, trackId);
    }

    private void AbortCurrentTrackPlaybackAfterFatalError(string? trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        SealFailedTrack(trackId);

        if (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal)) return;

        lock (_queueLock)
        {
            if (_queue.Count <= 1 && _currentIndex >= 0 && _currentIndex < _queue.Count
                && string.Equals(_queue[_currentIndex].Id, trackId, StringComparison.Ordinal))
                _currentIndex = -1;
        }

        BeginNewSession();
        _player.Stop();
    }

    /// <summary>
    /// Сбрасывает и останавливает воспроизведение при возникновении критической ошибки.
    /// </summary>
    public void StopAfterFatalPlaybackError()
    {
        AbortCurrentTrackPlaybackAfterFatalError(CurrentTrack?.Id);

        CurrentTrack = null;
        StreamInfo = AudioStreamInfo.Empty;

        RaiseOnUI(() =>
        {
            OnTrackChanged?.Invoke(null);
            OnPositionChanged?.Invoke(TimeSpan.Zero);
            OnPlaybackStateChanged?.Invoke(false, false);
            OnLoadingStateChanged?.Invoke(false);
        });
    }

    #endregion

    #region Command Processing

    /// <summary>
    /// Единый цикл обработки typed commands.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (cmd)
                    {
                        case PlayTrackCommand play:
                            await HandlePlayTrackAsync(play).ConfigureAwait(false);
                            break;

                        case StartQueueCommand start:
                            await HandleStartQueueAsync(start).ConfigureAwait(false);
                            break;

                        case PlayCurrentIndexCommand pci:
                            await PlayCurrentIndexAsync(pci.Session).ConfigureAwait(false);
                            break;

                        case NavigateCommand nav:
                            await HandleNavigateAsync(nav).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn($"[AudioEngine] Command error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Отправляет typed command в очередь.</summary>
    private void EnqueueCommand(IEngineCommand command)
    {
        _commandQueue.Writer.TryWrite(command);
    }

    #endregion

    #region Internal Playback

    /// <summary>Вызывается при фиксации gain нормализации.</summary>
    private void HandleGainLocked(string trackId, float gain)
    {
        var canonical = _library.GetTrack(trackId);
        canonical?.SetGain(gain);

        var current = CurrentTrack;
        if (current != null && current.Id == trackId && !ReferenceEquals(current, canonical))
            current.SetGain(gain);

        _pendingGainWrites.Enqueue((trackId, gain));
    }

    /// <summary>
    /// Запускает воспроизведение трека по текущему индексу очереди с опциональной позиции.
    /// </summary>
    private async Task PlayCurrentIndexAsync(int session, TimeSpan? seekPosition = null)
    {
        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
            track = _queue[_currentIndex];
        }

        if (track == null || IsSealedFailedTrack(track.Id)) return;

        var previousTask = Volatile.Read(ref _activePlayTask);
        if (previousTask is { IsCompleted: false })
        {
            try { await previousTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        if (_session.IsStale(session)) return;

        // Передаем seekPosition в задачу подготовки
        var playTask = PlayTrackCoreAsync(track, session, GetSessionToken(), seekPosition);
        Volatile.Write(ref _activePlayTask, playTask);

        try
        {
            await playTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>
    /// Основной метод подготовки и запуска воспроизведения трека с поддержкой SeekPosition.
    /// </summary>
    private async Task PlayTrackCoreAsync(TrackInfo track, int session, CancellationToken ct, TimeSpan? seekPosition = null)
    {
        if (_session.IsStale(session) || IsSealedFailedTrack(track.Id)) return;

        Log.Debug($"[AudioEngine] [PlayTrackCore] Initiating playback for track: {track.Id} ('{track.Title}') | Session: {session}");

        _player.Stop();
        if (_session.IsStale(session) || IsSealedFailedTrack(track.Id)) return;

        SetManualLoading(true);

        try
        {
            var canonical = await _library.GetTrackAsync(track.Id, ct).ConfigureAwait(false);
            if (canonical != null)
            {
                Log.Debug($"[AudioEngine] [PlayTrackCore] Track {track.Id} found in DB. Upgrading metadata. Saved DB Gain: {(float.IsNaN(canonical.CachedNormalizationGain) ? "NaN" : canonical.CachedNormalizationGain.ToString("F4"))}");
                canonical.UpdateMetadata(track);
                track = canonical;
            }
            else
            {
                Log.Debug($"[AudioEngine] [PlayTrackCore] Track {track.Id} NOT found in DB. Registering in TrackRegistry L1 cache.");
                track = _trackRegistry.RegisterOrUpdate(track);
            }

            CurrentTrack = track;
            StreamInfo = AudioStreamInfo.Empty;

            RaiseOnUI(() =>
            {
                OnTrackChanged?.Invoke(track);
                OnPositionChanged?.Invoke(TimeSpan.Zero);
            });

            ct.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _nTokenActiveTrackId, track.Id);
            Interlocked.Exchange(ref _nTokenWarnedTrackId, null);

            if (track.HasCachedNormalizationGain)
            {
                Log.Debug($"[AudioEngine] [PlayTrackCore] Queuing initial cached gain write: {track.CachedNormalizationGain:F4}x for {track.Id}");
                _pendingGainWrites.Enqueue((track.Id, track.CachedNormalizationGain));
            }

            string streamUrl = "";
            int bitrateHint = 0;
            const int maxStartupAttempts = 3;

            // Локальный цикл повторных попыток для защиты от транзиентных отмен (например, при перезагрузке Auth-клиента на старте)
            for (int attempt = 1; attempt <= maxStartupAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var resolved = await Task.Run(
                        () => ResolveStreamUrlAsync(track, ct, seekPosition), ct).ConfigureAwait(false);

                    streamUrl = resolved.Url;
                    bitrateHint = resolved.Bitrate;

                    if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id)) return;

                    await _player.PlayAsync(streamUrl, track.Id, bitrateHint, ct, seekPosition: seekPosition).ConfigureAwait(false);
                    break; // Успешно запустились, выходим из цикла попыток
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // Сессия отменена пользователем, пробрасываем выше для тихого выхода
                }
                catch (OperationCanceledException ex) when (attempt < maxStartupAttempts)
                {
                    Log.Warn($"[AudioEngine] Transient cancellation during track startup (attempt {attempt}/{maxStartupAttempts}), retrying in 150ms: {ex.Message}");
                    _player.Stop();
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
            }

            ApplyGainToPipeline();
            ApplyLifecycleSourceSuspendPolicy();
        }
        catch (OperationCanceledException ex)
        {
            // Если сессионный токен отменен — выходим молча. 
            // Если транзиентная отмена исчерпала лимит попыток, останавливаем плеер, но НЕ блокируем трек в IsSealedFailedTrack.
            if (!ct.IsCancellationRequested)
            {
                Log.Warn($"[AudioEngine] Playback startup aborted due to exhausted transient cancellations: {ex.Message}");
                _player.Stop();
                RaiseError(ex);
            }
        }
        // Предупреждение CS0168 устранено (ex убран)
        catch (Exception) when (_session.IsStaleOrCancelled(session, ct)) { }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(track.Id);
            RaiseError(ex);
        }
        finally
        {
            SetManualLoading(false);
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, track.Id);
        }
    }

    private async ValueTask<string?> RefreshUrlCallbackAsync(string trackId, CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId)) return null;
        var track = await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);
        if (track == null || IsSealedFailedTrack(trackId)) return null;

        var sessionToken = GetSessionToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);
        try
        {
            var info = await Task.Run(
                () => _youtube.RefreshStreamUrlAsync(track, true, linked.Token), linked.Token).ConfigureAwait(false);
            return info?.Url;
        }
        // Предупреждение CS0168 устранено (ex убран)
        catch (Exception) when (linked.IsCancellationRequested || sessionToken.IsCancellationRequested
            || !string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(trackId);
            RaiseError(ex);
            return null;
        }
    }

    /// <summary>
    /// Вычисляет минимальный объём contiguous local prefix, достаточный
    /// для Partial Cache Fast Start.
    /// </summary>
    /// <param name="bitrateKbps">Битрейт потока в kbps.</param>
    private static int ComputePartialCacheBootstrapBytes(int bitrateKbps)
    {
        double bitrateBytesPerSec = Math.Max(1, bitrateKbps) * 1000.0 / 8.0;
        int bytes = (int)Math.Ceiling(bitrateBytesPerSec * PartialCacheBootstrapTargetMs / 1000.0);
        return Math.Clamp(bytes, PartialCacheBootstrapMinBytes, PartialCacheBootstrapMaxBytes);
    }

    /// <summary>
    /// Пытается подобрать лучший partial cache для fast-start с позиции начала трека.
    /// </summary>
    /// <param name="track">Трек.</param>
    /// <param name="seekPosition">
    /// Позиция seek-before-play. Если указана и не равна нулю, partial fast-start отключается,
    /// чтобы не стартовать с неподходящего локального префикса.
    /// </param>
    private static CacheEntry? TryGetPartialBootstrapCache(TrackInfo track, TimeSpan? seekPosition)
    {
        if (seekPosition is { TotalMilliseconds: > 0 })
            return null;

        var cacheManager = AudioSourceFactory.GlobalCache;
        if (cacheManager == null)
            return null;

        int bitrateHint = track.TransientBitrate > 0
            ? track.TransientBitrate
            : track.CachedBitrate > 0
                ? track.CachedBitrate
                : 160;

        int requiredBytes = ComputePartialCacheBootstrapBytes(bitrateHint);
        return cacheManager.FindBestStartupCache(track.Id, requiredBytes);
    }

    /// <summary>
    /// Пытается прикрепить уже подготовленный continuation URL к активному source.
    /// </summary>
    /// <param name="track">Текущий трек.</param>
    /// <param name="url">Финальный stream URL.</param>
    private void TryAttachPrimedContinuationUrlToActiveSource(TrackInfo track, string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
            return;

        var current = CurrentTrack;
        if (current == null || !string.Equals(current.Id, track.Id, StringComparison.Ordinal))
            return;

        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is not Audio.Sources.CachingStreamSource cachingSource)
            return;

        if (cachingSource.TryAttachContinuationUrl(url))
        {
            Log.Debug($"[AudioEngine] Primed continuation URL attached to live source: {track.Id}");
        }
    }

    /// <summary>
    /// Асинхронно подготавливает continuation URL в фоне, не блокируя старт playback.
    /// </summary>
    /// <param name="track">Текущий трек.</param>
    /// <param name="ct">Токен текущей playback session.</param>
    private async Task PrimeContinuationUrlAsync(TrackInfo track, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || IsSealedFailedTrack(track.Id))
                return;

            if (!string.IsNullOrEmpty(track.StreamUrl))
            {
                TryAttachPrimedContinuationUrlToActiveSource(track, track.StreamUrl);
                return;
            }

            var info = await _youtube.RefreshStreamUrlAsync(track, false, ct).ConfigureAwait(false);
            if (info is null || string.IsNullOrEmpty(info.Value.Url))
                return;

            track.StreamUrl = info.Value.Url;
            track.TransientBitrate = info.Value.Bitrate;
            track.TransientSize = info.Value.Size;
            track.TransientContainer = info.Value.Container;
            track.CachedCodec = info.Value.Codec;
            track.CachedBitrate = info.Value.Bitrate;
            track.CachedContainer = info.Value.Container;
            track.IsHlsOnly = false;
            track.HlsManifestUrl = null;

            TryAttachPrimedContinuationUrlToActiveSource(track, info.Value.Url);

            Log.Info($"[AudioEngine] Partial-cache continuation primed: {track.Id} " +
                     $"({info.Value.Codec}/{info.Value.Bitrate}kbps)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Continuation priming skipped for {track.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Разрешает startup-источник трека: full cache → partial fast-start → сохранённый URL → YouTube API.
    /// </summary>
    private async Task<(string Url, long Size, int Bitrate)> ResolveStreamUrlAsync(
        TrackInfo track,
        CancellationToken ct,
        TimeSpan? seekPosition = null)
    {
        var rawId = track.GetRawIdSpan().ToString();

        var fullCache = AudioSourceFactory.FindAnyCachedTrack(track.Id)
                     ?? (rawId != track.Id ? AudioSourceFactory.FindAnyCachedTrack(rawId) : null);

        if (fullCache != null)
        {
            track.TransientBitrate = fullCache.Value.Entry.Bitrate;

            if (float.IsNaN(track.CachedNormalizationGain) && float.IsNaN(track.YoutubeIntegratedLoudnessDb))
            {
                var profileStr = _library.Settings.InternetProfile.ToString();
                bool isDataSaving = profileStr.Contains("Cellular", StringComparison.OrdinalIgnoreCase) ||
                                    profileStr.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
                                    profileStr.Contains("Low", StringComparison.OrdinalIgnoreCase);

                if (!isDataSaving)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(1.2));

                    try
                    {
                        Log.Debug($"[AudioEngine] Local cache for {track.Id} has no gain metadata. " +
                                  "Performing fast online loudness lookup...");
                        float loudness = await _youtube.GetLoudnessDbOnlyAsync(rawId, timeoutCts.Token).ConfigureAwait(false);

                        if (!float.IsNaN(loudness))
                        {
                            track.TrySetGainFromLoudness(loudness);
                            Log.Info($"[AudioEngine] Resolved online loudness for cached track {track.Id}: {loudness:F2}dB");
                        }
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        Log.Warn($"[AudioEngine] Loudness lookup for cached track {track.Id} timed out. Local pre-scan fallback will be used.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[AudioEngine] Loudness lookup failed for {track.Id}: {ex.Message}. Local pre-scan fallback will be used.");
                    }
                }
            }

            return ("", fullCache.Value.Entry.TotalSize, fullCache.Value.Entry.Bitrate);
        }

        // Partial Cache Fast Start:
        // если есть достаточный локальный contiguous prefix от начала трека,
        // стартуем playback немедленно из cache, а continuation URL готовим в фоне.
        var bootstrapCache = TryGetPartialBootstrapCache(track, seekPosition);
        if (bootstrapCache != null)
        {
            track.TransientBitrate = bootstrapCache.Bitrate;
            track.CachedCodec = bootstrapCache.Codec.ToString();
            track.CachedBitrate = bootstrapCache.Bitrate;
            track.CachedContainer = bootstrapCache.Format.ToString();
            track.TransientContainer = bootstrapCache.Format.ToString();
            track.TransientSize = bootstrapCache.TotalSize;

            _ = PrimeContinuationUrlAsync(track, ct);

            Log.Info($"[AudioEngine] Partial-cache fast start: {track.Id} " +
                     $"(prefix={bootstrapCache.GetContiguousDownloadedBytesFrom(0) / 1024}KB, " +
                     $"downloaded={bootstrapCache.DownloadedBytes / 1024}KB/{bootstrapCache.TotalSize / 1024}KB)");

            return ("", bootstrapCache.TotalSize, bootstrapCache.Bitrate);
        }

        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(track.StreamUrl))
            return (track.StreamUrl, track.TransientSize, track.TransientBitrate);

        var info = await _youtube.RefreshStreamUrlAsync(track, false, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

        track.TransientBitrate = info.Bitrate;

        if (!string.IsNullOrEmpty(info.Url))
            track.StreamUrl = info.Url;

        return (info.Url ?? "", info.Size, info.Bitrate);
    }

    #endregion

    #region Event Handlers

    private void HandlePlayerStateChanged(PlaybackState state)
    {
        ApplyLifecycleSourceSuspendPolicy();

        RaiseOnUI(() =>
        {
            this.RaisePropertyChanged(nameof(IsPlaying));
            this.RaisePropertyChanged(nameof(IsPaused));
            this.RaisePropertyChanged(nameof(IsLoading));
            this.RaisePropertyChanged(nameof(TotalDuration));
            OnPlaybackStateChanged?.Invoke(state == PlaybackState.Playing, state == PlaybackState.Paused);
            OnLoadingStateChanged?.Invoke(IsLoading);
        });
    }

    /// <summary>
    /// Обработчик естественного завершения трека.
    /// Маршрутизируется через typed command для соблюдения actor invariant.
    /// </summary>
    private void HandlePlayerTrackEnded()
    {
        if (_player.State is PlaybackState.Loading or PlaybackState.Buffering) return;
        EnqueueCommand(new NavigateCommand(Forward: true, UserInitiated: false));
    }

    private void HandleStreamInfoChanged(AudioStreamInfo info)
    {
        RaiseOnUI(() => { StreamInfo = info; OnStreamInfoChanged?.Invoke(info); });
    }

    #endregion

    #region Playback Control

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        EnqueueCommand(new PlayTrackCommand(track, session, null)); // Обычный запуск с начала
        return Task.CompletedTask;
    }

    public Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        EnqueueCommand(new StartQueueCommand(tracks, startTrack, session));
        return Task.CompletedTask;
    }

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (shouldPlay)
        {
            if (_player.State == PlaybackState.Paused) _player.Resume();
            else if (_player.State is PlaybackState.Stopped or PlaybackState.Error && CurrentTrack != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                EnqueueCommand(new PlayCurrentIndexCommand(session));
            }
        }
        else _player.Pause();
    }

    /// <summary>
    /// Останавливает воспроизведение и очищает стейт трека.
    /// </summary>
    public void Stop()
    {
        BeginNewSession();
        _player.Stop();

        CurrentTrack = null;
        StreamInfo = AudioStreamInfo.Empty;

        RaiseOnUI(() =>
        {
            OnTrackChanged?.Invoke(null);
        });
    }

    public Task PlayNextAsync() { ResetSealedFailedTrack(); EnqueueCommand(new NavigateCommand(true, true)); return Task.CompletedTask; }
    public Task PlayPreviousAsync() { ResetSealedFailedTrack(); EnqueueCommand(new NavigateCommand(false, true)); return Task.CompletedTask; }

    #endregion

    #region Command Handlers

    private async Task HandlePlayTrackAsync(PlayTrackCommand cmd)
    {
        if (_session.IsStale(cmd.Session)) return;

        // Сбрасываем лимит авто-попыток только при обычном (неавтоматическом) запуске трека
        if (!cmd.IsRetry)
        {
            Interlocked.Exchange(ref _cacheRetryCount, 0);
        }

        lock (_queueLock)
        {
            int idx = _queue.FindIndex(t => t.Id == cmd.Track.Id);
            if (idx >= 0) { _currentIndex = idx; _queue[idx] = cmd.Track; }
            else { _queue.Clear(); _queue.Add(cmd.Track); _currentIndex = 0; }
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() => OnQueueChanged?.Invoke());
        // Передаем SeekPosition дальше в проигрыватель
        await PlayCurrentIndexAsync(cmd.Session, cmd.SeekPosition).ConfigureAwait(false);
    }

    private async Task HandleStartQueueAsync(StartQueueCommand cmd)
    {
        if (_session.IsStale(cmd.Session)) return;

        Interlocked.Exchange(ref _cacheRetryCount, 0);

        lock (_queueLock)
        {
            _queue.Clear();
            _queue.AddRange(cmd.Tracks);
            _currentIndex = _queue.FindIndex(t => t.Id == cmd.StartTrack.Id);
            if (_currentIndex == -1 && _queue.Count > 0) _currentIndex = 0;
            if (ShuffleEnabled && _queue.Count > 1) ApplyShuffleInPlace(preserveCurrentAtStart: true);
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() => OnQueueChanged?.Invoke());
        await PlayCurrentIndexAsync(cmd.Session).ConfigureAwait(false);
    }

    private async Task HandleNavigateAsync(NavigateCommand cmd)
    {
        int session = BeginNewSession();
        bool canMove;
        bool queueMutated;

        lock (_queueLock)
        {
            canMove = cmd.Forward ? TryMoveNext(cmd.UserInitiated) : TryMovePrevious();
            queueMutated = _queueMutatedByNavigation;
        }

        if (queueMutated) RaiseOnUI(() => OnQueueChanged?.Invoke());

        if (canMove)
            await PlayCurrentIndexAsync(session).ConfigureAwait(false);
        else if (!cmd.Forward && _player.State != PlaybackState.Stopped)
            await _player.SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
        else
            Stop();
    }

    #endregion

    #region Seek

    /// <summary>
    /// Выполняет seek немедленно.
    /// </summary>
    /// <remarks>
    /// <para>Debounce-логика удалена: после переноса preview seek из UI
    /// в чисто визуальный режим реальный seek происходит только по финальному
    /// действию пользователя (release/click). Дополнительный debounce в движке
    /// больше не нужен и только создаёт лишнее состояние и гонки.</para>
    /// </remarks>
    public ValueTask SeekAsync(TimeSpan position)
    {
        return _player.SeekAsync(position);
    }

    #endregion

    #region Quality Switching

    public async Task SwitchQualityAsync(string container, int bitrate)
    {
        if (CurrentTrack == null) return;
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        var ct = GetSessionToken();

        try
        {
            var elapsed = (DateTime.UtcNow - _lastQualitySwitchTime).TotalMilliseconds;
            if (elapsed < QualitySwitchCooldownMs)
                await Task.Delay(QualitySwitchCooldownMs - (int)elapsed, ct).ConfigureAwait(false);
            _lastQualitySwitchTime = DateTime.UtcNow;

            var pos = CurrentPosition;
            var track = CurrentTrack;
            if (track == null) return;

            track.TransientContainer = container;
            track.TransientBitrate = bitrate;
            if (_library.Settings.RememberTrackFormat)
            {
                track.PreferredContainer = container;
                track.PreferredBitrate = bitrate;
            }

            if (!_session.IsStale(session))
                await SwitchQualityCoreAsync(track, pos, session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Выполняет переключение качества: останавливает воспроизведение, обновляет URL
    /// и перезапускает с новым битрейтом/контейнером.
    /// </summary>
    /// <param name="track">Текущий трек.</param>
    /// <param name="position">Позиция для возобновления после переключения.</param>
    /// <param name="session">ID сессии.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task SwitchQualityCoreAsync(TrackInfo track, TimeSpan position, int session, CancellationToken ct)
    {
        try
        {
            _player.Stop();
            track.StreamUrl = "";
            Interlocked.Exchange(ref _nTokenActiveTrackId, track.Id);
            ct.ThrowIfCancellationRequested();

            var info = await Task.Run(async () =>
                await _youtube.RefreshStreamUrlAsync(track, false, ct).ConfigureAwait(false)
                ?? await _youtube.RefreshStreamUrlAsync(track, true, ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            if (info == null)
            {
                if (!_session.IsStale(session))
                    RaiseError(new InvalidOperationException("No stream available"));
                return;
            }

            if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id)) return;

            track.TransientBitrate = info.Value.Bitrate;

            // Кэшируем свежий URL
            if (!string.IsNullOrEmpty(info.Value.Url))
                track.StreamUrl = info.Value.Url;

            await _player.PlayAsync(info.Value.Url, track.Id, info.Value.Bitrate, ct,
                seekPosition: position.TotalSeconds > 1 ? position : null).ConfigureAwait(false);

            ApplyGainToPipeline();
        }
        catch (Exception ex)
        {
            if (!_session.IsStaleOrCancelled(session, ct) && !CancellationHelper.IsCancellationLike(ex))
            {
                AbortCurrentTrackPlaybackAfterFatalError(track.Id);
                RaiseError(ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, track.Id);
        }
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Обрабатывает инвалидацию кэша: при сбое чтения выполняет 
    /// бесшовное переключение на стриминг, который хирургически пропатчит повреждённый чанк на диске.
    /// </summary>
    private void HandleCacheInvalidated(CacheInvalidatedException cacheEx)
    {
        var trackId = cacheEx.TrackId ?? CurrentTrack?.Id;

        if (cacheEx.IsRecoverable && _cacheRetryCount < MaxCacheAutoRetries)
        {
            int retryNumber = Interlocked.Increment(ref _cacheRetryCount);
            var resumePosition = CurrentPosition;
            var track = CurrentTrack;

            Log.Info($"[AudioEngine] Cache auto-retry #{retryNumber}/{MaxCacheAutoRetries}: track={trackId}, kind={cacheEx.Kind}, pos={resumePosition}");

            // Полное физическое стирание файла с диска выполняем ТОЛЬКО если файла действительно нет на месте (FileDeleted)
            if (cacheEx.Kind is CacheInvalidationKind.FileDeleted)
            {
                if (!string.IsNullOrEmpty(trackId))
                {
                    try
                    {
                        AudioSourceFactory.GlobalCache?.RemoveTrackCache(trackId);
                        Log.Info($"[AudioEngine] Removed missing cache registry for retry: {trackId}");
                    }
                    catch (Exception removeEx)
                    {
                        Log.Warn($"[AudioEngine] Failed to remove cache: {removeEx.Message}");
                    }
                }
            }
            else if (cacheEx.Kind is CacheInvalidationKind.ParserResync or CacheInvalidationKind.ShortRead)
            {
                // При повреждении файла мы сохраняем его на диске!
                // Метод LocalFileSource уже пометил повреждённый чанк неактивным.
                // Пересоздание конвейера создаст CachingStreamSource, который скачает из сети
                // исключительно недостающий чанк и запишет его прямо в тело существующего файла.
                Log.Info($"[AudioEngine] Surgical patch in progress. Preserving existing cache file for: {trackId}");
            }

            if (track != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                // Указываем IsRetry: true для предотвращения сброса счетчика попыток
                EnqueueCommand(new PlayTrackCommand(track, session, resumePosition, IsRetry: true));
            }
            return;
        }

        Log.Warn($"[AudioEngine] Cache error non-recoverable or retry budget exhausted (retries={_cacheRetryCount}, kind={cacheEx.Kind}): {cacheEx.Message}");

        if (!string.IsNullOrEmpty(trackId))
        {
            try { AudioSourceFactory.GlobalCache?.RemoveTrackCache(trackId); }
            catch (Exception ex) { Log.Warn($"[AudioEngine] Failed to remove cache: {ex.Message}"); }
        }

        RaiseError(new CacheInvalidatedException(cacheEx.Message, cacheEx.InnerException));
    }

    private void RaiseError(Exception exception)
    {
        RaiseOnUI(() => OnErrorOccurred?.Invoke(exception));
    }

    #endregion

    #region ISuspendable Implementation

    /// <inheritdoc />
    public void OnSuspend(SuspendLevel level)
    {
        _isSuspended = true;

        if (ShouldKeepSourceActiveWhileSuspended())
        {
            Log.Debug("[AudioEngine] Suspend policy: source remains active due to active playback/buffering");
            return;
        }

        ApplyLifecycleSourceSuspendPolicy();
    }

    /// <inheritdoc />
    public void OnResume(SuspendLevel previousLevel)
    {
        _isSuspended = false;
        ApplyLifecycleSourceSuspendPolicy();
        _ = PreWarmHttpConnectionAsync();
    }

    private static async Task PreWarmHttpConnectionAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://redirector.googlevideo.com/");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Audio.Http.SharedHttpClient.Instance.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    #endregion

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _player.GetActivePipeline();
    public long GetDownloadedBytes() => _player.GetDownloadedBytes();
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => _player.GetBufferedRanges();

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        var info = StreamInfo;
        return (info.Codec, info.Bitrate, info.IsValid);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Потокобезопасно обновляет статус ручной загрузки движка на UI-потоке.
    /// </summary>
    private void SetManualLoading(bool loading)
    {
        RaiseOnUI(() =>
        {
            if (_isManualLoading != loading)
            {
                _isManualLoading = loading;
                this.RaisePropertyChanged(nameof(IsLoading));
                OnLoadingStateChanged?.Invoke(IsLoading);
            }
        });
    }

    /// <summary>
    /// Определяет, должен ли сетевой audio source оставаться активным,
    /// даже если UI ушёл в background/suspend.
    /// </summary>
    /// <returns>
    /// <c>true</c>, если source нельзя suspend'ить;
    /// <c>false</c>, если suspend source допустим.
    /// </returns>
    /// <remarks>
    /// <para>Ключевой принцип: UI suspend ≠ audio/network suspend.</para>
    /// <para>
    /// Пока player находится в состояниях <see cref="PlaybackState.Loading"/>,
    /// <see cref="PlaybackState.Buffering"/>, <see cref="PlaybackState.Playing"/>
    /// или в детальном состоянии <see cref="PlayerState.Seeking"/>,
    /// source preload критически важен для стабильного playback/rebuffer.
    /// </para>
    /// </remarks>
    private bool ShouldKeepSourceActiveWhileSuspended()
    {
        var playbackState = _player.State;
        var detailedState = _player.DetailedState;

        return playbackState is PlaybackState.Loading
            or PlaybackState.Buffering
            or PlaybackState.Playing
            || detailedState == PlayerState.Seeking;
    }

    /// <summary>
    /// Применяет lifecycle-политику suspend/resume к активному сетевому audio source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Если приложение suspended, но playback ещё активен, source НЕ приостанавливается:
    /// это предотвращает starvation на сетях с высоким RTT.
    /// </para>
    /// <para>
    /// Если playback не активен (paused/stopped/error) и приложение suspended —
    /// source можно safely suspend'ить для экономии ресурсов.
    /// </para>
    /// </remarks>
    private void ApplyLifecycleSourceSuspendPolicy()
    {
        if (_player.GetActivePipeline()?.Source is not Audio.Sources.CachingStreamSource cs)
        {
            Interlocked.Exchange(ref _sourceLifecycleSuspended, 0);
            return;
        }

        if (!_isSuspended || ShouldKeepSourceActiveWhileSuspended())
        {
            if (Interlocked.Exchange(ref _sourceLifecycleSuspended, 0) != 0)
                cs.Resume();

            return;
        }

        if (Interlocked.Exchange(ref _sourceLifecycleSuspended, 1) == 0)
            cs.Suspend();
    }

    private static void RaiseOnUI(Action action)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        AudioSourceFactory.ApplyInternetProfile(profile);
        return Task.CompletedTask;
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Синхронный dispose — FALLBACK shutdown path.
    /// </summary>
    /// <remarks>
    /// Best-effort cleanup без блокирующего ожидания async операций.
    /// НЕ ожидает flush pending gain writes — возможна потеря последних записей.
    /// Для корректного flush использовать <see cref="DisposeAsync"/>.
    /// </remarks>
    private void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
            lock (_sessionLock) { _sessionCts?.Cancel(); _sessionCts?.Dispose(); }

            _library.UpdateSettings(s =>
            {
                s.Volume = _volumePercent;
                s.RepeatMode = RepeatMode;
                s.ShuffleEnabled = ShuffleEnabled;
            });

            // Закрываем канал явно, затем cancel — максимально быстрое завершение loop'а
            _commandQueue.Writer.TryComplete();
            _lifetimeCts.Cancel();

            // Best-effort блокирующее ожидание: короткий таймаут, не блокируем UI надолго
            try { _commandProcessorTask?.Wait(millisecondsTimeout: 500); } catch { }
            try { _volumeSaveTask?.Wait(millisecondsTimeout: 200); } catch { }

            _player.Dispose();
            _lifetimeCts.Dispose();
        }
    }

    /// <summary>
    /// Асинхронный dispose — PRIMARY shutdown path.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. Отписка + отмена active CTS
        _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
        lock (_sessionLock) { _sessionCts?.Cancel(); _sessionCts?.Dispose(); }

        // 2. Синхронное сохранение настроек (in-memory словарь, sub-μs)
        _library.UpdateSettings(s =>
        {
            s.Volume = _volumePercent;
            s.RepeatMode = RepeatMode;
            s.ShuffleEnabled = ShuffleEnabled;
        });

        // 3. Async flush gain writes — hard timeout без блокировки UI
        using (var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await FlushPendingGainWritesAsync(flushCts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            { Log.Warn($"[AudioEngine] Gain flush on async dispose: {ex.Message}"); }
        }

        // 4. Complete writer — «новых команд не будет»; штатное завершение ReadAllAsync
        _commandQueue.Writer.TryComplete();

        // 5. Cancel lifetime — fallback-сигнал для loop'ов и VolumeSaveLoop
        _lifetimeCts.Cancel();

        // 6. Детерминированный drain: ждём завершения обоих loop'ов
        //    Таймауты выровнены с DisposeTaskTimeoutSec AudioPlayer'а
        const int loopDrainTimeoutMs = 2_000;
        if (_commandProcessorTask != null)
        {
            try
            {
                await _commandProcessorTask
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            { Log.Warn("[AudioEngine] Command processor did not finish within dispose timeout"); }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { }
        }

        if (_volumeSaveTask != null)
        {
            try
            {
                await _volumeSaveTask
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            { Log.Warn("[AudioEngine] Volume save loop did not finish within dispose timeout"); }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { }
        }

        // 7. Async dispose плеера (ожидает его внутренний command processor)
        await _player.DisposeAsync().ConfigureAwait(false);

        // 8. Dispose lifetime CTS
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Выполняет синхронную утилизацию ресурсов аудио-движка.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}