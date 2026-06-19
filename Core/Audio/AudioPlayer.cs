using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Аудио плеер с акторной моделью обработки команд.
/// </summary>
/// <remarks>
/// <para><b>Декомпозиция (partial classes):</b></para>
/// <list type="bullet">
///   <item><c>AudioPlayer.cs</c> — ядро: command loop, state machine, play/stop/resume.</item>
///   <item><c>AudioPlayer.Seek.cs</c> — двухфазный seek.</item>
///   <item><c>AudioPlayer.DeviceRecovery.cs</c> — восстановление после потери устройства.</item>
///   <item><c>AudioPlayer.Timers.cs</c> — управление таймерами UI-обновлений.</item>
/// </list>
/// </remarks>
public sealed partial class AudioPlayer : IAsyncDisposable, IDisposable
{
    #region Constants

    private const int CommandChannelCapacity = 32;
    private const int StopTaskTimeoutSec = 2;
    private const int DisposeTaskTimeoutSec = 2;
    private const int ResumeMinBufferMs = 100;
    private const int ResumeWarmupTimeoutMs = 500;
    private const int PlayBufferWaitTimeoutMs = 5000;
    private const int BitsPerByte = 8;

    #endregion

    /// <summary>
    /// Пользовательское намерение относительно playback lifecycle.
    /// Отделено от operational state machine.
    /// </summary>
    private enum PlaybackIntent
    {
        Stop = 0,
        Pause = 1,
        Play = 2
    }

    #region Fields

    private readonly AudioPlayerOptions _options;
    private readonly AudioPlayerEvents _events = new();
    private readonly Channel<IAudioCommand> _commandChannel;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _commandProcessorTask;
    private readonly IPlaybackBackend _sharedBackend;

    private volatile AudioPipeline? _activePipeline;
    private volatile PlayerState _state = PlayerState.Idle;
    private volatile bool _disposed;
    private int _playbackIntent = (int)PlaybackIntent.Stop;
    private int _seekGeneration;

    /// <summary>
    /// Task dispose'а предыдущего pipeline, запущенного fire-and-forget в
    /// <see cref="HandlePlayAsync"/>
    /// Хранится для детерминированного ожидания в <see cref="HandleDisposeAsync"/>:
    /// без этого <c>_sharedBackend.Dispose()</c> может выполниться пока
    /// <c>oldPipeline.DisposeAsync()</c> ещё обращается к backend'у.
    /// Только один «в полёте» в любой момент — атомарно заменяется через
    /// <see cref="Interlocked.Exchange{T}(ref T,T)"/>.
    /// </summary>
    private Task? _oldPipelineDisposeTask;

    private SessionGuard _session;
    private string? _currentTrackId;
    private Func<CancellationToken, Task<string?>>? _cachedUrlRefresher;
    private string? _cachedUrlRefresherTrackId;

    private readonly SharedPlaybackState _sharedState = new();

    /// <summary>
    /// Последнее считанное из драйвера значение физически воспроизведённых сэмплов.
    /// Используется для детекции шага дискретизации аппаратного буфера и сброса экстраполяции.
    /// </summary>
    private long _lastRawPlayedSamples = -1;

    #endregion

    #region Properties

    /// <summary>События плеера.</summary>
    public AudioPlayerEvents Events => _events;

    /// <summary>Текущее детальное состояние плеера.</summary>
    public PlayerState DetailedState => _state;

    /// <summary>Текущая позиция воспроизведения с суб-миллисекундной экстраполяцией времени [2, 3].</summary>
    public TimeSpan Position
    {
        get
        {
            var pipeline = _activePipeline;
            if (pipeline == null) return TimeSpan.Zero;

            int bufferedSamples = pipeline.BackendBufferedSamples;
            long played = Math.Max(0, pipeline.PlayedSamples - bufferedSamples);

            if (played != _lastRawPlayedSamples)
            {
                _lastRawPlayedSamples = played;
                _sharedState.Update(
                    played,
                    bufferedSamples, // <-- Transferring hardware buffer constraint
                    pipeline.SampleRate,
                    pipeline.Channels,
                    _state == PlayerState.Playing,
                    (long)Duration.TotalMilliseconds);
            }

            return _sharedState.GetCurrentPosition();
        }
    }

