using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Suspend/Resume

    /// <summary>
    /// Приостанавливает фоновую загрузку (при сворачивании окна).
    /// Critical read-ahead продолжает работать для бесперебойного воспроизведения.
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
    /// Фоновый цикл предзагрузки диапазонов.
    /// Удерживает буфер вокруг текущей позиции, учитывает in-flight загрузки,
    /// адаптирует параллелизм и размер запросов к состоянию сети.
    /// </summary>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
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

                // ── Пауза: ждём открытия gate ──
                if (isPaused)
                {
                    try { _playbackGate.Wait(PlaybackGateTimeoutMs, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // ── Suspend mode: только critical read-ahead ──
                if (isSuspended)
                {
                    long current = Volatile.Read(ref _currentReadOffset);
                    long bufferedAhead = GetBufferedBytesAheadIncludingInflight(current);

                    if (bufferedAhead < _config.MinRequestSizeBytes)
                    {
                        long nextPos = current + bufferedAhead;
                        int nextLen = GetAlignedReadLength(nextPos, _config.MinRequestSizeBytes);

                        try
                        {
                            await EnsureRangeAsync(nextPos, nextLen, ct, isCritical: true)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (ChunkDownloadFatalException)
                        {
                            Log.Debug("[CachingSource] Critical suspended preload fatal");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[CachingSource] Suspended critical preload: {ex.Message}");
                        }
                    }

                    try { _suspendGate.Wait(PlaybackGateCriticalTimeoutMs, ct); }
                    catch (OperationCanceledException) { break; }

                    if (_suspendGate.IsSet)
                        justResumed = true;

                    continue;
                }

                // ── Дебаунс после resume ──
                if (justResumed)
                {
                    justResumed = false;
                    try { await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // ── Штатный интервал ожидания между итерациями ──
                await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false);

                if (_cacheEntry.IsComplete) break;

                // ── Snapshot текущего состояния ──
                long epochAtLoopStart = Interlocked.Read(ref _downloadEpoch);
                var token = CurrentDownloadToken;
                long currentOffset = Volatile.Read(ref _currentReadOffset);

                int adaptiveMaxDownloads = GetAdaptiveMaxConcurrentDownloads();
                int pending = GetActiveDownloadCount();

                if (pending >= adaptiveMaxDownloads)
                    continue;

                int adaptiveTargetBufferMs = GetAdaptiveTargetBufferMs();
                int refillStopMs = adaptiveTargetBufferMs + TargetBufferHysteresisMs;

                var degradation = GetNetworkDegradationLevel();

                // ── Prefetch loop: планируем запросы вперёд от текущей позиции ──
                while (pending < adaptiveMaxDownloads)
                {
                    if (Interlocked.Read(ref _downloadEpoch) != epochAtLoopStart)
                        break;

                    long plannedAheadBytes = GetBufferedBytesAheadIncludingInflight(currentOffset);
                    int plannedAheadMs = ConvertBufferedBytesToMs(plannedAheadBytes);

                    // Достаточно буферизовано — стоп
                    if (plannedAheadMs >= refillStopMs)
                        break;

                    long nextPosition = currentOffset + plannedAheadBytes;
                    if (nextPosition >= _contentLength)
                        break;

                    int requestBytes = GetAlignedReadLength(nextPosition, _config.MinRequestSizeBytes);
                    if (requestBytes <= 0)
                        break;

                    bool critical =
                        plannedAheadMs < EmergencyRefillBufferMs
                        || (pending == 0 && plannedAheadMs < CriticalRefillBufferMs);

                    _ = SafeEnsureRangeAsync(nextPosition, requestBytes, token, critical);
                    pending++;

                    // На деградированной сети — один запрос за итерацию
                    if (degradation != NetworkDegradationLevel.Normal)
                        break;

                    // На нормальной сети — только critical запросы идут цепочкой
                    if (!critical)
                        break;
                }

                // ── Trim RAM cache ──
                if (_ramCache.TotalBytes > _config.MaxRamBytes)
                    ReleaseRamBuffers();

#if DEBUG
                int progress = (int)(_cacheEntry?.DownloadProgress ?? 0);
                if (progress != lastReportedProgress)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% " +
                              $"({_cacheEntry?.DownloadedBytes ?? 0}/{_cacheEntry?.TotalSize ?? 0} bytes)");
                    lastReportedProgress = progress;
                }
#endif
            }
            catch (OperationCanceledException) { break; }
            catch { /* Защита от неожиданных исключений в фоновом цикле */ }
        }
    }

    /// <summary>
    /// Безопасная обёртка над <see cref="EnsureRangeAsync"/> для fire-and-forget вызовов.
    /// </summary>
    private async Task SafeEnsureRangeAsync(
        long position, int minimumLength, CancellationToken ct, bool isCritical)
    {
        try
        {
            await EnsureRangeAsync(position, minimumLength, ct, isCritical).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ChunkDownloadFatalException ex)
        {
            Log.Debug($"[Preload] Range {position}: fatal: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Preload] Range {position}: {ex.Message}");
        }
    }

    #endregion

    #region Buffer Ranges

    /// <inheritdoc/>
    /// <remarks>
    /// Объединяет диапазоны из disk-кэша и RAM-кэша,
    /// нормализует в <c>[0.0, 1.0]</c> относительно <see cref="_contentLength"/>.
    /// </remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_cacheEntry == null || _contentLength <= 0)
            return [];

        var diskRanges = _cacheEntry.GetDownloadedRangesSnapshot();
        var ramBlocks = _ramCache.GetRangesSnapshot();

        if (diskRanges.Length == 0 && ramBlocks.Length == 0)
            return [];

        // ── Собираем все диапазоны в один массив и сортируем ──
        var all = new List<(long Start, long End)>(diskRanges.Length + ramBlocks.Length);

        for (int i = 0; i < diskRanges.Length; i++)
            all.Add((diskRanges[i].Start, diskRanges[i].EndExclusive));

        for (int i = 0; i < ramBlocks.Length; i++)
            all.Add((ramBlocks[i].StartOffset, ramBlocks[i].EndOffsetExclusive));

        all.Sort((a, b) => a.Start.CompareTo(b.Start));

        // ── Merge overlapping ranges и нормализуем ──
        var merged = new List<(double, double)>(all.Count);
        long mergedStart = -1;
        long mergedEnd = -1;

        for (int i = 0; i < all.Count; i++)
        {
            var (s, e) = all[i];

            if (mergedStart < 0)
            {
                mergedStart = s;
                mergedEnd = e;
                continue;
            }

            if (s <= mergedEnd)
            {
                if (e > mergedEnd)
                    mergedEnd = e;
            }
            else
            {
                merged.Add(((double)mergedStart / _contentLength, (double)mergedEnd / _contentLength));
                mergedStart = s;
                mergedEnd = e;
            }
        }

        if (mergedStart >= 0)
            merged.Add(((double)mergedStart / _contentLength, (double)mergedEnd / _contentLength));

        return merged;
    }

    /// <summary>
    /// Обновляет URL потока через <see cref="_urlRefresher"/> при истечении 403.
    /// </summary>
    private async Task RefreshUrlAsync(CancellationToken ct)
    {
        if (_urlRefresher == null) return;

        try
        {
            var newUrl = await _urlRefresher(ct).ConfigureAwait(false);
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