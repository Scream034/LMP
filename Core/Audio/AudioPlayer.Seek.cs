using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

public sealed partial class AudioPlayer
{
    /// <summary>Таймаут прогрева буфера после seek (мс).</summary>
    private const int SeekWarmupTimeoutMs = 300;

    /// <summary>Интервал проверки нового pending seek в coalescing loop (мс).</summary>
    private const int SeekCoalesceCheckIntervalMs = 30;

    /// <summary>Максимальное время ожидания в coalescing loop (мс).</summary>
    private const int SeekCoalesceMaxWaitMs = 5000;

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
        // _backgroundSeekActive = true пока HandleSeekAsync ожидает CompleteSeekWithCoalescingAsync.
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

    /// <summary>Отменяет текущую фазу seek и сбрасывает coalescing state.</summary>
    private void CancelActiveSeek()
    {
        ResetSeekState();

        var old = Interlocked.Exchange(ref _activeSeekCts, null);
        if (old == null) return;

#if DEBUG
        int restartCount = Interlocked.Increment(ref _seekRestartCount);
        if (restartCount % 5 == 0)
            Log.Warn($"[AudioPlayer] Seek restart storm: {restartCount} cancellations on current track");
#endif

        try { old.Cancel(); } catch (ObjectDisposedException) { }
        try { old.Dispose(); } catch (ObjectDisposedException) { }
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
    /// <remarks>
    /// <para><b>ИСПРАВЛЕНИЕ (Fix 3 финальный):</b></para>
    /// <para>Предыдущая версия делала fire-and-forget для
    /// <see cref="CompleteSeekWithCoalescingAsync"/>. Это немедленно возвращало управление
    /// в command processor, который мог взять следующий <see cref="SeekCommand"/> из канала
    /// и вызвать новый <see cref="HandleSeekAsync"/> → <see cref="AudioPipeline.StopDecodingAsync"/>
    /// → убивал decoder запущенный предыдущим seek → restart storm.</para>
    /// <para><b>Решение:</b> <c>await CompleteSeekWithCoalescingAsync</c> блокирует command
    /// processor на всё время seek. Пока actor ждёт, новые <c>SeekAsync</c> вызовы от UI
    /// коалесцируются через <see cref="_pendingSeekMs"/> (т.к. <see cref="_backgroundSeekActive"/>
    /// = true). Decoder запускается ровно один раз — на финальную позицию.</para>
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
            _backgroundSeekActive = true;  // блокирует coalescing ДО возврата из await

            var seekCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            var oldCts = Interlocked.Exchange(ref _activeSeekCts, seekCts);
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                try { oldCts.Dispose(); } catch (ObjectDisposedException) { }
            }

            await CompleteSeekWithCoalescingAsync(pipeline, cmd, posMs, wasPlaying, cmd.SessionId, seekCts);
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
    /// Background-фаза seek с latest-wins coalescing.
    /// </summary>
    /// <remarks>
    /// <para><b>Архитектура (финальная):</b></para>
    /// <list type="bullet">
    ///   <item>Вызывается через <c>await</c> из <see cref="HandleSeekAsync"/> —
    ///     command processor заблокирован на всё время выполнения.</item>
    ///   <item><see cref="AudioPipeline.StartDecoding"/> вызывается ПОСЛЕ coalescing loop —
    ///     ни один re-seek внутри loop не убивает decoder.</item>
    ///   <item>После <see cref="ResetSeekState"/> в <c>finally</c>,
    ///     <see cref="HandleSeekAsync"/> возвращается, command processor
    ///     обрабатывает следующую команду.</item>
    /// </list>
    /// </remarks>
    private async Task CompleteSeekWithCoalescingAsync(
        AudioPipeline pipeline, SeekCommand cmd, long initialPosMs,
        bool wasPlaying, int sessionAtStart, CancellationTokenSource seekCts)
    {
        var seekCt = seekCts.Token;

        try
        {
            long currentTargetMs = initialPosMs;

            // ── Фаза 1: Первый seek ──
            bool success = await pipeline.Source.SeekAsync(currentTargetMs, seekCt)
                .ConfigureAwait(false);

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

            // ── Фаза 2: Coalescing loop ──
            // Decoder НЕ запущен — ResetDownloadEpoch() внутри re-seek'ов безопасен.
            int totalWaitMs = 0;

            while (totalWaitMs < SeekCoalesceMaxWaitMs)
            {
                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    return;
                }

                long pendingMs = Interlocked.Exchange(ref _pendingSeekMs, -1);
                if (pendingMs < 0)
                    break; // Новых seek'ов нет

                currentTargetMs = pendingMs;

                pipeline.Flush();
                pipeline.PrepareForSeek(currentTargetMs);

                bool reseekSuccess = await pipeline.Source.SeekAsync(currentTargetMs, seekCt)
                    .ConfigureAwait(false);

                if (!reseekSuccess)
                {
                    cmd.Completion?.TrySetResult(false);
                    SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);
                    if (wasPlaying) pipeline.Start();
                    StartPositionTimerDelayed();
                    return;
                }

                UpdateDecodedPosition(pipeline, currentTargetMs);

                await Task.Delay(SeekCoalesceCheckIntervalMs, seekCt).ConfigureAwait(false);
                totalWaitMs += SeekCoalesceCheckIntervalMs;
            }

            // ── Фаза 3: Финальная валидация ──
            if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
            {
                cmd.Completion?.TrySetCanceled();
                return;
            }

            // ── Фаза 4: Decoder запускается ОДИН РАЗ на финальную позицию ──
#if DEBUG
            int restartCount = Interlocked.Increment(ref _decoderRestartCount);
            if (restartCount % 10 == 0)
                Log.Warn($"[AudioPlayer] Decoder restart churn: {restartCount} restarts on current track");
#endif

            pipeline.StartDecoding(CreateUrlRefresher(), _options, OnTrackEnded, HandleError);

            // ── Фаза 5: Ожидание буфера и возобновление ──
            if (wasPlaying && _state != PlayerState.Paused)
            {
                bool isFast = pipeline.Source.IsFullyBuffered
                    || pipeline.Source is Sources.LocalFileSource
                    || pipeline.Source.BufferProgress >= 95.0;

                int msThreshold = isFast
                    ? Math.Max(MinSeekResumeBufferMs / 4, 10)
                    : MinSeekResumeBufferMs;

                int seekThreshold = pipeline.SampleRate * pipeline.Channels * msThreshold / 1000;

                await pipeline.WaitForBufferAsync(seekThreshold, SeekWarmupTimeoutMs, seekCt)
                    .ConfigureAwait(false);

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
            try { seekCts.Dispose(); } catch (ObjectDisposedException) { }
        }
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