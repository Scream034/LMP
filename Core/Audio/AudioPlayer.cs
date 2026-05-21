using System.Threading.Channels;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Models;
using LMP.Core.Services;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Настройки инициализации аудиоплеера.
/// </summary>
public sealed class AudioPlayerOptions
{
    /// <summary>Колбэк для обновления протухших URL на лету.</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? UrlRefreshCallback { get; init; }

    /// <summary>Частота оповещений UI об изменении позиции (мс).</summary>
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPositionUpdateIntervalMs);

    /// <summary>Количество попыток переподключения при ошибках сети.</summary>
    public int MaxRetryAttempts { get; init; } = AudioConstants.MaxRetryAttempts;

    /// <summary>Задержка между попытками переподключения.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(RetryDelayMs);

    /// <summary>Использовать заглушку вместо реального звукового драйвера.</summary>
    public bool UseNullBackend { get; init; }

    /// <summary>Конфигурация стриминга (настройки буферизации сети).</summary>
    public StreamingConfig? StreamingConfig { get; init; }

    /// <summary>
    /// Callback конфигурации pipeline, вызываемый после заполнения буфера,
    /// строго до открытия gate (<see cref="IPlaybackBackend.ActivateFillLoop"/>).
    /// </summary>
    /// <remarks>
    /// <para>Гарантирует что gain и нормализация применены до первого <see cref="AudioPipeline.AudioCallback"/>,
    /// исключая попадание un-normalized сэмплов в provider buffer.</para>
    /// </remarks>
    public Action<AudioPipeline>? OnPipelineConfiguring { get; init; }

    /// <summary>
    /// Callback вызываемый когда gain нормализации зафиксирован (pre-scan или real-time анализ).
    /// Аргументы: trackId, locked gain. Используется для персистирования в БД.
    /// Вызывается максимум один раз за трек. Должен быть thread-safe (вызов из fill thread).
    /// </summary>
    public Action<string, float>? OnGainLocked { get; init; }
}

/// <summary>
/// Аудио плеер с акторной моделью обработки команд.
///
/// <para><b>Архитектура:</b> Все публичные методы неблокирующие (отправляют команды в Channel). Обработка строго последовательна в фоне.</para>
/// <para><b>Shared Backend:</b> Backend драйвера ОС переиспользуется без полного пересоздания, что ускоряет переключение и предотвращает проблемы с EcoQoS.</para>
/// <para><b>Error contract:</b> Ошибка конкретной команды публикуется ровно один раз внутри её обработчика.
/// <see cref="ProcessCommandsAsync"/> не должен повторно эскалировать уже обработанную ошибку.</para>
/// </summary>
public sealed class AudioPlayer : IAsyncDisposable, IDisposable
{
    #region Constants

    /// <summary>Вместимость очереди команд плеера.</summary>
    private const int CommandChannelCapacity = 32;

    /// <summary>Таймаут ожидания остановки плеера (в секундах).</summary>
    private const int StopTaskTimeoutSec = 2;

    /// <summary>Таймаут очистки ресурсов при диспозе (в секундах).</summary>
    private const int DisposeTaskTimeoutSec = 2;

    /// <summary>Минимальное количество мс аудио данных, требуемое для быстрого возобновления (Resume) после паузы.</summary>
    private const int ResumeMinBufferMs = 100;

    /// <summary>Максимальное время блокировки (мс) при быстром warmup во время Resume.</summary>
    private const int ResumeWarmupTimeoutMs = 500;

    /// <summary>Время на остановку потока декодера при Seek-операции (мс).</summary>
    private const int SeekStopDecoderTimeoutMs = 50;

    /// <summary>Максимальное время блокировки (мс) при warmup во время Seek.</summary>
    private const int SeekWarmupTimeoutMs = 300;

    /// <summary>Время ожидания (мс) до заполнения первичного PCM буфера для старта.</summary>
    private const int PlayBufferWaitTimeoutMs = 5000;

    /// <summary>Количество бит в одном байте (для расчета скорости).</summary>
    private const int BitsPerByte = 8;

    #endregion

    #region Fields

    private readonly AudioPlayerOptions _options;
    private readonly AudioPlayerEvents _events = new();

    /// <summary>Канал очереди команд для actor model.</summary>
    private readonly Channel<IAudioCommand> _commandChannel;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _commandProcessorTask;

