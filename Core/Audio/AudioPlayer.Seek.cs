using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

public sealed partial class AudioPlayer
{
    #region Seek Constants

    /// <summary>Таймаут прогрева буфера после seek для streaming контента (мс).</summary>
    private const int SeekWarmupTimeoutMs = 150;

    /// <summary>Таймаут прогрева буфера после seek для cached/local контента (мс).</summary>
    private const int SeekWarmupTimeoutCachedMs = 50;

    /// <summary>Максимальное количество итераций seek coalescing loop.</summary>
    private const int SeekLoopMaxIterations = 50;

    /// <summary>Таймаут остановки декодера при re-seek внутри loop (мс).</summary>
    private const int ReSeekDecoderStopTimeoutMs = 200;

    /// <summary>
    /// Порог BufferProgress для определения «быстрого» источника (%).
    /// </summary>
    private const double FastSourceBufferThreshold = 80.0;

    /// <summary>
    /// Максимальный таймаут Phase A (source seek + critical range download).
    /// Ограничивает блокировку actor loop при scrubbing по незакачанному контенту.
    /// </summary>
    private const int SourceSeekTimeoutMs = 500;

    /// <summary>
    /// Максимальное время фонового ожидания decoder/ring buffer
    /// после deferred seek в состоянии Buffering.
    /// </summary>
    private const int DeferredSeekResumeTimeoutMs = 30_000;

    #endregion

    #region Seek State

    /// <summary>CTS текущей фоновой фазы seek.</summary>
    private CancellationTokenSource? _activeSeekCts;

    /// <summary>CTS текущего deferred-resume waiter после seek-buffering.</summary>
    private CancellationTokenSource? _deferredResumeCts;

    /// <summary>Pending seek позиция для latest-wins coalescing. -1 = нет pending seek.</summary>
    private long _pendingSeekMs = -1;

    /// <summary>Флаг активного seek (HandleSeekAsync выполняется).</summary>
    private volatile bool _backgroundSeekActive;

    /// <summary>Pipeline, к которому относится текущий seek.</summary>
    private AudioPipeline? _backgroundSeekPipeline;

#if DEBUG
    private int _seekRestartCount;
    private int _decoderRestartCount;
#endif

    #endregion

    #region Public Seek API

    /// <summary>
    /// Инициирует seek с latest-wins coalescing.
    /// Мгновенно возвращает управление UI-потоку.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed || _state is not (PlayerState.Playing or PlayerState.Paused or PlayerState.Buffering))
            return ValueTask.CompletedTask;

        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
            return ValueTask.CompletedTask;

        pipeline.Source.SetPlaybackActive(false);

        long targetMs = (long)position.TotalMilliseconds;

        if (_backgroundSeekActive || Interlocked.Read(ref _pendingSeekMs) >= 0)
        {
            Interlocked.Exchange(ref _pendingSeekMs, targetMs);
            return ValueTask.CompletedTask;
        }

