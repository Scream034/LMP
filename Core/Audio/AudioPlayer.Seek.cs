using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

public sealed partial class AudioPlayer
{
    /// <summary>
    /// Таймаут прогрева буфера после seek для streaming контента (мс).
    /// Для cached контента используется <see cref="SeekWarmupTimeoutCachedMs"/>.
    /// </summary>
    private const int SeekWarmupTimeoutMs = 150;

    /// <summary>
    /// Таймаут прогрева буфера после seek для cached/local контента (мс).
    /// Существенно короче <see cref="SeekWarmupTimeoutMs"/>: данные уже на диске,
    /// задержка определяется только скоростью декодирования.
    /// </summary>
    private const int SeekWarmupTimeoutCachedMs = 50;

    /// <summary>
    /// Максимальное количество итераций основного seek-loop.
    /// Защита от бесконечного цикла при патологическом потоке seek-событий.
    /// Значение 50 покрывает даже агрессивный scrubbing (~10 seeks/sec × 5 sec).
    /// </summary>
    private const int SeekLoopMaxIterations = 50;

    /// <summary>Таймаут остановки декодера при re-seek внутри loop (мс).</summary>
    private const int ReSeekDecoderStopTimeoutMs = 200;

    /// <summary>
    /// Порог BufferProgress для определения «быстрого» источника (%).
    /// Снижен с 95% до 80%: при 80%+ cached трек для seek не требует HTTP,
    /// данные читаются с диска. Предыдущий порог 95% заставлял WaitForBuffer
    /// ждать полный <see cref="MinSeekResumeBufferMs"/> на 92% cached треках.
    /// </summary>
    private const double FastSourceBufferThreshold = 80.0;

    /// <summary>
    /// Максимальный таймаут Phase A (source seek + critical chunk download).
    /// </summary>
    /// <remarks>
    /// <para>Phase A может блокировать actor loop на всё время HTTP download,
    /// что при scrubbing по незакачанному контенту превращается в секундные freeze'ы.
    /// Этот таймаут ограничивает максимальное ожидание: если source seek не завершился
    /// за это время, seek прерывается и новый pending seek обрабатывается немедленно.</para>
    /// </remarks>
    private const int SourceSeekTimeoutMs = 500;

    /// <summary>CTS текущей фоновой фазы seek.</summary>
    private CancellationTokenSource? _activeSeekCts;

    /// <summary>
    /// Pending seek позиция для latest-wins coalescing.
    /// -1 = нет pending seek.
    /// </summary>
    private long _pendingSeekMs = -1;

    /// <summary>Флаг активного seek (HandleSeekAsync выполняется).</summary>
    private volatile bool _backgroundSeekActive;

    /// <summary>
    /// Pipeline, к которому относится текущий seek.
    /// Защищает от утечки seek-state между треками.
    /// </summary>
    private AudioPipeline? _backgroundSeekPipeline;

#if DEBUG
    /// <summary>Счётчик принудительных перезапусков seek.</summary>
    private int _seekRestartCount;

    /// <summary>Счётчик перезапусков decoder loop в рамках текущего трека.</summary>
    private int _decoderRestartCount;
#endif

    /// <summary>
    /// Инициирует seek с latest-wins coalescing.
    /// </summary>
    /// <remarks>
    /// <para>Если <see cref="HandleSeekAsync"/> уже выполняется (включая ожидание
    /// <see cref="CompleteSeekWithCoalescingAsync"/>), новый seek только обновляет
    /// <see cref="_pendingSeekMs"/> без создания нового SeekCommand и без перезапуска decoder.</para>
    /// <para>Coalescing pipeline-bound: seek нового трека никогда не коалесцируется
    /// в background task старого pipeline.</para>
    /// </remarks>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed || _state is not (PlayerState.Playing or PlayerState.Paused))
            return ValueTask.CompletedTask;

        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
            return ValueTask.CompletedTask;

        // Coalescing: если seek активен для того же pipeline — только обновляем позицию.
        if (_backgroundSeekActive && ReferenceEquals(Volatile.Read(ref _backgroundSeekPipeline), pipeline))
        {
            Volatile.Write(ref _pendingSeekMs, (long)position.TotalMilliseconds);
            return ValueTask.CompletedTask;
        }

