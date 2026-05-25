using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Suspend/Resume

    /// <summary>
    /// Приостанавливает фоновую загрузку (при сворачивании окна).
    /// 
    /// <para><b>ВАЖНО:</b> Suspend останавливает только background fill.
    /// Critical read-ahead (чанки вокруг текущей позиции) продолжает работать
    /// для бесперебойного воспроизведения. On-demand загрузка через ReadAtAsync
    /// тоже не затрагивается.</para>
    /// </summary>
    public void Suspend()
    {
        _suspendGate.Reset();
        Log.Debug("[CachingSource] Suspended (background fill paused, critical read-ahead active)");
    }

    /// <summary>
    /// Возобновляет фоновую загрузку.
    /// </summary>
    public void Resume()
    {
        _suspendGate.Set();
        Log.Debug("[CachingSource] Resumed");
    }

    #endregion

    #region Preload Loop

    /// <summary>
    /// Фоновый цикл предзагрузки чанков.
    /// 
    /// <para><b>Три режима:</b></para>
    /// <list type="bullet">
    ///   <item><b>Suspend mode:</b> Загружает только critical read-ahead (5 чанков вперёд).
    ///     Чанки загружаются последовательно (await) для надёжности при тротлинге сети.
    ///     Блокируется на _suspendGate до Resume или timeout 500ms.</item>
    ///   <item><b>Active seek mode:</b> Preload loop уступает приоритет seek path —
    ///     пропускает итерацию, если с момента последнего seek прошло менее
    ///     <see cref="SeekPreloadDebounceMs"/>. Это предотвращает конкуренцию
    ///     preload loop и critical seek downloads за download slots.</item>
    ///   <item><b>Normal mode:</b> Полный preload с parallel downloads и background fill.</item>
    /// </list>
    /// </summary>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        int idleCycles = 0;
        _backgroundChunksLoaded = 0;

        // Флаг: только что вышли из suspend — нужна пауза перед новыми загрузками.
        bool justResumed = false;

#if DEBUG
        int lastReportedProgress = -1;