    /// <summary>Backend системного звука (создается 1 раз, переиспользуется).</summary>
    private readonly IPlaybackBackend _sharedBackend;

    /// <summary>Текущий активный конвейер воспроизведения.</summary>
    private volatile AudioPipeline? _activePipeline;

    private volatile PlayerState _state = PlayerState.Idle;
    private volatile bool _disposed;

    /// <summary>Уникальный ID сессии для отмены устаревших команд. Изменяется через Interlocked.</summary>
    private int _sessionId;

    private Timer? _positionTimer;
    private Timer? _bufferTimer;

    private string? _currentTrackId;

    /// <summary>Кэшированный делегат URL refresher. Инвалидируется при смене <see cref="_currentTrackId"/>.</summary>
    private Func<CancellationToken, Task<string?>>? _cachedUrlRefresher;
    private string? _cachedUrlRefresherTrackId;

    /// <summary>
    /// CTS активной background-фазы seek.
    /// Каждый новый Seek / Stop / Play отменяет предыдущий через <see cref="CancelActiveSeek"/>.
    /// Гарантирует что одновременно выполняется не более одного background-seek.
    /// </summary>
    private CancellationTokenSource? _activeSeekCts;

    #endregion

    #region Properties

    public AudioPlayerEvents Events => _events;

    public TimeSpan Position
    {
        get
        {
            var pipeline = _activePipeline;
            if (pipeline == null) return TimeSpan.Zero;

            long playedSamples = Math.Max(0, pipeline.PlayedSamples - pipeline.BackendBufferedSamples);
            double seconds = (double)playedSamples / (pipeline.SampleRate * pipeline.Channels);

            var duration = Duration;
            if (duration.TotalSeconds > 0 && seconds > duration.TotalSeconds)
                seconds = duration.TotalSeconds;

            return TimeSpan.FromSeconds(seconds);
        }
    }

    public TimeSpan Duration => _activePipeline != null
        ? TimeSpan.FromMilliseconds(_activePipeline.StreamInfo.DurationMs)
        : TimeSpan.Zero;

    public PlaybackState State => MapState(_state);
    public AudioStreamInfo StreamInfo => _activePipeline?.StreamInfo ?? AudioStreamInfo.Empty;

    #endregion

    #region Constructor

    public AudioPlayer(AudioPlayerOptions? options = null)
    {
        _options = options ?? new AudioPlayerOptions();

        _commandChannel = Channel.CreateBounded<IAudioCommand>(new BoundedChannelOptions(CommandChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _sharedBackend = CreateSharedBackend(_options);
        _commandProcessorTask = Task.Run(ProcessCommandsAsync);

        Log.Info("[AudioPlayer] Created with actor model + shared backend");
    }

    private static IPlaybackBackend CreateSharedBackend(AudioPlayerOptions options)
    {
        if (options.UseNullBackend) return new Backends.NullAudioBackend();
        try { return new Backends.NAudioBackend(); }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] NAudio creation failed: {ex.Message}, using NullBackend");
            return new Backends.NullAudioBackend();
        }
    }

    #endregion

    #region Public API (Non-blocking)

    private void Play(
        string url,
        string? trackId = null,
        int bitrateHint = 0,
        TimeSpan? seekPosition = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int session = Interlocked.Increment(ref _sessionId);
        _currentTrackId = trackId;
        SetState(PlayerState.Loading);
        _commandChannel.Writer.TryWrite(new PlayCommand(url, trackId, bitrateHint, session, seekPosition, ct));
    }

