using System.Threading.Channels;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Models;
using LMP.Core.Services;
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

    /// <summary>
    /// Task dispose'а предыдущего pipeline, запущенного fire-and-forget в
    /// <see cref="HandlePlayAsync"/> или <see cref="OnTrackEnded"/>.
    /// Хранится для детерминированного ожидания в <see cref="HandleDisposeAsync"/>:
    /// без этого <c>_sharedBackend.Dispose()</c> может выполниться пока
    /// <c>oldPipeline.DisposeAsync()</c> ещё обращается к backend'у.
    /// Только один «в полёте» в любой момент — атомарно заменяется через
    /// <see cref="Interlocked.Exchange{T}"/>.
    /// </summary>
    private Task? _oldPipelineDisposeTask;

    private SessionGuard _session;
    private string? _currentTrackId;
    private Func<CancellationToken, Task<string?>>? _cachedUrlRefresher;
    private string? _cachedUrlRefresherTrackId;


    #endregion

    #region Properties

    /// <summary>События плеера.</summary>
    public AudioPlayerEvents Events => _events;

    /// <summary>Текущая позиция воспроизведения.</summary>
    public TimeSpan Position
    {
        get
        {
            var pipeline = _activePipeline;
            if (pipeline == null) return TimeSpan.Zero;
            long played = Math.Max(0, pipeline.PlayedSamples - pipeline.BackendBufferedSamples);
            double seconds = (double)played / (pipeline.SampleRate * pipeline.Channels);
            var dur = Duration;
            if (dur.TotalSeconds > 0 && seconds > dur.TotalSeconds) seconds = dur.TotalSeconds;
            return TimeSpan.FromSeconds(seconds);
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

    private void Play(string url, string? trackId = null, int bitrateHint = 0,
        TimeSpan? seekPosition = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int session = _session.BeginNew();
        _currentTrackId = trackId;
        SetState(PlayerState.Loading);
        _commandChannel.Writer.TryWrite(new PlayCommand(url, trackId, bitrateHint, session, seekPosition, ct));
    }

    /// <summary>Запускает воспроизведение и ожидает Playing.</summary>
    public Task PlayAsync(string url, string? trackId = null, int bitrateHint = 0,
        CancellationToken ct = default, TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(PlaybackState s) { if (s == PlaybackState.Playing) tcs.TrySetResult(true); }
        void OnError(AudioPlayerError e) { tcs.TrySetException(e.Exception ?? new Exception(e.Message)); }

        _events.StateChanged += OnState;
        _events.ErrorOccurred += OnError;

        var reg = ct.UnsafeRegister(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);

        tcs.Task.ContinueWith((_, state) =>
        {
            var ctx = (PlayAsyncContext)state!;
            ctx.Player._events.StateChanged -= ctx.OnState;
            ctx.Player._events.ErrorOccurred -= ctx.OnError;
            ctx.Reg.Dispose();
        }, new PlayAsyncContext(this, OnState, OnError, reg),
           CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        Play(url, trackId, bitrateHint, seekPosition, ct);
        return tcs.Task;
    }

    private sealed record PlayAsyncContext(
        AudioPlayer Player, Action<PlaybackState> OnState,
        Action<AudioPlayerError> OnError, CancellationTokenRegistration Reg);

    /// <summary>Пауза.</summary>
    public void Pause()
    {
        if (_state != PlayerState.Playing) return;
        SetState(PlayerState.Paused);
        _activePipeline?.Stop();
    }

    /// <summary>Возобновление.</summary>
    public void Resume()
    {
        if (_state != PlayerState.Paused) return;
        var pipeline = _activePipeline;
        if (pipeline == null) return;

        if (pipeline.IsDeviceLost)
        {
            SetState(PlayerState.Buffering);
            _commandChannel.Writer.TryWrite(new DeviceRecoveryCommand(_session.Current));
            return;
        }

        int minBytes = pipeline.SampleRate * pipeline.Channels * sizeof(float) * ResumeMinBufferMs / 1000;
        if (_sharedBackend.BufferedBytes < minBytes)
        {
            pipeline.ActivateFillLoop();
            pipeline.WaitForBackendWarmup(ResumeWarmupTimeoutMs);
        }

        SetState(PlayerState.Playing);
        pipeline.Start();
    }

    /// <summary>Останов.</summary>
    public void Stop()
    {
        if (_state is PlayerState.Idle or PlayerState.Disposed) return;
        _commandChannel.Writer.TryWrite(new StopCommand(_session.BeginNew()));
    }

    /// <summary>Асинхронный останов.</summary>
    public async Task StopAsync()
    {
        if (_state is PlayerState.Idle or PlayerState.Disposed) return;
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

        try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(StopTaskTimeoutSec)).ConfigureAwait(false); }
        catch (TimeoutException) { _events.StateChanged -= OnState; }
    }

    /// <summary>Немедленно применяет volume gain.</summary>
    public void SetVolumeGain(float gain) => _sharedBackend.SetVolumeGain(gain);

    #endregion

    #region Command Processing

    /// <summary>
    /// Основной акторный цикл обработки команд плеера.
    /// </summary>
    /// <remarks>
    /// <para>Все переходы state machine и lifecycle-операции над active pipeline
    /// выполняются только здесь. В том числе natural end-of-track теперь приходит
    /// как <see cref="TrackEndedCommand"/>, а не как прямой callback из decoder thread.</para>
    /// </remarks>
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

                        case SeekCommand seek:
                            await HandleSeekAsync(seek).ConfigureAwait(false);
                            break;

                        case TrackEndedCommand trackEnded:
                            await HandleTrackEndedAsync(trackEnded).ConfigureAwait(false);
                            break;

                        case DeviceRecoveryCommand recovery:
                            await HandleDeviceRecoveryAsync(recovery).ConfigureAwait(false);
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
    /// Извлечена из HandlePlayAsync для устранения дупликации degraded/normal path.
    /// </summary>
    /// <remarks>
    /// <para><b>Архитектура:</b> natural end-of-track callback создаётся session-bound
    /// и доставляется обратно в actor loop через <see cref="TrackEndedCommand"/>.</para>
    /// <para>Это сохраняет все переходы state machine строго в одном потоке команд
    /// и устраняет гонки, когда старый decoder loop поздно вызывал завершение уже
    /// после запуска нового pipeline.</para>
    /// </remarks>
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
            HandleError);
    }

    private async Task HandlePlayAsync(PlayCommand cmd)
    {
        SetState(PlayerState.Loading);
        CancelActiveSeek();

        // Сброс per-track observability счётчиков при смене трека
        ResetPerTrackCounters();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token, cmd.ExternalCancellationToken);
        var ct = linkedCts.Token;

        var oldPipeline = Interlocked.Exchange(ref _activePipeline, null);
        float previousGain = oldPipeline?.GetLockedNormalizationGain() ?? 1.0f;

        // Сохраняем Task dispose'а для детерминированного ожидания при shutdown.
        // Предыдущий _oldPipelineDisposeTask вытесняется — оба pipeline завершат
        // dispose до _sharedBackend.Dispose() через ожидание в HandleDisposeAsync.
        if (oldPipeline != null) TrackAndFirePipelineDispose(oldPipeline);

        StopTimers();

        try
        {
            ct.ThrowIfCancellationRequested();

            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Url, cmd.TrackId, cmd.BitrateHint, CreateUrlRefresher(),
                _options, _sharedBackend, ct).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId)) { await pipeline.DisposeAsync(); return; }
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

            // Degraded mode: устройство отсутствует
            if (pipeline.IsDeviceLost)
            {
                await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
                SetState(PlayerState.Paused);
                _events.RaiseDeviceLost();
                return;
            }

            // Normal path
            await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
            SetState(PlayerState.Buffering);

            int threshold = pipeline.SampleRate * pipeline.Channels * MinBufferMs / 1000;
            await pipeline.WaitForBufferAsync(threshold, PlayBufferWaitTimeoutMs, ct).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId))
            {
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null) await stale.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            ResumePlaybackSequence(pipeline, startTimers: true, configurePipeline: false);
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
        try { await Task.Delay(Timeout.Infinite, pipeline.LifetimeToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (_disposed || _session.IsStale(sessionId) || _activePipeline != pipeline) return;
            HandleError(new AudioDeviceException(GetDeviceErrorMessage()));
            try { await _commandChannel.Writer.WriteAsync(new StopCommand(_session.BeginNew())).ConfigureAwait(false); }
            catch (ChannelClosedException) { }
        }
    }

    private async Task HandleStopAsync()
    {
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
    /// Ожидает завершения предыдущего in-flight pipeline dispose ПЕРЕД
    /// освобождением таймеров — это закрывает гонку между
    /// <see cref="TrackAndFirePipelineDispose"/> и <see cref="DisposeTimers"/>.
    /// </summary>
    private async Task HandleDisposeAsync()
    {
        await HandleStopAsync().ConfigureAwait(false);

        // Ждём завершения in-flight dispose предыдущего pipeline.
        // Критично: _sharedBackend.Dispose() в Dispose/DisposeAsync вызывается
        // ПОСЛЕ HandleDisposeAsync — этот await закрывает гонку.
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
            catch { /* dispose task не должна пробрасывать, но страхуемся */ }
        }

        DisposeTimers();
        SetState(PlayerState.Disposed);
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

    /// <summary>
    /// Обрабатывает естественное завершение трека внутри actor loop.
    /// </summary>
    /// <param name="cmd">Команда завершения трека, привязанная к актуальной playback session.</param>
    /// <returns>Завершённая задача обработки.</returns>
    /// <remarks>
    /// <para><b>Почему обработка идёт через actor:</b></para>
    /// <para>Прежняя реализация вызывалась напрямую из decoder thread и могла
    /// сбросить уже новый pipeline поздним stale callback'ом. Теперь завершение
    /// трека сериализовано с Play/Stop/Seek и подчиняется тем же session guards.</para>
    /// <para><b>Порядок действий:</b></para>
    /// <list type="number">
    ///   <item>Останавливаем UI timers и flush'им backend.</item>
    ///   <item>Синхронно публикуем <see cref="AudioPlayerEvents.TrackEnded"/>,
    ///       пока metadata текущего трека ещё доступна обработчикам.</item>
    ///   <item>Только после этого обнуляем active pipeline и запускаем его dispose.</item>
    /// </list>
    /// </remarks>
    private Task HandleTrackEndedAsync(TrackEndedCommand cmd)
    {
        if (_state is not (PlayerState.Buffering or PlayerState.Playing or PlayerState.Paused))
            return Task.CompletedTask;

        if (_activePipeline == null)
            return Task.CompletedTask;

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

    /// <summary>Обработчик естественного завершения трека.</summary>
    private void OnTrackEnded()
    {
        if (_state is PlayerState.Idle or PlayerState.Disposed or PlayerState.Loading
            or PlayerState.Buffering or PlayerState.Seeking) return;

        StopTimers();
        _sharedBackend.Flush();

        var old = Interlocked.Exchange(ref _activePipeline, null);

        // Сохраняем Task — необходимо для детерминированного ожидания
        // в HandleDisposeAsync перед освобождением backend'а.
        if (old != null) TrackAndFirePipelineDispose(old);

        _currentTrackId = null;
        _events.RaiseTrackEnded();
        SetState(PlayerState.Idle);
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

    private void SetState(PlayerState newState)
    {
        if (_state == newState) return;
        if (!PlayerStateTransitions.CanTransition(_state, newState)) return;
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
    /// <param name="sessionId">Сессия playback, к которой привязан текущий decoder loop.</param>
    /// <returns>
    /// Делегат, безопасно публикующий <see cref="TrackEndedCommand"/> в actor channel.
    /// </returns>
    /// <remarks>
    /// <para>Callback намеренно не мутирует состояние плеера напрямую. Decoder loop
    /// работает на фоне, а <see cref="AudioPlayer"/> использует actor model.</para>
    /// <para>Session binding гарантирует, что late callback от старого pipeline будет
    /// автоматически отброшен как stale-команда.</para>
    /// </remarks>
    private Action CreateTrackEndedCallback(int sessionId)
    {
        return () =>
        {
            if (_disposed) return;
            _commandChannel.Writer.TryWrite(new TrackEndedCommand(sessionId));
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

    /// <summary>
    /// Синхронный Dispose — FALLBACK shutdown path.
    /// </summary>
    /// <remarks>
    /// Блокирует вызывающий поток не более <see cref="DisposeTaskTimeoutSec"/> секунд.
    /// Для вызова из UI-потока предпочтительнее <see cref="DisposeAsync"/>.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _commandChannel.Writer.TryWrite(new DisposeCommand(int.MaxValue));
        _lifetimeCts.Cancel();

        try { _commandProcessorTask.Wait(TimeSpan.FromSeconds(DisposeTaskTimeoutSec)); }
        catch { }

        // К этому моменту HandleDisposeAsync уже дождался _oldPipelineDisposeTask,
        // поэтому backend dispose безопасен.
        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Асинхронный Dispose — PRIMARY shutdown path.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Отправляем DisposeCommand и сразу complete writer —
        // ReadAllAsync завершится штатно после обработки последней команды,
        // не дожидаясь Cancel.
        await _commandChannel.Writer.WriteAsync(new DisposeCommand(int.MaxValue))
            .ConfigureAwait(false);

        // Complete сигнализирует ReadAllAsync что новых команд не будет.
        // Это первичный сигнал завершения loop — Cancel только fallback.
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

        // Cancel — fallback для незавершённых async операций внутри handlers
        _lifetimeCts.Cancel();

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}