using System.Threading.Channels;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Models;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

public sealed class AudioPlayerOptions
{
    public Func<string, CancellationToken, ValueTask<string?>>? UrlRefreshCallback { get; init; }
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPositionUpdateIntervalMs);
    public int MaxRetryAttempts { get; init; } = AudioConstants.MaxRetryAttempts;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(RetryDelayMs);
    public bool UseNullBackend { get; init; }

    /// <summary>
    /// Конфигурация стриминга. null = текущий глобальный профиль из <see cref="AudioSourceFactory"/>.
    /// </summary>
    public StreamingConfig? StreamingConfig { get; init; }
}

/// <summary>
/// Аудио плеер с акторной моделью обработки команд.
/// 
/// <para><b>Архитектура:</b></para>
/// <para>Все публичные методы неблокирующие — они только отправляют команды в очередь.
/// Обработка идёт строго последовательно в фоновом потоке.</para>
/// 
/// <para><b>Shared Backend:</b></para>
/// <para>NAudioBackend (WaveOutEvent) создаётся ОДИН РАЗ при создании AudioPlayer
/// и переиспользуется между треками через <see cref="IPlaybackBackend.Reinitialize"/>.
/// Это исключает дорогостоящие kernel-вызовы waveOutClose/waveOutOpen при каждой
/// смене трека, что критично когда приложение в фоне (ОС деприоритезирует потоки).</para>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>Все публичные методы потокобезопасны. Внутреннее состояние защищено
/// через actor model (single reader channel).</para>
/// </summary>
public sealed class AudioPlayer : IAsyncDisposable, IDisposable
{
    #region Fields

    private readonly AudioPlayerOptions _options;
    private readonly AudioPlayerEvents _events = new();
    private readonly Channel<IAudioCommand> _commandChannel;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _commandProcessorTask;

    /// <summary>
    /// Shared backend — создаётся один раз, переиспользуется между треками.
    /// Уничтожается только при dispose самого AudioPlayer.
    /// </summary>
    private readonly IPlaybackBackend _sharedBackend;

    // Atomic pipeline swap
    private volatile AudioPipeline? _activePipeline;

    // State machine
    private volatile PlayerState _state = PlayerState.Idle;
    private volatile float _volume = 1.0f;
    private volatile bool _disposed;

    // Session management - НЕ volatile, используем только через Interlocked
    private int _sessionId;

    // Position tracking
    private Timer? _positionTimer;
    private Timer? _bufferTimer;

    // Track info
    private string? _currentTrackId;

    #endregion

    #region Properties