    /// <summary>
    /// Запускает воспроизведение и ожидает перехода в состояние <see cref="PlaybackState.Playing"/>.
    /// </summary>
    /// <remarks>
    /// <para>Подписки на события очищаются через <see cref="CancellationTokenRegistration"/>
    /// во всех сценариях завершения: успех, ошибка, отмена.</para>
    ///
    /// <para>Если <paramref name="ct"/> уже отменён на момент вызова —
    /// метод немедленно возвращает отменённую задачу без запуска команды.</para>
    /// </remarks>
    public Task PlayAsync(
        string url,
        string? trackId = null,
        int bitrateHint = 0,
        CancellationToken ct = default,
        TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(PlaybackState state)
        {
            if (state != PlaybackState.Playing) return;
            tcs.TrySetResult(true);
        }

        void OnError(AudioPlayerError error)
        {
            tcs.TrySetException(error.Exception ?? new Exception(error.Message));
        }

        _events.StateChanged += OnStateChanged;
        _events.ErrorOccurred += OnError;

        var registration = ct.UnsafeRegister(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            tcs);

        tcs.Task.ContinueWith(
            (_, state) =>
            {
                var ctx = (PlayAsyncContext)state!;
                ctx.Player._events.StateChanged -= ctx.OnStateChanged;
                ctx.Player._events.ErrorOccurred -= ctx.OnError;
                ctx.Registration.Dispose();
            },
            new PlayAsyncContext(this, OnStateChanged, OnError, registration),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Play(url, trackId, bitrateHint, seekPosition, ct);
        return tcs.Task;
    }

    /// <summary>Контекст для continuation в <see cref="PlayAsync"/> — избегает замыканий на heap.</summary>
    private sealed record PlayAsyncContext(
        AudioPlayer Player,
        Action<PlaybackState> OnStateChanged,
        Action<AudioPlayerError> OnError,
        CancellationTokenRegistration Registration);

    public void Pause()
    {
        if (_state != PlayerState.Playing) return;
        SetState(PlayerState.Paused);
        _activePipeline?.Stop();
    }

    public void Resume()
    {
        if (_state != PlayerState.Paused) return;
        var pipeline = _activePipeline;
        if (pipeline == null) return;

        int bufferedBytes = _sharedBackend.BufferedBytes;
        int minBytes = pipeline.SampleRate * pipeline.Channels * sizeof(float) * ResumeMinBufferMs / 1000;

        if (bufferedBytes < minBytes)
        {
            pipeline.ActivateFillLoop();
            pipeline.WaitForBackendWarmup(timeoutMs: ResumeWarmupTimeoutMs);
        }

        SetState(PlayerState.Playing);
        pipeline.Start();
    }

    public void Stop()
    {
        if (_state == PlayerState.Idle || _state == PlayerState.Disposed) return;
        int session = Interlocked.Increment(ref _sessionId);
        _commandChannel.Writer.TryWrite(new StopCommand(session));
    }

    public async Task StopAsync()
    {
        if (_state == PlayerState.Idle || _state == PlayerState.Disposed) return;
        int session = Interlocked.Increment(ref _sessionId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(PlaybackState state)
        {
            if (state == PlaybackState.Stopped)
            {
                _events.StateChanged -= OnState;
                tcs.TrySetResult(true);
            }
        }

        _events.StateChanged += OnState;
        await _commandChannel.Writer.WriteAsync(new StopCommand(session));

        try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(StopTaskTimeoutSec)); }
        catch (TimeoutException)
        {
            _events.StateChanged -= OnState;
            Log.Warn("[AudioPlayer] StopAsync timeout");
        }
    }

    /// <summary>
    /// Инициирует seek-операцию, отменяя предыдущий pending seek если есть.
    /// </summary>
    /// <remarks>
    /// <para><b>FIX 3:</b> Каждый новый Seek отменяет <see cref="_activeSeekCts"/> предыдущего.
    /// Background-фаза предыдущего seek получает <see cref="OperationCanceledException"/>
    /// и завершается, предотвращая каскадное накопление HTTP-запросов при drag-seek.</para>
    /// </remarks>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed || _state is not (PlayerState.Playing or PlayerState.Paused))
            return ValueTask.CompletedTask;

        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
            return ValueTask.CompletedTask;

        _events.RaisePositionChanged(position);