        CancelActiveSeek();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));

        int seekGeneration = Interlocked.Increment(ref _seekGeneration);

        Interlocked.Exchange(ref _pendingSeekMs, targetMs);
        _commandChannel.Writer.TryWrite(new SeekCommand(position, _session.Current, seekGeneration, tcs));

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Отменяет текущую фоновую фазу seek АСИНХРОННО.
    /// </summary>
    private void CancelActiveSeek()
    {
        ResetSeekState();

        var deferredResumeCts = Interlocked.Exchange(ref _deferredResumeCts, null);
        CancelCtsAsync(deferredResumeCts);

        var cts = Interlocked.Exchange(ref _activeSeekCts, null);
        if (cts == null) return;

#if DEBUG
        int restartCount = Interlocked.Increment(ref _seekRestartCount);
        if (restartCount % 5 == 0)
            Log.Warn($"[AudioPlayer] Seek restart storm: {restartCount} cancellations on current track");
#endif

        CancelCtsAsync(cts);
    }

    private static void CancelCtsAsync(CancellationTokenSource? cts)
    {
        if (cts == null) return;

        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            try { ((CancellationTokenSource)state!).Cancel(); }
            catch (ObjectDisposedException) { }
        }, cts);
    }

    /// <summary>Сбрасывает observability-счётчики при смене трека.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal void ResetPerTrackCounters()
    {
#if DEBUG
        Interlocked.Exchange(ref _seekRestartCount, 0);
        Interlocked.Exchange(ref _decoderRestartCount, 0);
#endif
    }

    #endregion

    #region Seek Command Handler

    /// <summary>
    /// Обрабатывает seek: синхронная actor-фаза + background coalescing.
    /// </summary>
    private async Task HandleSeekAsync(SeekCommand cmd)
    {
        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
        {
            cmd.Completion?.TrySetResult(false);
            return;
        }

        long latestTargetMs = DrainPendingSeekMs();
        long posMs = latestTargetMs >= 0 ? latestTargetMs : (long)cmd.Position.TotalMilliseconds;

        bool wasPlaying = _state is PlayerState.Playing or PlayerState.Buffering;
        SetState(PlayerState.Seeking);
        StopPositionTimer();
        _lastRawPlayedSamples = -1;

        try
        {
            if (pipeline.Source is CachingStreamSource cachingSource)
                _ = cachingSource.TryPrefetchChunkForSeekAsync(posMs, _lifetimeCts.Token);

            await pipeline.StopDecodingAsync(
                TimeSpan.FromMilliseconds(DecoderStopTimeoutSeekMs)).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId))
            {
                cmd.Completion?.TrySetCanceled();
                StartPositionTimerDelayed();
                return;
            }

            pipeline.Stop();
            pipeline.Flush();
            pipeline.PrepareForSeek(posMs);

            Volatile.Write(ref _backgroundSeekPipeline, pipeline);
            _backgroundSeekActive = true;

            var seekCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            var oldCts = Interlocked.Exchange(ref _activeSeekCts, seekCts);
            if (oldCts != null)
            {
                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            await CompleteSeekWithCoalescingAsync(
                pipeline, cmd, posMs, wasPlaying, cmd.SessionId, cmd.SeekGeneration, seekCts).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cmd.Completion?.TrySetCanceled();
            StartPositionTimerDelayed();
        }
        catch (AudioDeviceException ex)
        {
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
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

    #endregion

    #region Seek Coalescing Loop

    /// <summary>
    /// Background-фаза seek с full-lifecycle coalescing и автоматическим
    /// переходом в Buffering при нехватке данных.
    /// </summary>
    private async Task CompleteSeekWithCoalescingAsync(
     AudioPipeline pipeline, SeekCommand cmd, long initialPosMs,
     bool wasPlaying, int sessionAtStart, int seekGeneration, CancellationTokenSource seekCts)
    {
        var seekCt = seekCts.Token;
        bool decoderStarted = false;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            long currentTargetMs = initialPosMs;
            int iteration = 0;

            while (iteration++ < SeekLoopMaxIterations)
            {
                var phaseSw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    var sourceSeekTask = pipeline.Source
                        .SeekAsync(currentTargetMs, seekCt).AsTask();

                    var completed = await Task.WhenAny(
                        sourceSeekTask,
                        Task.Delay(SourceSeekTimeoutMs, seekCt)).ConfigureAwait(false);

                    if (completed == sourceSeekTask)
                    {
                        await sourceSeekTask.ConfigureAwait(false);
                    }
                    else
                    {
                        long drained = DrainPendingSeekMs();
                        if (drained >= 0)
                        {
                            currentTargetMs = drained;
                            pipeline.Flush();
                            pipeline.PrepareForSeek(currentTargetMs);
                            Log.Debug($"[SeekTelemetry] Phase A timed out, coalescing to {drained}ms");
                            continue;
                        }

                        await sourceSeekTask.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Debug($"[SeekTelemetry] Phase A cancelled after {phaseSw.ElapsedMilliseconds}ms");
                    throw;
                }

                long phaseAMs = phaseSw.ElapsedMilliseconds;
                phaseSw.Restart();

                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    return;
                }

                UpdateDecodedPosition(pipeline, currentTargetMs);

                {
                    long drained = DrainPendingSeekMs();
                    if (drained >= 0)
                    {
                        currentTargetMs = drained;
                        pipeline.Flush();
                        pipeline.PrepareForSeek(currentTargetMs);
                        Log.Debug($"[SeekTelemetry] Phase B: Coalesced to {drained}ms");
                        continue;
                    }
                }

                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    return;
                }

                pipeline.StartDecoding(
                    CreateUrlRefresher(), _options,
                    CreateTrackEndedCallback(cmd.SessionId),
                    CreateErrorCallback(cmd.SessionId, pipeline));
                decoderStarted = true;

                long phaseDMs = phaseSw.ElapsedMilliseconds;
                phaseSw.Restart();

                {
                    long drained = DrainPendingSeekMs();
                    if (drained >= 0)
                    {
                        currentTargetMs = drained;
                        await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                        decoderStarted = false;
                        pipeline.PrepareForSeek(currentTargetMs);
                        Log.Debug($"[SeekTelemetry] Phase D interrupted, re-seeking to {drained}ms");
                        continue;
                    }
                }

                long phaseEMs = 0;

                if (wasPlaying && _state != PlayerState.Paused)
                {
                    var (seekThreshold, warmupTimeout) =
                        ComputeSeekWarmupParams(pipeline, currentTargetMs);

                    bool warmupReady = seekThreshold <= 0;

                    if (seekThreshold > 0 && warmupTimeout > 0)
                    {
                        try
                        {
                            warmupReady = await pipeline.WaitForBufferAsync(
                                seekThreshold, warmupTimeout, seekCt).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                        }
                    }

                    phaseEMs = phaseSw.ElapsedMilliseconds;
                    phaseSw.Restart();

                    {
                        long drained = DrainPendingSeekMs();
                        if (drained >= 0)
                        {
                            currentTargetMs = drained;
                            await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                            decoderStarted = false;
                            pipeline.PrepareForSeek(currentTargetMs);
                            Log.Debug($"[SeekTelemetry] Phase E interrupted, re-seeking to {drained}ms");
                            continue;
                        }
                    }

                    if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                    {
                        cmd.Completion?.TrySetCanceled();
                        return;
                    }

                    bool bufferReady = seekThreshold <= 0
                        || warmupReady
                        || pipeline.BufferedSamples >= seekThreshold;

                    if (bufferReady || pipeline.Source.IsFullyBuffered)
                    {
                        ResumePlaybackSequence(
                            pipeline, startTimers: false,
                            configurePipeline: true, trackId: _currentTrackId);
                    }
                    else
                    {
                        Log.Warn($"[SeekTelemetry] Buffer warmup timed out " +
                                 $"(ring={pipeline.BufferedSamples}, threshold={seekThreshold}). " +
                                 "Entering Buffering state. Playback will auto-resume.");

                        _options.OnPipelineConfiguring?.Invoke(pipeline, _currentTrackId);
                        pipeline.ActivateBufferingMode();
                        SetState(PlayerState.Buffering);

                        var deferredResumeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                        var previousDeferredResumeCts = Interlocked.Exchange(ref _deferredResumeCts, deferredResumeCts);
                        CancelCtsAsync(previousDeferredResumeCts);

                        _ = AwaitDeferredSeekBufferAndResumeAsync(
                            pipeline, seekThreshold, sessionAtStart, seekGeneration, deferredResumeCts);
                    }
                }
                else
                {
                    SetState(PlayerState.Paused);
                }

                Log.Info($"[SeekTelemetry] Seek to {currentTargetMs}ms COMPLETED. " +
                         $"Total: {totalSw.ElapsedMilliseconds}ms | " +
                         $"A: {phaseAMs}ms | D: {phaseDMs}ms | E: {phaseEMs}ms");

                StartPositionTimerDelayed();
                _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
                cmd.Completion?.TrySetResult(true);
                return;
            }

            Log.Warn($"[SeekTelemetry] Seek coalescing loop exhausted ({SeekLoopMaxIterations} iterations)");

            if (decoderStarted && wasPlaying && _state != PlayerState.Paused)
            {
                ResumePlaybackSequence(
                    pipeline, startTimers: false,
                    configurePipeline: true, trackId: _currentTrackId);
            }

            StartPositionTimerDelayed();
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(initialPosMs));
            cmd.Completion?.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"[SeekTelemetry] Seek to {initialPosMs}ms cancelled. " +
                       $"Elapsed: {totalSw.ElapsedMilliseconds}ms");
            cmd.Completion?.TrySetCanceled();
            StartPositionTimerDelayed();
        }
        catch (AudioDeviceException ex)
        {
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
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
            if (ReferenceEquals(Volatile.Read(ref _backgroundSeekPipeline), pipeline))
                ResetSeekState();

            Interlocked.CompareExchange(ref _activeSeekCts, null, seekCts);

            try { seekCts.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Фоново дожидается готовности decoder/ring buffer после deferred seek
    /// и возвращает completion в actor loop через <see cref="DeferredResumeCommand"/>.
    /// </summary>
    private async Task AwaitDeferredSeekBufferAndResumeAsync(
        AudioPipeline pipeline,
        int seekThreshold,
        int sessionId,
        int seekGeneration,
        CancellationTokenSource deferredResumeCts)
    {
        try
        {
            bool thresholdReached = await pipeline.WaitForBufferAsync(
                seekThreshold, DeferredSeekResumeTimeoutMs, deferredResumeCts.Token)
                .ConfigureAwait(false);

            int ringBufferSamples = pipeline.BufferedSamples;

            if (_disposed || deferredResumeCts.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
                return;

            if (!_commandChannel.Writer.TryWrite(new DeferredResumeCommand(
                    sessionId,
                    seekGeneration,
                    pipeline,
                    thresholdReached,
                    ringBufferSamples)))
            {
                Log.Debug("[SeekTelemetry] Deferred resume command dropped: channel unavailable");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[SeekTelemetry] Deferred seek waiter error: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _deferredResumeCts, null, deferredResumeCts);
            try { deferredResumeCts.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Возобновляет воспроизведение после deferred seek внутри actor loop.
    /// </summary>
    private Task HandleDeferredResumeAsync(DeferredResumeCommand cmd)
    {
        int currentSeekGeneration = Volatile.Read(ref _seekGeneration);

        if (_disposed
            || _activePipeline != cmd.Pipeline
            || _session.IsStale(cmd.SessionId)
            || cmd.SeekGeneration != currentSeekGeneration)
        {
            Log.Debug($"[SeekTelemetry] Deferred resume dropped: " +
                      $"disposed={_disposed}, pipelineMatch={_activePipeline == cmd.Pipeline}, " +
                      $"stale={_session.IsStale(cmd.SessionId)}, " +
                      $"generationMatch={cmd.SeekGeneration == currentSeekGeneration}, " +
                      $"state={_state}");
            return Task.CompletedTask;
        }

        if (CurrentPlaybackIntent != PlaybackIntent.Play)
        {
            Log.Debug($"[SeekTelemetry] Deferred resume ignored due to intent={CurrentPlaybackIntent}");
            return Task.CompletedTask;
        }

        if (_state == PlayerState.Playing)
            return Task.CompletedTask;

        if (_state is PlayerState.Idle or PlayerState.Disposed or PlayerState.Error)
        {
            Log.Debug($"[SeekTelemetry] Deferred resume ignored in terminal state={_state}");
            return Task.CompletedTask;
        }

        if (cmd.Pipeline.IsDeviceLost)
        {
            SetState(PlayerState.Buffering);
            _commandChannel.Writer.TryWrite(new DeviceRecoveryCommand(cmd.SessionId));
            return Task.CompletedTask;
        }

        if (!cmd.ThresholdReached)
        {
            Log.Warn($"[SeekTelemetry] Deferred warmup timed out after {DeferredSeekResumeTimeoutMs}ms " +
                     $"(ring={cmd.BufferedSamples}). Force-resuming playback.");
        }
        else
        {
            Log.Debug($"[SeekTelemetry] Deferred warmup complete " +
                      $"(ring={cmd.BufferedSamples}). Resuming playback.");
        }

        ResumePlaybackSequence(
            cmd.Pipeline,
            startTimers: true,
            configurePipeline: false,
            trackId: _currentTrackId);

        Log.Info("[SeekTelemetry] Deferred seek buffer ready. Playback resumed automatically.");
        return Task.CompletedTask;
    }

    #endregion

    #region Deferred Seek Resume

    /// <summary>
    /// Фоново дожидается готовности decoder/ring buffer после deferred seek
    /// и автоматически возобновляет воспроизведение, не блокируя actor loop.
    /// </summary>
    /// <param name="pipeline">Pipeline, для которого выполняется ожидание.</param>
    /// <param name="seekThreshold">Минимальный порог ring buffer для безопасного resume.</param>
    /// <param name="sessionId">Сессия playback, к которой относится seek.</param>
    private async Task AwaitDeferredSeekBufferAndResumeAsync(
        AudioPipeline pipeline, int seekThreshold, int sessionId)
    {
        try
        {
            bool thresholdReached = await pipeline.WaitForBufferAsync(
                seekThreshold, DeferredSeekResumeTimeoutMs, _lifetimeCts.Token)
                .ConfigureAwait(false);

            int ringBufferSamples = pipeline.BufferedSamples;

            // Guard: убеждаемся что pipeline/session/state всё ещё актуальны
            if (_disposed
                || _activePipeline != pipeline
                || _session.IsStale(sessionId)
                || _state != PlayerState.Buffering)
            {
                Log.Debug($"[SeekTelemetry] Deferred resume abandoned: " +
                          $"disposed={_disposed}, pipelineMatch={_activePipeline == pipeline}, " +
                          $"stale={_session.IsStale(sessionId)}, state={_state}");
                return;
            }

            if (thresholdReached)
            {
                Log.Debug($"[SeekTelemetry] Deferred warmup: ring buffer reached threshold " +
                          $"({ringBufferSamples} samples). Resuming playback.");
            }
            else
            {
                // Таймаут истёк. Запускаем воспроизведение принудительно:
                // decoder продолжит заполнять ring buffer на лету.
                // При пустом ring buffer будут кратковременные underruns,
                // но starvation callback обработает эскалацию.
                Log.Warn($"[SeekTelemetry] Deferred warmup timed out after {DeferredSeekResumeTimeoutMs}ms " +
                         $"(ring={ringBufferSamples}, threshold={seekThreshold}). " +
                         "Force-resuming playback.");
            }

            pipeline.Start();
            SetState(PlayerState.Playing);
            StartTimers();

            Log.Info("[SeekTelemetry] Deferred seek buffer ready. Playback resumed automatically.");
        }
        catch (OperationCanceledException)
        {
            // Нормально: пользователь сделал Stop, новый Seek или Dispose
        }
        catch (AudioDeviceException ex)
        {
            HandleError(ex);
        }
        catch (Exception ex)
        {
            Log.Warn($"[SeekTelemetry] Deferred seek resume error: {ex.Message}");
        }
    }

    #endregion

    #region Seek Helpers

    /// <summary>
    /// Вычисляет параметры WaitForBuffer на основе реального состояния источника.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>Fully cached / LocalFile:</b> threshold=0, timeout=0 — пропуск ожидания</item>
    ///   <item><b>Target range cached (≥80% overall):</b> минимальный threshold, короткий timeout</item>
    ///   <item><b>Streaming (не cached):</b> полный threshold и стандартный timeout</item>
    /// </list>
    /// </remarks>
    private static (int seekThreshold, int warmupTimeout) ComputeSeekWarmupParams(
        AudioPipeline pipeline, long targetMs)
    {
        var source = pipeline.Source;
        int rate = pipeline.SampleRate;
        int channels = pipeline.Channels;

        // Полностью кэшированные / локальные файлы: мгновенный resume
        if (source.IsFullyBuffered || source is Sources.LocalFileSource)
            return (0, 0);

        // Target range уже доступен или общий прогресс высокий
        if (source is CachingStreamSource caching)
        {
            bool targetReady = caching.IsTargetChunkAvailable(targetMs);

            if (targetReady || source.BufferProgress >= FastSourceBufferThreshold)
            {
                int minThreshold = rate * channels * 10 / 1000;
                return (Math.Max(minThreshold, 1), SeekWarmupTimeoutCachedMs);
            }
        }
        else if (source.BufferProgress >= FastSourceBufferThreshold)
        {
            int minThreshold = rate * channels * 10 / 1000;
            return (Math.Max(minThreshold, 1), SeekWarmupTimeoutCachedMs);
        }

        // Streaming: нужно подождать
        int fullThreshold = rate * channels * MinSeekResumeBufferMs / 1000;
        return (fullThreshold, SeekWarmupTimeoutMs);
    }

    /// <summary>Атомарно забирает pending seek позицию (latest-wins).</summary>
    /// <returns>Позиция в миллисекундах, или <c>-1</c> если pending seek отсутствует.</returns>
    private long DrainPendingSeekMs()
        => Interlocked.Exchange(ref _pendingSeekMs, -1);

    /// <summary>
    /// Останавливает decoder и очищает буферы для re-seek внутри coalescing loop.
    /// </summary>
    private static async Task StopDecoderForReseekAsync(AudioPipeline pipeline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await pipeline.StopDecodingAsync(
            TimeSpan.FromMilliseconds(ReSeekDecoderStopTimeoutMs)).ConfigureAwait(false);

        pipeline.Stop();
        pipeline.Flush();
    }

    /// <summary>Проверяет валидность состояния seek.</summary>
    private bool ValidateSeekState(CancellationToken ct, AudioPipeline pipeline, int sessionId)
    {
        return !ct.IsCancellationRequested
            && !_disposed
            && _activePipeline == pipeline
            && !_session.IsStale(sessionId);
    }

    /// <summary>Обновляет позицию decoded samples после seek.</summary>
    private static void UpdateDecodedPosition(AudioPipeline pipeline, long posMs)
    {
        long targetSamples = (long)(posMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
        pipeline.SetDecodedSamplesPosition(targetSamples);
    }

    /// <summary>Сбрасывает состояние coalescing seek.</summary>
    private void ResetSeekState()
    {
        Volatile.Write(ref _pendingSeekMs, -1);
        Volatile.Write(ref _backgroundSeekPipeline, null);
        _backgroundSeekActive = false;
    }

    #endregion
}