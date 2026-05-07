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
}

/// <summary>
/// Аудио плеер с акторной моделью обработки команд.
/// 
/// <para><b>Архитектура:</b> Все публичные методы неблокирующие (отправляют команды в Channel). Обработка строго последовательна в фоне.</para>
/// <para><b>Shared Backend:</b> Backend драйвера ОС переиспользуется без полного пересоздания, что ускоряет переключение и предотвращает проблемы с EcoQoS.</para>
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
    private const int SeekStopDecoderTimeoutMs = 200;

    /// <summary>Максимальное время блокировки (мс) при warmup во время Seek.</summary>
    private const int SeekWarmupTimeoutMs = 500;

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

    // State machine
    private volatile PlayerState _state = PlayerState.Idle;
    private volatile float _volume = 1.0f;
    private volatile bool _disposed;

    /// <summary>Уникальный ID сессии для отмены устаревших команд. Изменяется через Interlocked.</summary>
    private int _sessionId;

    // Position tracking
    private Timer? _positionTimer;
    private Timer? _bufferTimer;

    private string? _currentTrackId;

    #endregion

    #region Properties

    public AudioPlayerEvents Events => _events;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, MaxVolumeGain);
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

    private void Play(string url, string? trackId = null, int bitrateHint = 0, TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int session = Interlocked.Increment(ref _sessionId);
        _currentTrackId = trackId;
        SetState(PlayerState.Loading);
        _commandChannel.Writer.TryWrite(new PlayCommand(url, trackId, bitrateHint, session, seekPosition));
    }

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

    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed || _state is not (PlayerState.Playing or PlayerState.Paused))
            return ValueTask.CompletedTask;

        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
            return ValueTask.CompletedTask;

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

                try { await ProcessCommandAsync(command); }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
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

    private async Task HandlePlayAsync(PlayCommand cmd)
    {
        Log.Info($"[AudioPlayer] Play: {cmd.TrackId ?? "unknown"}, bitrate hint: {cmd.BitrateHint}");
        SetState(PlayerState.Loading);

        var oldPipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (oldPipeline != null) await oldPipeline.DisposeAsync();

        StopTimers();

        try
        {
            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Url, cmd.TrackId, cmd.BitrateHint, CreateUrlRefresher(),
                _options, _sharedBackend, _lifetimeCts.Token);

            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                await pipeline.DisposeAsync();
                return;
            }

            pipeline.SetVolume(_volume);
            _events.RaiseStreamInfo(pipeline.StreamInfo);

            var replaced = Interlocked.Exchange(ref _activePipeline, pipeline);
            if (replaced != null) await replaced.DisposeAsync();

            if (cmd.SeekPosition is { TotalMilliseconds: > 0 } seekPos)
            {
                long seekMs = (long)seekPos.TotalMilliseconds;
                pipeline.PrepareForSeek(seekMs);
                if (await pipeline.Source.SeekAsync(seekMs, _lifetimeCts.Token))
                {
                    long targetSamples = (long)(seekMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
                    pipeline.SetDecodedSamplesPosition(targetSamples);
                }
            }

            pipeline.StartDecoding(CreateUrlRefresher(), _options, OnTrackEnded, HandleError);
            SetState(PlayerState.Buffering);

            int threshold = pipeline.SampleRate * pipeline.Channels * MinBufferMs / 1000;
            await pipeline.WaitForBufferAsync(threshold, PlayBufferWaitTimeoutMs, _lifetimeCts.Token);

            currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (cmd.SessionId < currentSession)
            {
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null) await stale.DisposeAsync();
                return;
            }

            pipeline.ActivateFillLoop();
            pipeline.Start();

            StartTimers();
            SetState(PlayerState.Playing);

            // NotifyDeviceLost() отменяет pipeline.LifetimeToken.
            // Без этого watch task потеря устройства молчит до следующего действия пользователя.
            WatchPipelineLifetimeAsync(pipeline, cmd.SessionId);
        }
        catch (OperationCanceledException)
        {
            if (_state == PlayerState.Loading || _state == PlayerState.Buffering)
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
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
            throw;
        }
    }

    /// <summary>
    /// Фоновая задача ожидания отмены lifetime token pipeline.
    /// Если pipeline отменён из-за потери устройства (не из-за штатной остановки) —
    /// вызывает HandleError и отправляет StopCommand.
    /// </summary>
    private async void WatchPipelineLifetimeAsync(AudioPipeline pipeline, int sessionId)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, pipeline.LifetimeToken);
        }
        catch (OperationCanceledException)
        {
            // Проверяем: это потеря устройства или штатная остановка?
            // Штатная остановка: _activePipeline уже null или другой pipeline.
            // Потеря устройства: _activePipeline всё ещё наш pipeline.
            int currentSession = Interlocked.CompareExchange(ref _sessionId, 0, 0);
            if (_disposed || sessionId < currentSession) return;
            if (_activePipeline != pipeline) return;

            Log.Error("[AudioPlayer] Pipeline lifetime ended unexpectedly (device lost)");
            HandleError(new Exceptions.AudioDeviceException(GetDeviceErrorMessage()));

            int session = Interlocked.Increment(ref _sessionId);
            await _commandChannel.Writer.WriteAsync(new StopCommand(session));
        }
    }

    private async Task HandleStopAsync()
    {
        Log.Debug("[AudioPlayer] Stop");
        StopTimers();

        var pipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (pipeline != null) await pipeline.DisposeAsync();

        // Never-stop: не вызываем _sharedBackend.Stop() — waveOut продолжает работать.
        // Flush очищает буфер, provider отдаёт тишину сам.
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
            await pipeline.StopDecodingAsync(TimeSpan.FromMilliseconds(SeekStopDecoderTimeoutMs));

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
            pipeline.StartDecoding(CreateUrlRefresher(), _options, OnTrackEnded, HandleError);

            if (wasPlaying)
            {
                int seekThreshold = pipeline.SampleRate * pipeline.Channels * MinSeekResumeBufferMs / 1000;
                await pipeline.WaitForBufferAsync(seekThreshold, SeekWarmupTimeoutMs, _lifetimeCts.Token);

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
        catch (Exceptions.AudioDeviceException ex)
        {
            // Устройство потеряно во время seek — сообщаем пользователю
            Log.Error($"[AudioPlayer] Audio device lost during seek: {ex.Message}");
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Seek error: {ex.Message}");
            cmd.Completion?.TrySetException(ex);
            SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);
            if (wasPlaying)
            {
                try { pipeline.Start(); }
                catch (Exceptions.AudioDeviceException devEx) { HandleError(devEx); return; }
            }
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

    private void StartPositionTimerDelayed()
    {
        _positionTimer?.Dispose();
        _positionTimer = new Timer(
            _ => _events.RaisePositionChanged(Position),
            null,
            (int)_options.PositionUpdateInterval.TotalMilliseconds,
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
        var state = new BufferState(source.BufferProgress, source.IsFullyBuffered, source.GetBufferedRanges());
        _events.RaiseBufferState(state);
    }

    #endregion

    #region Helpers

    private void OnTrackEnded()
    {
        var currentState = _state;
        if (currentState is PlayerState.Idle or PlayerState.Disposed
            or PlayerState.Loading or PlayerState.Buffering or PlayerState.Seeking)
        {
            return;
        }

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
        var callback = _options.UrlRefreshCallback;
        return ct => callback(trackId, ct).AsTask();
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

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => _activePipeline?.Source.GetBufferedRanges() ??[];

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
            await _commandProcessorTask.WaitAsync(TimeSpan.FromSeconds(DisposeTaskTimeoutSec));
        }
        catch { }

        // Shared backend уничтожается ПОСЛЕДНИМ
        _sharedBackend.Dispose();

        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}