        CancelActiveSeek();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));

        int session = Interlocked.CompareExchange(ref _sessionId, 0, 0);
        _commandChannel.Writer.TryWrite(new SeekCommand(position, session, tcs));

        return new ValueTask(tcs.Task);
    }

    /// <summary>
    /// Отменяет текущую background-фазу seek (HTTP загрузка чанка + resume).
    /// Вызывается из <see cref="SeekAsync"/>, <see cref="HandleStopAsync"/>,
    /// <see cref="HandlePlayAsync"/> для предотвращения гонки между
    /// background-seek и новой командой.
    /// </summary>
    private void CancelActiveSeek()
    {
        var old = Interlocked.Exchange(ref _activeSeekCts, null);
        if (old != null)
        {
            try { old.Cancel(); } catch (ObjectDisposedException) { }
            try { old.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    #endregion

    #region Command Processing

    private async Task ProcessCommandsAsync()
    {
        var reader = _commandChannel.Reader;
        try
        {
            await foreach (var command in reader.ReadAllAsync(_lifetimeCts.Token))
            {
                int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);

                if (command.SessionId < currentSession && command is not DisposeCommand)
                {
                    if (command is SeekCommand { Completion: { } tcs }) tcs.TrySetCanceled();
                    continue;
                }

                if (!PlayerStateTransitions.CanAcceptCommand(_state, command))
                {
                    if (command is SeekCommand { Completion: { } failTcs }) failTcs.TrySetResult(false);
                    continue;
                }

                try
                {
                    await ProcessCommandAsync(command);
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (PlaybackErrorOrchestrator.IsCancellationLike(ex) ||
                        command.SessionId < Interlocked.CompareExchange(ref _sessionId, 0, 0))
                    {
                        Log.Debug($"[AudioPlayer] Suppressing stale command error: {ex.GetType().Name}");
                        continue;
                    }

                    Log.Error($"[AudioPlayer] Command error: {ex.Message}", ex);
                    HandleError(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"[AudioPlayer] Command processor fatal: {ex.Message}", ex); }
    }

    private async Task ProcessCommandAsync(IAudioCommand command)
    {
        switch (command)
        {
            case PlayCommand play: await HandlePlayAsync(play); break;
            case StopCommand: await HandleStopAsync(); break;
            case SeekCommand seek: await HandleSeekAsync(seek); break;
            case DisposeCommand: await HandleDisposeAsync(); break;
        }
    }

    /// <summary>
    /// Обрабатывает команду воспроизведения.
    /// </summary>
    /// <remarks>
    /// <para>Все ошибки публикуются внутри этого метода ровно один раз через
    /// <see cref="AudioPlayerEvents.ErrorOccurred"/>. Метод не пробрасывает
    /// уже обработанные ошибки наружу, чтобы <see cref="ProcessCommandsAsync"/>
    /// не эскалировал их повторно.</para>
    /// </remarks>
    private async Task HandlePlayAsync(PlayCommand cmd)
    {
        Log.Info($"[AudioPlayer] Play: {cmd.TrackId ?? "unknown"}, bitrate hint: {cmd.BitrateHint}");
        SetState(PlayerState.Loading);

        CancelActiveSeek();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token,
            cmd.ExternalCancellationToken);
        var ct = linkedCts.Token;

        var oldPipeline = Interlocked.Exchange(ref _activePipeline, null);
        float previousLockedGain = oldPipeline?.GetLockedNormalizationGain() ?? 1.0f;

        if (oldPipeline != null)
            _ = Task.Run(async () =>
            {
                try { await oldPipeline.DisposeAsync(); }
                catch (Exception ex) { Log.Debug($"[AudioPlayer] Background dispose error: {ex.Message}"); }
            });

        StopTimers();

        try
        {
            ct.ThrowIfCancellationRequested();

            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Url, cmd.TrackId, cmd.BitrateHint, CreateUrlRefresher(),
                _options, _sharedBackend, ct).ConfigureAwait(false);

            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                await pipeline.DisposeAsync();
                return;
            }

            ct.ThrowIfCancellationRequested();

            _events.RaiseStreamInfo(pipeline.StreamInfo);

            var replaced = Interlocked.Exchange(ref _activePipeline, pipeline);
            if (replaced != null) await replaced.DisposeAsync();

            pipeline.SetInitialNormalizationGain(previousLockedGain);
            _options.OnPipelineConfiguring?.Invoke(pipeline);

            // Регистрируем callback фиксации gain ДО pre-scan.
            // PreScanNormalizationAsync → LockGain → _onGainLocked?.Invoke(gain).
            // Если callback не зарегистрирован — gain вычисляется, но НИКОГДА
            // не персистируется в БД, и при следующем запуске pre-scan повторяется.
            var lockedTrackId = cmd.TrackId;
            if (lockedTrackId != null && _options.OnGainLocked != null)
            {
                var gainCallback = _options.OnGainLocked;
                pipeline.Analyzer.SetGainLockedCallback(g => gainCallback(lockedTrackId, g));
            }

            await pipeline.PreScanNormalizationAsync(ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (cmd.SeekPosition is { TotalMilliseconds: > 0 } seekPos)
            {
                long seekMs = (long)seekPos.TotalMilliseconds;
                pipeline.PrepareForSeek(seekMs);
                if (await pipeline.Source.SeekAsync(seekMs, ct))
                {
                    long targetSamples = (long)(seekMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
                    pipeline.SetDecodedSamplesPosition(targetSamples);
                }
            }

            pipeline.StartDecoding(CreateUrlRefresher(), _options, OnTrackEnded, HandleError);
            SetState(PlayerState.Buffering);

            int threshold = pipeline.SampleRate * pipeline.Channels * MinBufferMs / 1000;
            await pipeline.WaitForBufferAsync(threshold, PlayBufferWaitTimeoutMs, ct).ConfigureAwait(false);

            currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null) await stale.DisposeAsync();
                return;
            }

            ct.ThrowIfCancellationRequested();

            pipeline.ActivateFillLoop();
            pipeline.Start();

            StartTimers();
            SetState(PlayerState.Playing);

            _ = WatchPipelineLifetimeAsync(pipeline, cmd.SessionId);
        }
        catch (OperationCanceledException)
        {
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (NAudio.MmException ex)
        {
            Log.Error($"[AudioPlayer] Audio device error: {ex.Message}");
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(GetDeviceErrorMessage(), ex));
        }
        catch (AudioDeviceException ex)
        {
            Log.Error($"[AudioPlayer] No audio device: {ex.Message}");
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(GetDeviceErrorMessage(), ex));
        }
        catch (Exception ex) when (
            cmd.ExternalCancellationToken.IsCancellationRequested ||
            cmd.SessionId < Interlocked.CompareExchange(ref _sessionId, 0, 0) ||
            PlaybackErrorOrchestrator.IsCancellationLike(ex))
        {
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);

            Log.Debug("[AudioPlayer] Suppressing stale/cancelled play error");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
        }
    }

    /// <summary>
    /// Фоновая задача ожидания отмены lifetime token pipeline.
    /// Если pipeline отменён из-за потери устройства — инициирует остановку.
    /// </summary>
    private async Task WatchPipelineLifetimeAsync(AudioPipeline pipeline, int sessionId)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, pipeline.LifetimeToken);
        }
        catch (OperationCanceledException)
        {
            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (_disposed || sessionId < currentSession) return;
            if (_activePipeline != pipeline) return;

            Log.Error("[AudioPlayer] Pipeline lifetime ended unexpectedly (device lost)");
            HandleError(new AudioDeviceException(GetDeviceErrorMessage()));

            int session = Interlocked.Increment(ref _sessionId);

            try
            {
                await _commandChannel.Writer.WriteAsync(new StopCommand(session));
            }
            catch (ChannelClosedException)
            {
                Log.Debug("[AudioPlayer] Command channel closed during device loss handling");
            }
        }
    }

    private async Task HandleStopAsync()
    {
        Log.Debug("[AudioPlayer] Stop");

        CancelActiveSeek();

        StopTimers();

        var pipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (pipeline != null) await pipeline.DisposeAsync().ConfigureAwait(false);

        _sharedBackend.Flush();
        _currentTrackId = null;
        SetState(PlayerState.Idle);
    }

    /// <summary>
    /// Обрабатывает команду seek.
    /// </summary>
    /// <remarks>
    /// <para><b>Двухфазный seek — actor loop свободен.</b></para>
    /// <para><b>Фаза 1 (sync, в actor loop):</b> Stop decoder → flush → reset parser.
    /// Занимает 1-50мс. Actor loop возвращается к обработке очереди.</para>
    /// <para><b>Фаза 2 (async, background Task):</b> HTTP загрузка чанка → start decoder →
    /// wait buffer → resume playback → resolve TCS. Отменяется через <see cref="_activeSeekCts"/>
    /// при новом Seek/Stop/Play.</para>
    /// <para><b>Почему это безопасно:</b> Каждая фаза 2 проверяет <c>_activeSeekCts.Token</c>
    /// на каждом шаге. Новая команда вызывает <see cref="CancelActiveSeek"/> → OCE →
    /// background task завершается без побочных эффектов.</para>
    /// </remarks>
    private async Task HandleSeekAsync(SeekCommand cmd)
    {
        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
        {
            cmd.Completion?.TrySetResult(false);
            return;
        }

        bool wasPlaying = _state == PlayerState.Playing;
        SetState(PlayerState.Seeking);
        StopPositionTimer();

        try
        {
            // ФАЗА 1: Синхронная подготовка (actor loop)

            // Prefetch использует lifetime token — не будет отменён epoch reset.
            if (pipeline.Source is Sources.CachingStreamSource cachingSource)
                _ = cachingSource.TryPrefetchChunkForSeekAsync(
                    (long)cmd.Position.TotalMilliseconds, _lifetimeCts.Token);

            await pipeline.StopDecodingAsync(
                TimeSpan.FromMilliseconds(SeekStopDecoderTimeoutMs)).ConfigureAwait(false);

            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                cmd.Completion?.TrySetCanceled();
                StartPositionTimerDelayed();
                return;
            }

            pipeline.Stop();
            pipeline.Flush();

            long posMs = (long)cmd.Position.TotalMilliseconds;
            pipeline.PrepareForSeek(posMs);

            // ФАЗА 2: Background (actor loop СВОБОДЕН)
            var seekCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            var oldSeekCts = Interlocked.Exchange(ref _activeSeekCts, seekCts);
            if (oldSeekCts != null)
            {
                try { oldSeekCts.Cancel(); } catch (ObjectDisposedException) { }
                try { oldSeekCts.Dispose(); } catch (ObjectDisposedException) { }
            }

            _ = CompleteSeekInBackgroundAsync(
                pipeline, cmd, posMs, wasPlaying, cmd.SessionId, seekCts);
        }
        catch (OperationCanceledException)
        {
            cmd.Completion?.TrySetCanceled();
            StartPositionTimerDelayed();
        }
        catch (AudioDeviceException ex)
        {
            Log.Error($"[AudioPlayer] Audio device lost during seek: {ex.Message}");
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Seek phase-1 error: {ex.Message}");
            cmd.Completion?.TrySetException(ex);
            SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);
            if (wasPlaying)
            {
                try { pipeline.Start(); }
                catch (AudioDeviceException devEx) { HandleError(devEx); return; }
            }
            StartPositionTimerDelayed();
        }
    }

    /// <summary>
    /// Background-фаза seek: загрузка чанка, запуск decoder, ожидание буфера, resume.
    /// </summary>
    private async Task CompleteSeekInBackgroundAsync(
        AudioPipeline pipeline,
        SeekCommand cmd,
        long posMs,
        bool wasPlaying,
        int sessionAtStart,
        CancellationTokenSource seekCts)
    {
        var seekCt = seekCts.Token;

        try
        {
            bool success = await pipeline.Source.SeekAsync(posMs, seekCt).ConfigureAwait(false);

            if (seekCt.IsCancellationRequested || _disposed || _activePipeline != pipeline)
            {
                cmd.Completion?.TrySetCanceled();
                return;
            }

            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (sessionAtStart < currentSession)
            {
                cmd.Completion?.TrySetCanceled();
                return;
            }

            if (!success)
            {
                cmd.Completion?.TrySetResult(false);
                SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);
                if (wasPlaying) pipeline.Start();
                StartPositionTimerDelayed();
                return;
            }

            long targetSamples = (long)(posMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
            pipeline.SetDecodedSamplesPosition(targetSamples);
            pipeline.StartDecoding(CreateUrlRefresher(), _options, OnTrackEnded, HandleError);

            if (wasPlaying && _state != PlayerState.Paused)
            {
                // Почему BufferProgress > 95%: preload заполнил почти весь трек,
                // target-чанк с высокой вероятностью уже доступен без HTTP.
                // Threshold 20мс достаточен — decoder выдаст первые сэмплы
                // за 5-10мс (RAM/диск I/O).
                bool isFastSource = pipeline.Source.IsFullyBuffered
                    || pipeline.Source is Sources.LocalFileSource
                    || pipeline.Source.BufferProgress >= 95.0;

                int msThreshold = isFastSource
                    ? Math.Max(MinSeekResumeBufferMs / 4, 10)
                    : MinSeekResumeBufferMs;

                int seekThreshold = pipeline.SampleRate * pipeline.Channels * msThreshold / 1000;
                await pipeline.WaitForBufferAsync(
                    seekThreshold, SeekWarmupTimeoutMs, seekCt).ConfigureAwait(false);

                if (seekCt.IsCancellationRequested || _activePipeline != pipeline)
                {
                    cmd.Completion?.TrySetCanceled();
                    return;
                }

                _options.OnPipelineConfiguring?.Invoke(pipeline);

                pipeline.ActivateFillLoop();
                pipeline.Start();
                SetState(PlayerState.Playing);
            }
            else
            {
                SetState(PlayerState.Paused);
            }

            StartPositionTimerDelayed();
            _events.RaiseSeekCompleted(cmd.Position);
            cmd.Completion?.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            cmd.Completion?.TrySetCanceled();
            StartPositionTimerDelayed();
        }
        catch (AudioDeviceException ex)
        {
            Log.Error($"[AudioPlayer] Audio device lost during seek: {ex.Message}");
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Seek background error: {ex.Message}");
            cmd.Completion?.TrySetException(ex);
            SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);
            if (wasPlaying && _activePipeline == pipeline)
            {
                try { pipeline.Start(); }
                catch (AudioDeviceException devEx) { HandleError(devEx); return; }
            }
            StartPositionTimerDelayed();
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeSeekCts, null, seekCts);
            try { seekCts.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleDisposeAsync()
    {
        await HandleStopAsync();
        DisposeTimers();
        SetState(PlayerState.Disposed);
    }

    #endregion

    #region Timers

    private void StartTimers()
    {
        int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

        if (_positionTimer == null)
        {
            _positionTimer = new Timer(
                _ => _events.RaisePositionChanged(Position),
                null, 0, interval);
        }
        else
        {
            _positionTimer.Change(0, interval);
        }

        if (_bufferTimer == null)
        {
            _bufferTimer = new Timer(
                _ => RaiseBufferState(),
                null, 0, BufferStateUpdateIntervalMs);
        }
        else
        {
            _bufferTimer.Change(0, BufferStateUpdateIntervalMs);
        }
    }

    private void StopTimers()
    {
        _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _bufferTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void StartPositionTimerDelayed()
    {
        int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

        if (_positionTimer == null)
        {
            _positionTimer = new Timer(
                _ => _events.RaisePositionChanged(Position),
                null, interval, interval);
        }
        else
        {
            _positionTimer.Change(interval, interval);
        }
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Полная финализация таймеров. Вызывать только при Dispose.
    /// </summary>
    private void DisposeTimers()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
        _bufferTimer?.Dispose();
        _bufferTimer = null;
    }

    private void RaiseBufferState()
    {
        var pipeline = _activePipeline;
        if (pipeline == null) return;

        var source = pipeline.Source;
        var state = new BufferState(source.BufferProgress, source.IsFullyBuffered, source.GetBufferedRanges());
        _events.RaiseBufferState(state);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Обработчик естественного завершения трека (decoder дочитал EOF).
    /// </summary>
    /// <remarks>
    /// <para><b>КРИТИЧНО:</b> Cleanup (flush backend, dispose pipeline, stop timers)
    /// выполняется ЗДЕСЬ, ДО <see cref="SetState"/>(<see cref="PlayerState.Idle"/>).</para>
    ///
    /// <para><b>Почему:</b> <see cref="AudioEngine.HandlePlayerTrackEnded"/> вызывает
    /// <see cref="Stop"/> который гвардится на <c>_state == Idle</c>.
    /// Если SetState(Idle) выполнен до Stop() — StopCommand никогда не отправляется,
    /// backend gate остаётся открытым, fill loop крутится вечно → starvation.</para>
    ///
    /// <para><b>Thread safety:</b> Вызывается из decoder thread.
    /// <see cref="StopTimers"/> — Timer.Change(Infinite) thread-safe.
    /// <see cref="IPlaybackBackend.Flush"/> — защищён _stateLock в NAudioBackend.
    /// Pipeline dispose — через fire-and-forget Task.Run.</para>
    /// </remarks>
    private void OnTrackEnded()
    {
        var currentState = _state;
        if (currentState is PlayerState.Idle or PlayerState.Disposed
            or PlayerState.Loading or PlayerState.Buffering or PlayerState.Seeking)
        {
            return;
        }

        var pipeline = _activePipeline;
        Log.Info($"[AudioPlayer] Track ended: {_currentTrackId}, " +
                 $"pos={pipeline?.Source.PositionMs ?? 0}ms/{pipeline?.Source.DurationMs ?? 0}ms");

        // CLEANUP ДО SetState(Idle)
        // Если SetState(Idle) выполнить первым — AudioEngine.Stop() → _player.Stop()
        // увидит Idle и вернётся без отправки StopCommand.
        // HandleStopAsync никогда не вызовется → утечка pipeline + backend + timers.
        StopTimers();
        _sharedBackend.Flush();

        var oldPipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (oldPipeline != null)
        {
            _ = Task.Run(async () =>
            {
                try { await oldPipeline.DisposeAsync(); }
                catch (Exception ex) { Log.Debug($"[AudioPlayer] Track-end dispose: {ex.Message}"); }
            });
        }

        _currentTrackId = null;

        _events.RaiseTrackEnded();
        SetState(PlayerState.Idle);
    }

    private void HandleError(Exception ex)
    {
        SetState(PlayerState.Error);

        string message = ex switch
        {
            AudioDeviceException or NAudio.MmException
                => GetDeviceErrorMessage(),
            CacheInvalidatedException
                => LocalizationService.Instance.Get("Error_CacheInvalidated", "Track cache was deleted. Playback stopped."),
            _ => ex.Message
        };

        _events.RaiseError(new AudioPlayerError(message, ex));
    }

    /// <summary>Возвращает локализованное сообщение об отсутствии аудиоустройства.</summary>
    private static string GetDeviceErrorMessage() =>
        LocalizationService.Instance.Get("Error_NoAudioDevice", "Audio output device is not available. Please connect headphones or speakers.");

    private void SetState(PlayerState newState)
    {
        var oldState = _state;
        if (oldState == newState) return;
        if (!PlayerStateTransitions.CanTransition(oldState, newState)) return;

        _state = newState;
        _events.RaiseStateChanged(MapState(newState));
    }

    private static PlaybackState MapState(PlayerState state) => state switch
    {
        PlayerState.Idle => PlaybackState.Stopped,
        PlayerState.Loading => PlaybackState.Loading,
        PlayerState.Buffering => PlaybackState.Buffering,
        PlayerState.Playing => PlaybackState.Playing,
        PlayerState.Paused => PlaybackState.Paused,
        PlayerState.Seeking => PlaybackState.Playing,
        PlayerState.Error => PlaybackState.Error,
        PlayerState.Disposed => PlaybackState.Stopped,
        _ => PlaybackState.Stopped
    };

    private Func<CancellationToken, Task<string?>>? CreateUrlRefresher()
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId)) return null;

        var trackId = _currentTrackId;

        // Reuse делегата пока trackId не изменился — исключает аллокацию замыкания
        // на каждый вызов (StartDecoding, HandleSeekAsync).
        if (_cachedUrlRefresher != null && _cachedUrlRefresherTrackId == trackId)
            return _cachedUrlRefresher;

        var callback = _options.UrlRefreshCallback;
        _cachedUrlRefresherTrackId = trackId;
        _cachedUrlRefresher = ct => callback(trackId, ct).AsTask();
        return _cachedUrlRefresher;
    }

    #endregion

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _activePipeline;

    public double BufferProgress => _activePipeline?.Source.BufferProgress ?? 0;
    public bool IsFullyBuffered => _activePipeline?.Source.IsFullyBuffered ?? false;

    public long GetDownloadedBytes()
    {
        var pipeline = _activePipeline;
        if (pipeline == null) return 0;

        return pipeline.Source switch
        {
            Sources.CachingStreamSource caching => caching.DownloadedBytes,
            Sources.LocalFileSource => pipeline.Source.IsFullyBuffered
                ? pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte
                : 0,
            _ => (long)(pipeline.Source.BufferProgress / 100.0
                * pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte)
        };
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() =>
        _activePipeline?.Source.GetBufferedRanges() ?? [];

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _commandChannel.Writer.TryWrite(new DisposeCommand(int.MaxValue));
        _lifetimeCts.Cancel();

        try
        {
            _commandProcessorTask.Wait(TimeSpan.FromSeconds(DisposeTaskTimeoutSec));
        }
        catch { }

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _commandChannel.Writer.WriteAsync(new DisposeCommand(int.MaxValue));
        _lifetimeCts.Cancel();

        try
        {
            await _commandProcessorTask.WaitAsync(TimeSpan.FromSeconds(DisposeTaskTimeoutSec));
        }
        catch { }

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}