#endif

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                bool isSuspended = !_suspendGate.IsSet;
                bool isPaused = !_playbackGate.IsSet;

                // PAUSE DETECT
                if (isPaused)
                {
                    try
                    {
                        _playbackGate.Wait(PlaybackGateTimeoutMs, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                if (isSuspended)
                {
                    // SUSPEND MODE: Только critical read-ahead
                    int current = Volatile.Read(ref _currentChunk);
                    var epochAtStart = Interlocked.Read(ref _downloadEpoch);

                    int criticalAhead = Math.Min(5, _config.ReadAheadChunks + 1);

                    for (int i = 0; i <= criticalAhead; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (Interlocked.Read(ref _downloadEpoch) != epochAtStart) break;

                        int idx = current + i;
                        if (idx >= _totalChunks) break;

                        if (!IsChunkAvailable(idx) && !_activeDownloads.ContainsKey(idx))
                        {
                            try
                            {
                                await EnsureChunkAsync(idx, ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { break; }
                            catch (ChunkDownloadFatalException)
                            {
                                Log.Debug($"[CachingSource] Critical chunk {idx} fatal in suspend mode");
                                break;
                            }
                            catch (Exception ex)
                            {
                                Log.Debug($"[CachingSource] Critical chunk {idx}: {ex.Message}");
                            }
                        }
                    }

                    try
                    {
                        _suspendGate.Wait(PlaybackGateCriticalTimeoutMs, ct);
                    }
                    catch (OperationCanceledException) { break; }

                    if (_suspendGate.IsSet)
                        justResumed = true;

                    continue;
                }

                // NORMAL MODE: Полный preload

                if (justResumed)
                {
                    justResumed = false;
                    try
                    {
                        await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Active seek suppression: если seek был недавно, уступаем приоритет
                // critical seek path, чтобы не конкурировать за download slots.
                long msSinceLastSeek = Environment.TickCount64 - Volatile.Read(ref _lastSeekTimestamp);
                if (msSinceLastSeek < SeekPreloadDebounceMs)
                {
                    try
                    {
                        int waitMs = (int)(SeekPreloadDebounceMs - msSinceLastSeek);
                        await Task.Delay(waitMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false);

                if (_cacheEntry.IsComplete) break;

                var epochNormal = Interlocked.Read(ref _downloadEpoch);
                var tokenNormal = CurrentDownloadToken;
                int currentNormal = Volatile.Read(ref _currentChunk);
                int pending = _activeDownloads.Count;

                if (pending >= _config.MaxConcurrentDownloads)
                {
                    idleCycles = 0;
                    continue;
                }

                bool activePreload = false;
                int chunksAhead = 0;

                for (int i = 0; i <= _config.ReadAheadChunks
                         && pending < _config.MaxConcurrentDownloads; i++)
                {
                    if (Interlocked.Read(ref _downloadEpoch) != epochNormal)
                    {
                        Log.Debug("[CachingSource] Preload: epoch changed, re-evaluating");
                        break;
                    }

                    int idx = currentNormal + i;
                    if (idx >= _totalChunks) break;

                    if (IsChunkAvailable(idx))
                    {
                        chunksAhead++;
                    }
                    else if (!_activeDownloads.ContainsKey(idx))
                    {
                        _ = SafeEnsureChunkAsync(idx, tokenNormal);
                        pending++;
                        activePreload = true;
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                }

                if (Interlocked.Read(ref _downloadEpoch) != epochNormal)
                    continue;

                if (!activePreload) idleCycles++;
                else idleCycles = 0;

                bool isResumedPartialCache = _cacheEntry.DownloadedChunks > 0
                    && !_cacheEntry.IsComplete
                    && chunksAhead >= _config.ReadAheadChunks;

                bool canBackgroundFill =
                    !activePreload
                    && (isResumedPartialCache || idleCycles >= _config.BackgroundFillIdleCycles)
                    && pending < _config.MaxConcurrentDownloads
                    && chunksAhead >= _config.MinBufferAheadForBackgroundFill
                    && (_config.MaxBackgroundChunksPerSession == 0
                        || _backgroundChunksLoaded < _config.MaxBackgroundChunksPerSession);

                if (canBackgroundFill)
                {
                    if (Interlocked.Read(ref _downloadEpoch) != epochNormal)
                        continue;

                    int fillBatch = isResumedPartialCache
                        ? Math.Min(3, _config.MaxConcurrentDownloads - pending)
                        : 1;

                    for (int f = 0; f < fillBatch; f++)
                    {
                        int? target = FindNearestMissingChunk(currentNormal);

                        if (target.HasValue
                            && target.Value < _totalChunks
                            && !IsChunkAvailable(target.Value)
                            && !_activeDownloads.ContainsKey(target.Value))
                        {
                            _ = SafeEnsureChunkAsync(target.Value, tokenNormal);
                            _backgroundChunksLoaded++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    await Task.Delay(_config.BackgroundFillIntervalMs, ct).ConfigureAwait(false);
                }

                if (_ramChunks.Count > _config.MaxRamChunks)
                    ReleaseRamBuffers();

#if DEBUG
                int progress = (int)(_cacheEntry?.DownloadProgress ?? 0);
                if (progress != lastReportedProgress)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% " +
                              $"({_cacheEntry?.DownloadedChunks ?? 0}/{_cacheEntry?.TotalChunks ?? 0})");
                    lastReportedProgress = progress;
                }
#endif
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task SafeEnsureChunkAsync(int index, CancellationToken ct)
    {
        try
        {
            await EnsureChunkAsync(index, ct);
        }
        catch (OperationCanceledException) { }
        catch (ChunkDownloadFatalException ex)
        {
            Log.Debug($"[Preload] Chunk {index} fatal: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Preload] Chunk {index} error: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    private int? FindNearestMissingChunk(int currentChunk)
    {
        if (_cacheEntry == null) return null;

        int total = Math.Min(_cacheEntry.TotalChunks, _totalChunks);

        for (int offset = 1; offset < total; offset++)
        {
            int forward = currentChunk + offset;
            if (forward < total && !IsChunkAvailable(forward))
                return forward;

            int backward = currentChunk - offset;
            if (backward >= 0 && !IsChunkAvailable(backward))
                return backward;
        }

        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>Pre-sized list с начальной ёмкостью 8 (типичное количество
    /// непрерывных диапазонов). Устраняет повторные resize List при каждом timer tick.</para>
    /// </remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_cacheEntry == null) return [];

        int total = Math.Min(_cacheEntry.TotalChunks, _totalChunks);
        if (total == 0) return [];

        var ranges = new List<(double, double)>(8);
        int? rangeStart = null;

        for (int i = 0; i < total; i++)
        {
            if (IsChunkAvailable(i))
            {
                rangeStart ??= i;
            }
            else if (rangeStart.HasValue)
            {
                ranges.Add(((double)rangeStart.Value / total, (double)i / total));
                rangeStart = null;
            }
        }

        if (rangeStart.HasValue)
            ranges.Add(((double)rangeStart.Value / total, BufferedRangeEnd));

        return ranges;
    }

    private async Task RefreshUrlAsync(CancellationToken ct)
    {
        if (_urlRefresher == null) return;

        try
        {
            var newUrl = await _urlRefresher(ct);
            if (!string.IsNullOrEmpty(newUrl))
            {
                _currentUrl = newUrl;
                Log.Info("[CachingSource] URL refreshed");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] URL refresh failed: {ex.Message}");
        }
    }

    #endregion
}