    /// <summary>Общая длительность трека.</summary>
    public TimeSpan Duration => _activePipeline != null
        ? TimeSpan.FromMilliseconds(_activePipeline.StreamInfo.DurationMs)
        : TimeSpan.Zero;

    /// <summary>Текущее состояние.</summary>
    public PlaybackState State => MapState(_state);

    /// <summary>Информация о потоке.</summary>
    public AudioStreamInfo StreamInfo => _activePipeline?.StreamInfo ?? AudioStreamInfo.Empty;

    #endregion

    #region Constructor

    /// <summary>Создаёт плеер.</summary>
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
    }

    private static IPlaybackBackend CreateSharedBackend(AudioPlayerOptions options)
    {
        if (options.UseNullBackend) return new Backends.NullAudioBackend();
        try { return new Backends.NAudioBackend(); }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] NAudio failed: {ex.Message}, using NullBackend");
            return new Backends.NullAudioBackend();
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Инициирует запуск воспроизведения.
    /// Мгновенно отменяет любые фоновые операции перемещения (seek) для предотвращения зависания актора.
    /// </summary>
    private void Play(
        string url,
        string? trackId = null,
        int bitrateHint = 0,
        TimeSpan? seekPosition = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SetPlaybackIntent(PlaybackIntent.Play);
        CancelActiveSeek();

        int session = _session.BeginNew();
        _currentTrackId = trackId;
        _lastRawPlayedSamples = -1;

        _commandChannel.Writer.TryWrite(
            new PlayCommand(url, trackId, bitrateHint, session, seekPosition, ct));
    }

    /// <summary>Запускает воспроизведение и ожидает состояния Playing.</summary>
    /// <param name="url">URL аудио потока.</param>
    /// <param name="trackId">ID трека для обновления URL (опционально).</param>
    /// <param name="bitrateHint">Подсказка битрейта потока.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <param name="seekPosition">
    /// Позиция для seek перед стартом воспроизведения.
    /// null = воспроизведение с начала.
    /// </param>
    /// <returns>Задача, завершающаяся при переходе плеера в Playing либо ошибке/отмене.</returns>
    public Task PlayAsync(
        string url,
        string? trackId = null,
        int bitrateHint = 0,
        CancellationToken ct = default,
        TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(PlaybackState s)
        {
            if (s == PlaybackState.Playing)
                tcs.TrySetResult(true);
        }

        void OnError(AudioPlayerError e)
        {
            tcs.TrySetException(e.Exception ?? new Exception(e.Message));
        }

        _events.StateChanged += OnState;
        _events.ErrorOccurred += OnError;

        var reg = ct.UnsafeRegister(static state =>
            ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);

        tcs.Task.ContinueWith((_, state) =>
        {
            var ctx = (PlayAsyncContext)state!;
            ctx.Player._events.StateChanged -= ctx.OnState;
            ctx.Player._events.ErrorOccurred -= ctx.OnError;
            ctx.Reg.Dispose();
        },
        new PlayAsyncContext(this, OnState, OnError, reg),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

        Play(url, trackId, bitrateHint, seekPosition, ct);
        return tcs.Task;
    }

    private sealed record PlayAsyncContext(
        AudioPlayer Player,
        Action<PlaybackState> OnState,
        Action<AudioPlayerError> OnError,
        CancellationTokenRegistration Reg);

    /// <summary>Приостанавливает воспроизведение.</summary>
    public void Pause()
    {
        if (_disposed) return;

        SetPlaybackIntent(PlaybackIntent.Pause);
        CancelActiveSeek();
        _commandChannel.Writer.TryWrite(new PauseCommand(_session.Current));
    }

    /// <summary>Возобновляет воспроизведение после паузы.</summary>
    public void Resume()
    {
        if (_disposed) return;

        SetPlaybackIntent(PlaybackIntent.Play);
        _commandChannel.Writer.TryWrite(new ResumeCommand(_session.Current));
    }

    /// <summary>
    /// Останавливает воспроизведение.
    /// Гарантирует мгновенную разблокировку UI-потока при зависшем скачивании.
    /// </summary>
    public void Stop()
    {
        if (_state is PlayerState.Idle or PlayerState.Disposed) return;

        SetPlaybackIntent(PlaybackIntent.Stop);
        CancelActiveSeek();
        _lastRawPlayedSamples = -1;

        _commandChannel.Writer.TryWrite(new StopCommand(_session.BeginNew()));
    }

    /// <summary>Асинхронный останов.</summary>
    public async Task StopAsync()
    {
        if (_state is PlayerState.Idle or PlayerState.Disposed) return;

        SetPlaybackIntent(PlaybackIntent.Stop);

        int session = _session.BeginNew();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(PlaybackState s)
        {
            if (s != PlaybackState.Stopped) return;
            _events.StateChanged -= OnState;
            tcs.TrySetResult(true);
        }

        _events.StateChanged += OnState;
        await _commandChannel.Writer.WriteAsync(new StopCommand(session)).ConfigureAwait(false);

        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(StopTaskTimeoutSec)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _events.StateChanged -= OnState;
        }
    }

    /// <summary>Немедленно применяет volume gain.</summary>
    public void SetVolumeGain(float gain) => _sharedBackend.SetVolumeGain(gain);

    #endregion

    #region Command Processing

    /// <summary>
    /// Основной акторный цикл обработки команд плеера.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var command in _commandChannel.Reader
                .ReadAllAsync(_lifetimeCts.Token)
                .ConfigureAwait(false))
            {
                if (_session.IsStale(command.SessionId) && command is not DisposeCommand)
                {
                    if (command is SeekCommand { Completion: { } tcs })
                        tcs.TrySetCanceled();

                    continue;
                }

                if (!PlayerStateTransitions.CanAcceptCommand(_state, command))
                {
                    Log.Debug($"[AudioPlayer] Command {command.GetType().Name} rejected in state {_state}");

                    if (command is SeekCommand { Completion: { } failTcs })
                        failTcs.TrySetResult(false);

                    continue;
                }

                try
                {
                    switch (command)
                    {
                        case PlayCommand play:
                            await HandlePlayAsync(play).ConfigureAwait(false);
                            break;

                        case StopCommand:
                            await HandleStopAsync().ConfigureAwait(false);
                            break;

                        case PauseCommand:
                            await HandlePauseAsync().ConfigureAwait(false);
                            break;

                        case StarvationCommand starvation:
                            await HandleStarvationAsync(starvation).ConfigureAwait(false);
                            break;

                        case ResumeCommand resume:
                            await HandleResumeAsync(resume).ConfigureAwait(false);
                            break;

                        case SeekCommand seek:
                            await HandleSeekAsync(seek).ConfigureAwait(false);
                            break;

                        case DeferredResumeCommand deferredResume:
                            await HandleDeferredResumeAsync(deferredResume).ConfigureAwait(false);
                            break;

                        case TrackEndedCommand trackEnded:
                            await HandleTrackEndedAsync(trackEnded).ConfigureAwait(false);
                            break;

                        case DeviceLostCommand deviceLost:
                            await HandleDeviceLostAsync(deviceLost).ConfigureAwait(false);
                            break;

                        case DeviceAvailableCommand deviceAvailable:
                            await HandleDeviceAvailableAsync(deviceAvailable).ConfigureAwait(false);
                            break;

                        case DeviceRecoveryCommand recovery:
                            await HandleDeviceRecoveryAsync(recovery).ConfigureAwait(false);
                            break;

                        case PlayerErrorCommand error:
                            await HandlePlayerErrorAsync(error).ConfigureAwait(false);
                            break;

                        case DisposeCommand:
                            await HandleDisposeAsync().ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (CancellationHelper.IsCancellationLike(ex) || _session.IsStale(command.SessionId))
                        continue;

                    Log.Error($"[AudioPlayer] Command error: {ex.Message}", ex);
                    HandleError(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task HandlePauseAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Pause);
        StopTimers();

        var pipeline = _activePipeline;
        if (pipeline != null)
            pipeline.Stop();

        if (_state is not (PlayerState.Idle or PlayerState.Disposed))
            SetState(PlayerState.Paused);

        return Task.CompletedTask;
    }

    private async Task HandleResumeAsync(ResumeCommand cmd)
    {
        SetPlaybackIntent(PlaybackIntent.Play);

        var pipeline = _activePipeline;
        if (pipeline == null) return;

        if (pipeline.IsDeviceLost)
        {
            SetState(PlayerState.Buffering);
            await HandleDeviceRecoveryAsync(new DeviceRecoveryCommand(cmd.SessionId)).ConfigureAwait(false);
            return;
        }

        int minBytes = pipeline.SampleRate * pipeline.Channels * sizeof(float) * ResumeMinBufferMs / 1000;
        if (_sharedBackend.BufferedBytes < minBytes)
        {
            pipeline.ActivateFillLoop();
            pipeline.WaitForBackendWarmup(ResumeWarmupTimeoutMs);
        }

        ResumePlaybackSequence(pipeline, startTimers: true, configurePipeline: false, trackId: _currentTrackId);
    }

    private async Task HandleStopAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        CancelActiveSeek();
        StopTimers();

        var pipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (pipeline != null) await pipeline.DisposeAsync().ConfigureAwait(false);

        _sharedBackend.Flush();
        _currentTrackId = null;
        SetState(PlayerState.Idle);
    }

    /// <summary>
    /// Финализирующий handler dispose-команды.
    /// </summary>
    private async Task HandleDisposeAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        await HandleStopAsync().ConfigureAwait(false);

        var oldDisposeTask = Volatile.Read(ref _oldPipelineDisposeTask);
        if (oldDisposeTask != null && !oldDisposeTask.IsCompleted)
        {
            try
            {
                await oldDisposeTask
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn("[AudioPlayer] Old pipeline dispose did not complete within HandleDisposeAsync timeout");
            }
            catch
            {
            }
        }

        DisposeTimers();
        SetState(PlayerState.Disposed);
    }

    /// <summary>
    /// Единая финализирующая последовательность запуска воспроизведения.
    /// </summary>
    /// <param name="pipeline">Pipeline для запуска.</param>
    /// <param name="startTimers">Запускать ли таймеры UI-обновлений.</param>
    /// <param name="configurePipeline">Вызывать ли <see cref="AudioPlayerOptions.OnPipelineConfiguring"/>.</param>
    /// <param name="trackId">ID трека — пробрасывается в <c>OnPipelineConfiguring</c> для привязки gain.</param>
    private void ResumePlaybackSequence(AudioPipeline pipeline, bool startTimers, bool configurePipeline, string? trackId = null)
    {
        if (configurePipeline) _options.OnPipelineConfiguring?.Invoke(pipeline, trackId);
        pipeline.ActivateFillLoop();
        pipeline.Start();
        if (startTimers) StartTimers();
        SetState(PlayerState.Playing);
    }

    /// <summary>
    /// Общая подготовка pipeline перед стартом декодирования.
    /// </summary>
    private async Task PrepareAndStartDecodingAsync(
        AudioPipeline pipeline, PlayCommand cmd, CancellationToken ct)
    {
        await pipeline.PreScanNormalizationAsync(ct).ConfigureAwait(false);

        if (cmd.SeekPosition is { TotalMilliseconds: > 0 } seekPos)
        {
            long seekMs = (long)seekPos.TotalMilliseconds;
            pipeline.PrepareForSeek(seekMs);

            if (await pipeline.Source.SeekAsync(seekMs, ct).ConfigureAwait(false))
            {
                pipeline.SetDecodedSamplesPosition(
                    (long)(seekMs / 1000.0 * pipeline.SampleRate * pipeline.Channels));
            }
        }

        pipeline.StartDecoding(
            CreateUrlRefresher(),
            _options,
            CreateTrackEndedCallback(cmd.SessionId),
            CreateErrorCallback(cmd.SessionId, pipeline));
    }

    private async Task HandlePlayAsync(PlayCommand cmd)
    {
        SetPlaybackIntent(PlaybackIntent.Play);
        SetState(PlayerState.Loading);
        CancelActiveSeek();

        ResetPerTrackCounters();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token, cmd.ExternalCancellationToken);
        var ct = linkedCts.Token;

        var oldPipeline = Interlocked.Exchange(ref _activePipeline, null);
        float previousGain = oldPipeline?.GetLockedNormalizationGain() ?? 1.0f;

        if (oldPipeline != null) TrackAndFirePipelineDispose(oldPipeline);

        StopTimers();
        _lastRawPlayedSamples = -1;

        try
        {
            ct.ThrowIfCancellationRequested();

            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Url, cmd.TrackId, cmd.BitrateHint, CreateUrlRefresher(),
                _options, _sharedBackend, ct).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId))
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();

            _events.RaiseStreamInfo(pipeline.StreamInfo);
            var replaced = Interlocked.Exchange(ref _activePipeline, pipeline);
            if (replaced != null) await replaced.DisposeAsync().ConfigureAwait(false);

            var capturedSession = cmd.SessionId;
            pipeline.SetDeviceLostHandler(() => OnPipelineDeviceLost(pipeline, capturedSession));
            pipeline.SetDeviceAvailableHandler(() => OnPipelineDeviceAvailable(pipeline, capturedSession));

            pipeline.SetInitialNormalizationGain(previousGain);
            _options.OnPipelineConfiguring?.Invoke(pipeline, cmd.TrackId);

            var lockedTrackId = cmd.TrackId;
            if (lockedTrackId != null && _options.OnGainLocked != null)
            {
                var cb = _options.OnGainLocked;
                pipeline.Analyzer.SetGainLockedCallback(g => cb(lockedTrackId, g));
            }

            if (pipeline.IsDeviceLost)
            {
                await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
                SetState(PlayerState.Paused);
                _events.RaiseDeviceLost();
                return;
            }

            await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
            SetState(PlayerState.Buffering);

            int threshold = pipeline.SampleRate * pipeline.Channels * MinBufferMs / 1000;
            bool warmupReady = await pipeline.WaitForBufferAsync(
                threshold, PlayBufferWaitTimeoutMs, ct).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId))
            {
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null) await stale.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();

            if (CurrentPlaybackIntent != PlaybackIntent.Play)
            {
                pipeline.Stop();
                SetState(PlayerState.Paused);
                return;
            }

            if (warmupReady || pipeline.BufferedSamples >= threshold)
            {
                ResumePlaybackSequence(pipeline, startTimers: true, configurePipeline: false);
            }
            else
            {
                // Данных ещё нет (медленная сеть). Остаёмся в Buffering.
                // Decoder уже запущен и ждёт данных из сети.
                // Deferred resume автоматически откроет gate когда ring buffer наполнится.
                Log.Warn($"[AudioPlayer] Initial warmup timed out " +
                         $"(ring={pipeline.BufferedSamples}, threshold={threshold}). " +
                         "Staying in Buffering for deferred resume.");

                _options.OnPipelineConfiguring?.Invoke(pipeline, cmd.TrackId);
                pipeline.ActivateBufferingMode();

                int seekGeneration = Volatile.Read(ref _seekGeneration);
                var deferredResumeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                var previousCts = Interlocked.Exchange(ref _deferredResumeCts, deferredResumeCts);
                CancelCtsAsync(previousCts);

                _ = AwaitDeferredSeekBufferAndResumeAsync(
                    pipeline, threshold, cmd.SessionId, seekGeneration, deferredResumeCts);
            }

            _ = WatchPipelineLifetimeAsync(pipeline, cmd.SessionId);
        }
        catch (OperationCanceledException)
        {
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (NAudio.MmException ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(GetDeviceErrorMessage(), ex));
        }
        catch (Exception ex) when (
            cmd.ExternalCancellationToken.IsCancellationRequested ||
            _session.IsStale(cmd.SessionId) ||
            CancellationHelper.IsCancellationLike(ex))
        {
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (Exception ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
        }
    }

    private async Task WatchPipelineLifetimeAsync(AudioPipeline pipeline, int sessionId)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, pipeline.LifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_disposed || _session.IsStale(sessionId) || _activePipeline != pipeline) return;

            try
            {
                _commandChannel.Writer.TryWrite(new PlayerErrorCommand(
                    sessionId,
                    pipeline,
                    new AudioDeviceException(GetDeviceErrorMessage())));

                _commandChannel.Writer.TryWrite(new StopCommand(sessionId));
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Запускает dispose предыдущего pipeline на ThreadPool и сохраняет Task
    /// для детерминированного ожидания в shutdown-пути.
    /// </summary>
    /// <remarks>
    /// Не await'ируем inline — dispose старого pipeline не должен блокировать
    /// запуск нового. Но мы обязаны дождаться завершения перед dispose backend'а.
    /// </remarks>
    /// <param name="pipeline">Pipeline, подлежащий async dispose.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void TrackAndFirePipelineDispose(AudioPipeline pipeline)
    {
        var disposeTask = Task.Run(async () =>
        {
            try { await pipeline.DisposeAsync().ConfigureAwait(false); }
            catch { /* dispose никогда не должен бросать наружу */ }
        });

        // Атомарная замена: если предыдущий dispose ещё не завершился,
        // мы просто потеряем ссылку — это допустимо, т.к. оба завершатся
        // до _sharedBackend.Dispose() через ожидание в HandleDisposeAsync.
        Interlocked.Exchange(ref _oldPipelineDisposeTask, disposeTask);
    }

    private Task HandleTrackEndedAsync(TrackEndedCommand cmd)
    {
        if (_state is not (PlayerState.Buffering or PlayerState.Playing or PlayerState.Paused or PlayerState.Seeking))
            return Task.CompletedTask;

        if (_activePipeline == null)
            return Task.CompletedTask;

        SetPlaybackIntent(PlaybackIntent.Stop);
        StopTimers();
        _sharedBackend.Flush();

        _events.RaiseTrackEnded();

        _currentTrackId = null;

        var old = Interlocked.Exchange(ref _activePipeline, null);
        if (old != null)
            TrackAndFirePipelineDispose(old);

        SetState(PlayerState.Idle);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Обрабатывает starvation (опустошение ring buffer) внутри actor loop.
    /// Переводит в Buffering и запускает deferred resume.
    /// </summary>
    /// <remarks>
    /// <para>В отличие от старого подхода (CancelActiveReads + restart),
    /// этот handler сохраняет decoder и source активными.
    /// На медленной сети HTTP-запрос может идти 5–16 секунд,
    /// и его отмена контрпродуктивна — данные УЖЕ качаются.</para>
    /// <para>Gate закрывается (тишина вместо glitch), decoder продолжает
    /// заполнять ring buffer, и deferred resume автоматически откроет gate.</para>
    /// </remarks>
    private Task HandleStarvationAsync(StarvationCommand cmd)
    {
        if (_disposed || _activePipeline != cmd.Pipeline || _session.IsStale(cmd.SessionId))
            return Task.CompletedTask;

        if (_state != PlayerState.Playing)
            return Task.CompletedTask;

        if (CurrentPlaybackIntent != PlaybackIntent.Play)
            return Task.CompletedTask;

        Log.Warn("[AudioPlayer] Starvation — closing gate for auto-rebuffer");

        var pipeline = cmd.Pipeline;

        // Закрываем gate (тишина), но оставляем decoder + source живыми
        pipeline.Stop();
        StopTimers();

        SetState(PlayerState.Buffering);
        pipeline.ActivateBufferingMode();

        // Deferred resume: когда ring buffer наберёт минимум — gate откроется автоматически
        int seekGeneration = Volatile.Read(ref _seekGeneration);
        int threshold = pipeline.SampleRate * pipeline.Channels * ResumeMinBufferMs / 1000;

        var deferredResumeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var previousCts = Interlocked.Exchange(ref _deferredResumeCts, deferredResumeCts);
        CancelCtsAsync(previousCts);

        _ = AwaitDeferredSeekBufferAndResumeAsync(
            pipeline, threshold, cmd.SessionId, seekGeneration, deferredResumeCts);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Обработчик starvation от pipeline. Публикует команду в actor loop.
    /// </summary>
    private void OnPipelineStarvation(AudioPipeline pipeline, int sessionId)
    {
        if (_disposed || _activePipeline != pipeline) return;
        if (_session.IsStale(sessionId)) return;

        _commandChannel.Writer.TryWrite(new StarvationCommand(sessionId, pipeline));
    }

    /// <summary>
    /// Обрабатывает фоновую ошибку внутри actor loop.
    /// </summary>
    private Task HandlePlayerErrorAsync(PlayerErrorCommand cmd)
    {
        if (_disposed || _activePipeline != cmd.Pipeline || _session.IsStale(cmd.SessionId))
            return Task.CompletedTask;

        if (CancellationHelper.IsCancellationLike(cmd.Error))
            return Task.CompletedTask;

        HandleError(cmd.Error);
        return Task.CompletedTask;
    }

    private void HandleError(Exception ex)
    {
        SetState(PlayerState.Error);
        string message = ex switch
        {
            AudioDeviceException or NAudio.MmException => GetDeviceErrorMessage(),
            CacheInvalidatedException => LocalizationService.Instance.Get(
                "Error_CacheInvalidated", "Track cache was deleted. Playback stopped."),
            _ => ex.Message
        };
        _events.RaiseError(new AudioPlayerError(message, ex));
    }

    private static string GetDeviceErrorMessage() =>
        LocalizationService.Instance.Get("Error_NoAudioDevice",
            "Audio output device is not available. Please connect headphones or speakers.");

    private bool SetState(PlayerState newState, [CallerMemberName] string? caller = null)
    {
        var oldState = _state;
        if (oldState == newState) return true;

        if (!PlayerStateTransitions.CanTransition(oldState, newState))
        {
            Log.Error($"[AudioPlayer] Invalid state transition: {oldState} -> {newState}. " +
                      $"Caller={caller}, Session={_session.Current}, SeekGeneration={Volatile.Read(ref _seekGeneration)}, " +
                      $"Intent={CurrentPlaybackIntent}");
            return false;
        }

        _state = newState;
        _events.RaiseStateChanged(MapState(newState));
        return true;
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

    private void SetPlaybackIntent(PlaybackIntent intent) =>
        Volatile.Write(ref _playbackIntent, (int)intent);

    private PlaybackIntent CurrentPlaybackIntent =>
        (PlaybackIntent)Volatile.Read(ref _playbackIntent);

    private Func<CancellationToken, Task<string?>>? CreateUrlRefresher()
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId)) return null;
        var trackId = _currentTrackId;

        if (_cachedUrlRefresher != null && _cachedUrlRefresherTrackId == trackId)
            return _cachedUrlRefresher;

        var callback = _options.UrlRefreshCallback;
        _cachedUrlRefresherTrackId = trackId;
        _cachedUrlRefresher = ct => callback(trackId, ct).AsTask();
        return _cachedUrlRefresher;
    }

    /// <summary>
    /// Создаёт session-bound callback естественного завершения трека.
    /// </summary>
    private Action CreateTrackEndedCallback(int sessionId)
    {
        return () =>
        {
            if (_disposed) return;
            _commandChannel.Writer.TryWrite(new TrackEndedCommand(sessionId));
        };
    }

    /// <summary>
    /// Создаёт session-bound callback фоновой ошибки.
    /// </summary>
    /// <param name="sessionId">Сессия, в рамках которой запущен pipeline.</param>
    /// <param name="pipeline">Pipeline, из которого может прийти ошибка.</param>
    /// <returns>Callback, публикующий <see cref="PlayerErrorCommand"/> в actor channel.</returns>
    private Action<Exception> CreateErrorCallback(int sessionId, AudioPipeline pipeline)
    {
        return ex =>
        {
            if (_disposed) return;
            _commandChannel.Writer.TryWrite(new PlayerErrorCommand(sessionId, pipeline, ex));
        };
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
                ? pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte : 0,
            _ => (long)(pipeline.Source.BufferProgress / 100.0
                * pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte)
        };
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() =>
        _activePipeline?.Source.GetBufferedRanges() ?? [];

    #endregion

    #region Dispose

    /// <inheritdoc/>
    /// <remarks>
    /// Синхронный Dispose — FALLBACK shutdown path.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Отменяем любые сетевые операции перемещения немедленно, чтобы не блокировать поток утилизации [2]
        CancelActiveSeek();

        _commandChannel.Writer.TryWrite(new DisposeCommand(int.MaxValue));
        _lifetimeCts.Cancel();

        try { _commandProcessorTask.Wait(TimeSpan.FromSeconds(DisposeTaskTimeoutSec)); }
        catch { }

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Асинхронный Dispose — PRIMARY shutdown path.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Отменяем любые сетевые операции перемещения немедленно [2]
        CancelActiveSeek();

        await _commandChannel.Writer.WriteAsync(new DisposeCommand(int.MaxValue))
            .ConfigureAwait(false);

        _commandChannel.Writer.TryComplete();

        try
        {
            await _commandProcessorTask
                .WaitAsync(TimeSpan.FromSeconds(DisposeTaskTimeoutSec))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("[AudioPlayer] Command processor did not finish within dispose timeout");
        }
        catch { }

        _lifetimeCts.Cancel();

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }


    #endregion
}