        CancelActiveSeek();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));

        _commandChannel.Writer.TryWrite(new SeekCommand(position, _session.Current, tcs));
        return new ValueTask(tcs.Task);
    }

    /// <summary>
    /// Отменяет текущую фазу seek без Dispose CTS.
    /// </summary>
    /// <remarks>
    /// <para><b>Ownership model (v3):</b></para>
    /// <para>CancelActiveSeek только Cancel. Dispose CTS —
    /// исключительно в <c>finally</c> блоке <see cref="CompleteSeekWithCoalescingAsync"/>.
    /// Устраняет <see cref="ObjectDisposedException"/> от двойного Dispose.</para>
    /// </remarks>
    private void CancelActiveSeek()
    {
        ResetSeekState();

        var cts = Volatile.Read(ref _activeSeekCts);
        if (cts == null) return;

#if DEBUG
        int restartCount = Interlocked.Increment(ref _seekRestartCount);
        if (restartCount % 5 == 0)
            Log.Warn($"[AudioPlayer] Seek restart storm: {restartCount} cancellations on current track");
#endif

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
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

    /// <summary>
    /// Обрабатывает seek: синхронная actor-фаза + ожидание background-фазы с coalescing.
    /// </summary>
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
            // Prefetch целевого чанка параллельно с остановкой decoder
            if (pipeline.Source is Sources.CachingStreamSource cachingSource)
            {
                _ = cachingSource.TryPrefetchChunkForSeekAsync(
                    (long)cmd.Position.TotalMilliseconds, _lifetimeCts.Token);
            }

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

            long posMs = (long)cmd.Position.TotalMilliseconds;
            pipeline.PrepareForSeek(posMs);

            Volatile.Write(ref _pendingSeekMs, -1);
            Volatile.Write(ref _backgroundSeekPipeline, pipeline);
            _backgroundSeekActive = true;

            var seekCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            var oldCts = Interlocked.Exchange(ref _activeSeekCts, seekCts);
            if (oldCts != null)
            {
                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            await CompleteSeekWithCoalescingAsync(pipeline, cmd, posMs, wasPlaying, cmd.SessionId, seekCts)
                .ConfigureAwait(false);
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

    /// <summary>
    /// Background-фаза seek с full-lifecycle coalescing.
    /// </summary>
    /// <remarks>
    /// <para><b>Архитектура (v4 — bounded Phase A + fast coalescing):</b></para>
    /// <list type="bullet">
    ///   <item>Вызывается через <c>await</c> из <see cref="HandleSeekAsync"/> —
    ///     command processor заблокирован на всё время выполнения.</item>
    ///   <item>Весь seek обёрнут в <c>while(true)</c> loop с checkpoint'ами
    ///     перед каждым критическим переходом.</item>
    ///   <item>Decoder запускается ОДИН РАЗ на финальную позицию.</item>
    /// </list>
    ///
    /// <para><b>Исправления v1→v2→v3→v4:</b></para>
    /// <list type="number">
    ///   <item>v2: Full-lifecycle coalescing (seeks на Phase D/F не теряются).</item>
    ///   <item>v2: <c>Task.Delay</c> polling → <see cref="DrainPendingSeekMs"/> (zero latency).</item>
    ///   <item>v2: CancelActiveSeek: Cancel-only, Dispose в finally.</item>
    ///   <item>v3: Smart <see cref="ComputeSeekWarmupParams"/> — cached контент
    ///     пропускает WaitForBuffer или использует минимальный таймаут.</item>
    ///   <item>v3: <see cref="FastSourceBufferThreshold"/> снижен с 95% до 80%.</item>
    ///   <item>v4: Phase A ограничена <see cref="SourceSeekTimeoutMs"/> — source seek
    ///     по незакачанному контенту не может блокировать actor дольше 500ms.
    ///     При timeout pending seek дренируется немедленно, устраняя каскадные freeze'ы
    ///     при aggressive scrubbing.</item>
    /// </list>
    /// </remarks>
    private async Task CompleteSeekWithCoalescingAsync(
        AudioPipeline pipeline, SeekCommand cmd, long initialPosMs,
        bool wasPlaying, int sessionAtStart, CancellationTokenSource seekCts)
    {
        var seekCt = seekCts.Token;
        bool decoderStarted = false;

        try
        {
            long currentTargetMs = initialPosMs;
            int iteration = 0;

            while (iteration++ < SeekLoopMaxIterations)
            {
                // ── Phase A: Seek source (bounded) ──
                bool success;
                try
                {
                    var sourceSeekTask = pipeline.Source
                        .SeekAsync(currentTargetMs, seekCt)
                        .AsTask();

                    var completed = await Task.WhenAny(
                        sourceSeekTask,
                        Task.Delay(SourceSeekTimeoutMs, seekCt)).ConfigureAwait(false);

                    if (completed == sourceSeekTask)
                    {
                        success = await sourceSeekTask.ConfigureAwait(false);
                    }
                    else
                    {
                        // Source seek не завершился за SourceSeekTimeoutMs.
                        // Проверяем: пришёл ли новый seek пока мы ждали?
                        long drained = DrainPendingSeekMs();
                        if (drained >= 0)
                        {
                            // Новый seek пришёл — переходим к нему немедленно.
                            currentTargetMs = drained;
                            pipeline.Flush();
                            pipeline.PrepareForSeek(currentTargetMs);
                            continue;
                        }

                        // Нет нового seek — дожидываемся source seek до конца.
                        success = await sourceSeekTask.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
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

                UpdateDecodedPosition(pipeline, currentTargetMs);

                // ── Phase B: Immediate drain — zero-latency coalescing ──
                {
                    long drained = DrainPendingSeekMs();
                    if (drained >= 0)
                    {
                        currentTargetMs = drained;
                        pipeline.Flush();
                        pipeline.PrepareForSeek(currentTargetMs);
                        continue;
                    }
                }

                // ── Phase C: Validation ──
                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    return;
                }

                // ── Phase D: StartDecoding — ОДИН РАЗ на финальную позицию ──
#if DEBUG
                int restartCount = Interlocked.Increment(ref _decoderRestartCount);
                if (restartCount % 10 == 0)
                    Log.Warn($"[AudioPlayer] Decoder restart churn: {restartCount} restarts on current track");
#endif

                pipeline.StartDecoding(
                    CreateUrlRefresher(), _options,
                    CreateTrackEndedCallback(cmd.SessionId), HandleError);
                decoderStarted = true;

                // Checkpoint: seek мог прилететь за время между Phase B и StartDecoding
                {
                    long drained = DrainPendingSeekMs();
                    if (drained >= 0)
                    {
                        currentTargetMs = drained;
                        await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                        decoderStarted = false;
                        pipeline.PrepareForSeek(currentTargetMs);
                        continue;
                    }
                }

                // ── Phase E: Ожидание буфера и возобновление ──
                if (wasPlaying && _state != PlayerState.Paused)
                {
                    var (seekThreshold, warmupTimeout) =
                        ComputeSeekWarmupParams(pipeline, currentTargetMs);

                    if (seekThreshold > 0 && warmupTimeout > 0)
                    {
                        await pipeline.WaitForBufferAsync(seekThreshold, warmupTimeout, seekCt)
                            .ConfigureAwait(false);
                    }

                    // ── Phase F: Финальный checkpoint ──
                    {
                        long drained = DrainPendingSeekMs();
                        if (drained >= 0)
                        {
                            currentTargetMs = drained;
                            await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                            decoderStarted = false;
                            pipeline.PrepareForSeek(currentTargetMs);
                            continue;
                        }
                    }

                    if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                    {
                        cmd.Completion?.TrySetCanceled();
                        return;
                    }

                    ResumePlaybackSequence(
                        pipeline, startTimers: false, configurePipeline: true,
                        trackId: _currentTrackId);
                }
                else
                {
                    SetState(PlayerState.Paused);
                }

                // ── Seek завершён ──
                StartPositionTimerDelayed();
                _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
                cmd.Completion?.TrySetResult(true);
                return;
            }

            // Exhausted — fallback
            Log.Warn($"[AudioPlayer] Seek coalescing loop exhausted ({SeekLoopMaxIterations} iterations)");
            if (decoderStarted && wasPlaying && _state != PlayerState.Paused)
            {
                ResumePlaybackSequence(pipeline, startTimers: false,
                    configurePipeline: true, trackId: _currentTrackId);
            }

            StartPositionTimerDelayed();
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
            cmd.Completion?.TrySetResult(true);
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
    /// Вычисляет параметры WaitForBuffer на основе реального состояния источника.
    /// </summary>
    /// <remarks>
    /// <para><b>Архитектура (v3):</b></para>
    /// <list type="bullet">
    ///   <item><b>Fully cached / LocalFile:</b> threshold=0, timeout=0 —
    ///     пропуск WaitForBuffer целиком. Данные на диске, decoder заполнит
    ///     ring buffer быстрее, чем backend его опустошит.</item>
    ///   <item><b>Target chunk cached (≥80% overall):</b> минимальный threshold
    ///     и короткий timeout. Целевой чанк уже в RAM/на диске, decoder
    ///     начнёт выдавать PCM мгновенно.</item>
    ///   <item><b>Streaming (target chunk не cached):</b> полный threshold
    ///     и стандартный timeout — нужно дождаться HTTP-загрузки.</item>
    /// </list>
    /// <para>Предыдущая версия использовала единый <c>BufferProgress ≥ 95%</c>
    /// порог, из-за которого 92% cached трек ждал полные 300ms.</para>
    /// </remarks>
    /// <param name="pipeline">Активный pipeline.</param>
    /// <param name="targetMs">Целевая позиция seek в миллисекундах.</param>
    /// <returns>
    /// Кортеж (seekThreshold в samples, warmupTimeout в мс).
    /// Оба = 0 означает пропуск WaitForBuffer.
    /// </returns>
    private static (int seekThreshold, int warmupTimeout) ComputeSeekWarmupParams(
        AudioPipeline pipeline, long targetMs)
    {
        var source = pipeline.Source;
        int rate = pipeline.SampleRate;
        int channels = pipeline.Channels;

        // ── Fast path: полностью кэшированные / локальные ──
        if (source.IsFullyBuffered || source is Sources.LocalFileSource)
            return (0, 0);

        // ── Medium path: target chunk уже доступен ──
        if (source is Sources.CachingStreamSource caching)
        {
            bool targetChunkCached = caching.IsTargetChunkAvailable(targetMs);

            if (targetChunkCached || source.BufferProgress >= FastSourceBufferThreshold)
            {
                // Минимальный порог: ~10ms буфера — достаточно для бесшовного resume
                int minThreshold = rate * channels * 10 / 1000;
                return (Math.Max(minThreshold, 1), SeekWarmupTimeoutCachedMs);
            }
        }
        else if (source.BufferProgress >= FastSourceBufferThreshold)
        {
            int minThreshold = rate * channels * 10 / 1000;
            return (Math.Max(minThreshold, 1), SeekWarmupTimeoutCachedMs);
        }

        // ── Slow path: streaming, target chunk не cached ──
        int fullThreshold = rate * channels * MinSeekResumeBufferMs / 1000;
        return (fullThreshold, SeekWarmupTimeoutMs);
    }

    /// <summary>
    /// Атомарно забирает pending seek позицию (latest-wins).
    /// </summary>
    /// <returns>
    /// Позиция в миллисекундах, или <c>-1</c> если pending seek отсутствует.
    /// </returns>
    private long DrainPendingSeekMs()
        => Interlocked.Exchange(ref _pendingSeekMs, -1);

    /// <summary>
    /// Останавливает decoder и очищает буферы для re-seek внутри coalescing loop.
    /// </summary>
    /// <param name="pipeline">Активный pipeline.</param>
    /// <param name="ct">Токен отмены seek-фазы.</param>
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
}