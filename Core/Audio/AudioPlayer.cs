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

        _commandProcessorTask = Task.Run(ProcessCommandsAsync);

        Log.Info("[AudioPlayer] Created with actor model");
    }

    #endregion

    #region Public API (Non-blocking)

    /// <summary>
    /// Запускает воспроизведение. Неблокирующий вызов.
    /// </summary>
    public void Play(string url, string? trackId = null, int bitrateHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int session = Interlocked.Increment(ref _sessionId);
        _currentTrackId = trackId;

        // Optimistic UI update
        SetState(PlayerState.Loading);

        _commandChannel.Writer.TryWrite(new PlayCommand(url, trackId, bitrateHint, session));
    }

    /// <summary>
    /// Async версия Play для обратной совместимости.
    /// Возвращает Task который завершается когда воспроизведение началось или ошибка.
    /// </summary>
    public Task PlayAsync(string url, string? trackId = null, int bitrateHint = 0, CancellationToken ct = default)
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

        Play(url, trackId, bitrateHint);

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

        // Optimistic UI update
        SetState(PlayerState.Playing);
        _activePipeline?.Start();
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
    /// </summary>
    public async Task StopAsync()
    {
        if (_state == PlayerState.Idle || _state == PlayerState.Disposed) return;

        int session = Interlocked.Increment(ref _sessionId);
        await _commandChannel.Writer.WriteAsync(new StopCommand(session));

        // Ждём перехода в Idle
        int waitCount = 0;
        while (_state != PlayerState.Idle && _state != PlayerState.Disposed && waitCount < 100)
        {
            await Task.Delay(10);
            waitCount++;
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
        Log.Info($"[AudioPlayer] Play: {cmd.TrackId ?? "unknown"}, bitrate hint: {cmd.BitrateHint}");

        SetState(PlayerState.Loading);

        var oldPipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (oldPipeline != null)
        {
            await oldPipeline.DisposeAsync();
        }

        StopTimers();

        try
        {
            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Url,
                cmd.TrackId,
                cmd.BitrateHint,
                CreateUrlRefresher(),
                _options,
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

            pipeline.StartDecoding(
                CreateUrlRefresher(),
                _options,
                OnTrackEnded,
                HandleError);

            SetState(PlayerState.Buffering);

            int threshold = pipeline.SampleRate * pipeline.Channels * MinBufferMs / 1000;
            await pipeline.WaitForBufferAsync(threshold, 5000, _lifetimeCts.Token);

            currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                Log.Debug($"[AudioPlayer] Play cancelled after buffering: " +
                          $"session {cmd.SessionId} < {currentSession}");
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null) await stale.DisposeAsync();
                return;
            }

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
            // Специальная обработка StreamUnavailableException — пробрасываем наверх
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
            throw; // Пробрасываем для обработки в AudioEngine
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

        var pipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (pipeline != null)
        {
            await pipeline.DisposeAsync();
        }

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

        // ═══ FIX: Останавливаем position timer во время seek ═══
        // Это предотвращает попадание stale position events в Rx pipeline.
        // Buffer timer НЕ останавливаем — cache line должна обновляться.
        StopPositionTimer();

        try
        {
            // Decoder stop — в логах стабильно <10ms, 200ms safety timeout
            await pipeline.StopDecodingAsync(TimeSpan.FromMilliseconds(200));

            // Проверяем сессию — мог прийти новый Play/Stop пока ждали
            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                cmd.Completion?.TrySetCanceled();
                StartPositionTimerDelayed();
                return;
            }

            // Останавливаем backend и очищаем все буферы (быстро, <1ms)
            pipeline.Stop();
            pipeline.Flush();

            // Подготавливаем decoder: skip frames + timestamp-based skip
            long posMs = (long)cmd.Position.TotalMilliseconds;
            pipeline.PrepareForSeek(posMs);

            // Выполняем seek в source — теперь non-blocking (fire-and-forget preload)
            bool success = await pipeline.Source.SeekAsync(posMs, _lifetimeCts.Token);

            if (!success)
            {
                cmd.Completion?.TrySetResult(false);
                SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);
                if (wasPlaying) pipeline.Start();
                StartPositionTimerDelayed();
                return;
            }

            // Обновляем позицию для корректного Position reporting
            long targetSamples = (long)(posMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
            pipeline.SetDecodedSamplesPosition(targetSamples);

            // Перезапускаем decoder loop — он начнёт читать из новой позиции
            pipeline.StartDecoding(
                CreateUrlRefresher(),
                _options,
                OnTrackEnded,
                HandleError);

            // ═══ FIX: Убран WaitForBufferAsync ═══
            // Backend AudioCallback уже обрабатывает underrun: заполняет тишиной.
            // Decoder заполнит буфер за 1-2 фрейма (~40ms).
            // Для rapid seeking это критично: каждый WaitForBuffer добавлял 100-300ms.

            // Возобновляем воспроизведение
            if (wasPlaying)
            {
                pipeline.Start();
                SetState(PlayerState.Playing);
            }
            else
            {
                SetState(PlayerState.Paused);
            }

            // ═══ FIX: Перезапускаем timer С ЗАДЕРЖКОЙ перед событием ═══
            // Первый тик через interval мс — к этому моменту Position будет корректной
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

            // Восстанавливаем состояние
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
                ? (long)(pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / 8)
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

        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}