    public AudioPlayerEvents Events => _events;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 4f);
            _activePipeline?.SetVolume(_volume);
        }
    }

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

    #region Events (Legacy compatibility)

    public event Action<TimeSpan>? PositionChanged
    {
        add => _events.PositionChanged += value;
        remove => _events.PositionChanged -= value;
    }

    public event Action<PlaybackState>? StateChanged
    {
        add => _events.StateChanged += value;
        remove => _events.StateChanged -= value;
    }

    public event Action? TrackEnded
    {
        add => _events.TrackEnded += value;
        remove => _events.TrackEnded -= value;
    }

    public event Action<Exception>? ErrorOccurred;

    #endregion

    #region Constructor

    public AudioPlayer(AudioPlayerOptions? options = null)
    {
        _options = options ?? new AudioPlayerOptions();

        _events.ErrorOccurred += err => ErrorOccurred?.Invoke(err.Exception ?? new Exception(err.Message));

        _commandChannel = Channel.CreateBounded<IAudioCommand>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // ═══ Shared backend: создаём ОДИН РАЗ при старте ═══
        // waveOutOpen() вызывается здесь, когда окно активно и потоки имеют полный приоритет.
        // При смене треков backend переиспользуется через Reinitialize() —
        // fast path (~0ms) если формат совпадает (Opus 48kHz → Opus 48kHz).
        _sharedBackend = CreateSharedBackend(_options);

        _commandProcessorTask = Task.Run(ProcessCommandsAsync);

        Log.Info("[AudioPlayer] Created with actor model + shared backend");
    }

    /// <summary>
    /// Создаёт shared backend. Вызывается из конструктора.
    /// </summary>
    private static IPlaybackBackend CreateSharedBackend(AudioPlayerOptions options)
    {
        if (options.UseNullBackend)
            return new Backends.NullAudioBackend();

        try
        {
            return new Backends.NAudioBackend();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] NAudio creation failed: {ex.Message}, using NullBackend");
            return new Backends.NullAudioBackend();
        }
    }

    #endregion

    #region Public API (Non-blocking)

    /// <summary>
    /// Запускает воспроизведение. Неблокирующий вызов.
    /// </summary>
    /// <param name="url">URL аудио потока.</param>
    /// <param name="trackId">ID трека.</param>
    /// <param name="bitrateHint">Битрейт (kbps).</param>
    /// <param name="seekPosition">
    /// Позиция для atomic seek-before-play. null = начать с начала.
    /// Используется при переключении качества для бесшовного перехода.
    /// </param>
    public void Play(string url, string? trackId = null, int bitrateHint = 0, TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int session = Interlocked.Increment(ref _sessionId);
        _currentTrackId = trackId;

        // Optimistic UI update
        SetState(PlayerState.Loading);

        _commandChannel.Writer.TryWrite(new PlayCommand(url, trackId, bitrateHint, session, seekPosition));
    }

    /// <summary>
    /// Async версия Play для обратной совместимости.
    /// Возвращает Task который завершается когда воспроизведение началось или ошибка.
    /// </summary>
    public Task PlayAsync(string url, string? trackId = null, int bitrateHint = 0,
        CancellationToken ct = default, TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(PlaybackState state)
        {
            if (state == PlaybackState.Playing)
            {
                _events.StateChanged -= OnStateChanged;
                _events.ErrorOccurred -= OnError;
                tcs.TrySetResult(true);
            }
        }

        void OnError(AudioPlayerError error)
        {
            _events.StateChanged -= OnStateChanged;
            _events.ErrorOccurred -= OnError;
            tcs.TrySetException(error.Exception ?? new Exception(error.Message));
        }

        _events.StateChanged += OnStateChanged;
        _events.ErrorOccurred += OnError;

        ct.Register(() =>
        {
            _events.StateChanged -= OnStateChanged;
            _events.ErrorOccurred -= OnError;
            tcs.TrySetCanceled(ct);
        });

        Play(url, trackId, bitrateHint, seekPosition);

        return tcs.Task;
    }

    /// <summary>
    /// Пауза. Неблокирующий вызов.
    /// </summary>
    public void Pause()
    {
        if (_state != PlayerState.Playing) return;

        // Optimistic UI update
        SetState(PlayerState.Paused);
        _activePipeline?.Stop();
    }

    /// <summary>
    /// Возобновление. Неблокирующий вызов.
    /// </summary>
    public void Resume()
    {
        if (_state != PlayerState.Paused) return;

        var pipeline = _activePipeline;
        if (pipeline == null) return;

        // ═══ Проверяем состояние буфера перед Resume ═══
        // После длительной паузы BufferedWaveProvider может быть в порядке,
        // но при быстром Pause/Resume (UI toggle) буфер может быть разреженным.
        int bufferedBytes = _sharedBackend.BufferedBytes;
        int minBytes = pipeline.SampleRate * pipeline.Channels * sizeof(float) * 100 / 1000; // 100ms

        if (bufferedBytes < minBytes)
        {
            // Буфер разреженный — активируем fill loop для дозаполнения
            pipeline.ActivateFillLoop();
            // Короткий warmup — не блокируем UI надолго
            pipeline.WaitForBackendWarmup(timeoutMs: 500);
        }

        // Optimistic UI update
        SetState(PlayerState.Playing);
        pipeline.Start();
    }

    /// <summary>
    /// Остановка. Неблокирующий вызов.
    /// </summary>
    public void Stop()
    {
        if (_state == PlayerState.Idle || _state == PlayerState.Disposed) return;

        int session = Interlocked.Increment(ref _sessionId);
        _commandChannel.Writer.TryWrite(new StopCommand(session));
    }

    /// <summary>
    /// Async версия Stop.
    /// Использует event-based ожидание вместо busy-wait.
    /// </summary>
    public async Task StopAsync()
    {
        if (_state == PlayerState.Idle || _state == PlayerState.Disposed) return;

        int session = Interlocked.Increment(ref _sessionId);

        // Event-based ожидание вместо spin-wait
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

        // Ждём перехода в Stopped с таймаутом
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            _events.StateChanged -= OnState;
            Log.Warn("[AudioPlayer] StopAsync timeout");
        }
    }

    /// <summary>
    /// Seek. Неблокирующий вызов, UI обновляется мгновенно.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed) return ValueTask.CompletedTask;
        if (_state is not (PlayerState.Playing or PlayerState.Paused)) return ValueTask.CompletedTask;

        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek) return ValueTask.CompletedTask;

        // Optimistic UI update — позиция обновляется мгновенно
        _events.RaisePositionChanged(position);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));

        int session = Interlocked.CompareExchange(ref _sessionId, 0, 0);
        _commandChannel.Writer.TryWrite(new SeekCommand(position, session, tcs));

        return new ValueTask(tcs.Task);
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

                // Проверяем актуальность сессии
                if (command.SessionId < currentSession && command is not DisposeCommand)
                {
                    Log.Debug($"[AudioPlayer] Skipping outdated command: " +
                              $"{command.GetType().Name} (session {command.SessionId} < {currentSession})");

                    if (command is SeekCommand { Completion: { } tcs })
                        tcs.TrySetCanceled();

                    continue;
                }

                // Проверяем допустимость команды для текущего состояния
                if (!PlayerStateTransitions.CanAcceptCommand(_state, command))
                {
                    Log.Debug($"[AudioPlayer] Ignoring {command.GetType().Name} in state {_state}");

                    if (command is SeekCommand { Completion: { } tcs })
                        tcs.TrySetResult(false);

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
                    Log.Error($"[AudioPlayer] Command error: {ex.Message}", ex);
                    HandleError(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Command processor fatal: {ex.Message}", ex);
        }
    }

    private async Task ProcessCommandAsync(IAudioCommand command)
    {
        switch (command)
        {
            case PlayCommand play:
                await HandlePlayAsync(play);
                break;

            case StopCommand:
                await HandleStopAsync();
                break;

            case SeekCommand seek:
                await HandleSeekAsync(seek);
                break;

            case DisposeCommand:
                await HandleDisposeAsync();
                break;
        }
    }

    private async Task HandlePlayAsync(PlayCommand cmd)
    {
        Log.Info($"[AudioPlayer] Play: {cmd.TrackId ?? "unknown"}, bitrate hint: {cmd.BitrateHint}" +
                 (cmd.SeekPosition.HasValue ? $", seek: {cmd.SeekPosition.Value.TotalSeconds:F1}s" : ""));

        SetState(PlayerState.Loading);

        // ═══ Dispose старого pipeline — backend НЕ уничтожается (shared) ═══
        var oldPipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (oldPipeline != null)
        {
            await oldPipeline.DisposeAsync();
        }

        StopTimers();

        try
        {
            // ═══ Создаём pipeline с SHARED backend ═══
            // После CreateAsync backend находится в остановленном состоянии:
            // Reinitialize() деактивировал fill loop, waveOut не играет.
            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Url,
                cmd.TrackId,
                cmd.BitrateHint,
                CreateUrlRefresher(),
                _options,
                _sharedBackend,
                _lifetimeCts.Token);

            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                Log.Debug($"[AudioPlayer] Play cancelled: session {cmd.SessionId} < {currentSession}");
                await pipeline.DisposeAsync();
                return;
            }

            pipeline.SetVolume(_volume);
            _events.RaiseStreamInfo(pipeline.StreamInfo);

            var replaced = Interlocked.Exchange(ref _activePipeline, pipeline);
            if (replaced != null)
            {
                await replaced.DisposeAsync();
            }

            // ═══ ATOMIC SEEK-BEFORE-PLAY ═══
            if (cmd.SeekPosition is { TotalMilliseconds: > 0 } seekPos)
            {
                long seekMs = (long)seekPos.TotalMilliseconds;
                pipeline.PrepareForSeek(seekMs);

                bool seekOk = await pipeline.Source.SeekAsync(seekMs, _lifetimeCts.Token);
                if (seekOk)
                {
                    long targetSamples = (long)(seekMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
                    pipeline.SetDecodedSamplesPosition(targetSamples);

                    Log.Debug($"[AudioPlayer] Pre-play seek to {seekMs}ms");
                }
            }

            // ═══ ЭТАП 1: Запускаем decoder ═══
            // Decoder начинает декодировать и наполнять PCM RingBuffer
            pipeline.StartDecoding(
                CreateUrlRefresher(),
                _options,
                OnTrackEnded,
                HandleError);

            SetState(PlayerState.Buffering);

            // ═══ ЭТАП 2: Ждём PCM RingBuffer ═══
            // Минимум данных в ring buffer чтобы fill loop имел что перекачивать
            int threshold = pipeline.SampleRate * pipeline.Channels * MinBufferMs / 1000;
            await pipeline.WaitForBufferAsync(threshold, 5000, _lifetimeCts.Token);

            // Проверка сессии после ожидания
            currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                Log.Debug($"[AudioPlayer] Play cancelled after buffering: " +
                          $"session {cmd.SessionId} < {currentSession}");
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null) await stale.DisposeAsync();
                return;
            }

            // ═══ ЭТАП 3: Активируем fill loop (warmup) ═══
            // Fill loop начинает перекачивать данные: PCM RingBuffer → BufferedWaveProvider
            // WaveOut ещё НЕ играет — данные только накапливаются
            pipeline.ActivateFillLoop();

            // ═══ ЭТАП 4: Ждём BufferedWaveProvider warmup ═══
            // WaitForWarmup блокирует до накопления ≥200ms данных в BufferedWaveProvider.
            // Это КРИТИЧНО для предотвращения артефакта "щётки":
            // без warmup waveOut.Play() начнёт читать из пустого буфера → glitches.
            // В фоновом режиме (EcoQoS троттлинг) warmup может занять дольше —
            // таймаут 3000ms достаточен даже при медленной сети.
            bool warmedUp = pipeline.WaitForBackendWarmup(timeoutMs: 3000);
            if (!warmedUp)
            {
                Log.Warn("[AudioPlayer] ⚠ Backend warmup timeout — possible initial artifact");
            }

            // Финальная проверка сессии после warmup
            currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                Log.Debug($"[AudioPlayer] Play cancelled after warmup: " +
                          $"session {cmd.SessionId} < {currentSession}");
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null) await stale.DisposeAsync();
                return;
            }

            // ═══ ЭТАП 5: Безопасный старт ═══
            // BufferedWaveProvider содержит ≥200ms данных → waveOut.Play() безопасен
            pipeline.Start();
            StartTimers();
            SetState(PlayerState.Playing);

            Log.Info($"[AudioPlayer] Playing: {pipeline.StreamInfo.FormatDisplay}");
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[AudioPlayer] Play cancelled");
            if (_state == PlayerState.Loading || _state == PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (Youtube.Exceptions.StreamUnavailableException ex)
        {
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
            throw;
        }
    }

    private async Task HandleStopAsync()
    {
        Log.Debug("[AudioPlayer] Stop");

        StopTimers();

        // Pipeline dispose — backend НЕ уничтожается (shared)
        var pipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (pipeline != null)
        {
            await pipeline.DisposeAsync();
        }

        // Останавливаем shared backend (Pause, не Dispose)
        // На случай если pipeline уже был null, но backend ещё играл
        _sharedBackend.Stop();
        _sharedBackend.Flush();

        _currentTrackId = null;

        SetState(PlayerState.Idle);
    }

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
            await pipeline.StopDecodingAsync(TimeSpan.FromMilliseconds(200));

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

            bool success = await pipeline.Source.SeekAsync(posMs, _lifetimeCts.Token);

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

            pipeline.StartDecoding(
                CreateUrlRefresher(),
                _options,
                OnTrackEnded,
                HandleError);

            // ═══ Warmup protocol для seek ═══
            // При seek буферы пусты после Flush(). Без warmup waveOut.Play()
            // начнёт читать пустой BufferedWaveProvider → артефакты.
            // Для seek используем короткий таймаут (500ms) чтобы не замедлять UX.
            if (wasPlaying)
            {
                pipeline.ActivateFillLoop();

                // Короткий warmup — seek должен быть быстрым
                // 500ms достаточно: decoder уже работает, PCM buffer наполняется
                pipeline.WaitForBackendWarmup(timeoutMs: 500);

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

            Log.Debug($"[AudioPlayer] Seeked to {posMs}ms");
        }
        catch (OperationCanceledException)
        {
            cmd.Completion?.TrySetCanceled();
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Seek error: {ex.Message}");
            cmd.Completion?.TrySetException(ex);

            SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);
            if (wasPlaying) pipeline.Start();
            StartPositionTimerDelayed();
        }
    }

    private async Task HandleDisposeAsync()
    {
        await HandleStopAsync();
        SetState(PlayerState.Disposed);
    }

    #endregion

    #region Timers

    private void StartTimers()
    {
        _positionTimer = new Timer(
            _ => _events.RaisePositionChanged(Position),
            null, 0, (int)_options.PositionUpdateInterval.TotalMilliseconds);

        _bufferTimer = new Timer(
            _ => RaiseBufferState(),
            null, 0, BufferStateUpdateIntervalMs);
    }

    private void StopTimers()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;

        _bufferTimer?.Dispose();
        _bufferTimer = null;
    }

    /// <summary>
    /// Запускает position timer с задержкой первого тика.
    /// Используется после seek — даёт время decoder'у наполнить буфер
    /// корректными данными перед первым position event.
    /// </summary>
    private void StartPositionTimerDelayed()
    {
        _positionTimer?.Dispose();
        _positionTimer = new Timer(
            _ => _events.RaisePositionChanged(Position),
            null,
            (int)_options.PositionUpdateInterval.TotalMilliseconds, // Первый тик с задержкой
            (int)_options.PositionUpdateInterval.TotalMilliseconds);
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    private void RaiseBufferState()
    {
        var pipeline = _activePipeline;
        if (pipeline == null) return;

        var source = pipeline.Source;
        var state = new BufferState(
            source.BufferProgress,
            source.IsFullyBuffered,
            source.GetBufferedRanges());

        _events.RaiseBufferState(state);
    }

    #endregion

    #region Helpers

    private void OnTrackEnded()
    {
        // Проверяем что плеер в адекватном состоянии для завершения трека.
        // Если идёт seek (Seeking) или загрузка нового трека (Loading/Buffering),
        // TrackEnded пришёл от устаревшего decoder loop — игнорируем.
        var currentState = _state;
        if (currentState is PlayerState.Idle or PlayerState.Disposed
            or PlayerState.Loading or PlayerState.Buffering or PlayerState.Seeking)
        {
            Log.Debug($"[AudioPlayer] Ignoring TrackEnded in state {currentState}");
            return;
        }

        _events.RaiseTrackEnded();
        SetState(PlayerState.Idle);
    }

    private void HandleError(Exception ex)
    {
        SetState(PlayerState.Error);
        _events.RaiseError(new AudioPlayerError(ex.Message, ex));
    }

    private void SetState(PlayerState newState)
    {
        var oldState = _state;
        if (oldState == newState) return;

        if (!PlayerStateTransitions.CanTransition(oldState, newState))
        {
            Log.Warn($"[AudioPlayer] Invalid transition: {oldState} -> {newState}");
            return;
        }

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
        PlayerState.Seeking => PlaybackState.Playing, // Визуально как Playing
        PlayerState.Error => PlaybackState.Error,
        PlayerState.Disposed => PlaybackState.Stopped,
        _ => PlaybackState.Stopped
    };

    private Func<CancellationToken, Task<string?>>? CreateUrlRefresher()
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId))
            return null;

        var trackId = _currentTrackId;
        var callback = _options.UrlRefreshCallback;
        return ct => callback(trackId, ct).AsTask();
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Возвращает текущий pipeline для прямого доступа к source.
    /// Используется AudioEngine для Suspend/Resume.
    /// </summary>
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
                ? pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / 8
                : 0,
            _ => (long)(pipeline.Source.BufferProgress / 100.0
                * pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / 8)
        };
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        return _activePipeline?.Source.GetBufferedRanges() ?? [];
    }

    public string CurrentCodec => _activePipeline?.StreamInfo.Codec ?? "";

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
            _commandProcessorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }

        // Shared backend уничтожается ПОСЛЕДНИМ — после всех pipeline
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
            await _commandProcessorTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { }

        // Shared backend уничтожается ПОСЛЕДНИМ
        _sharedBackend.Dispose();

        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}