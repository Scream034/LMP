namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Preload Loop

    /// <summary>
    /// Фоновый цикл упреждающей загрузки чанков.
    /// Все параметры берутся из <see cref="_config"/>.
    /// </summary>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        int lastReportedProgress = -1;
        int idleCycles = 0;
        _backgroundChunksLoaded = 0;

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                await Task.Delay(_config.PreloadIntervalMs, ct);

                if (_cacheEntry.IsComplete)
                    break;

                int current = Volatile.Read(ref _currentChunk);
                int pending = _activeDownloads.Count;

                if (pending >= _config.MaxConcurrentDownloads)
                {
                    idleCycles = 0;
                    continue;
                }

                // ── Preload ahead ──
                bool activePreload = false;
                int chunksAhead = 0;

                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct, downloadToken);

                for (int i = 0; i <= _config.ReadAheadChunks
                         && pending < _config.MaxConcurrentDownloads; i++)
                {
                    int idx = current + i;
                    if (idx >= _cacheEntry.TotalChunks) break;

                    if (IsChunkAvailable(idx))
                    {
                        chunksAhead++;
                    }
                    else if (!_activeDownloads.ContainsKey(idx))
                    {
                        // Используем безопасный wrapper, чтобы отладчик не ловил Unobserved Exceptions
                        _ = SafeEnsureChunkAsync(idx, linkedCts.Token);
                        pending++;
                        activePreload = true;
                        await Task.Delay(50, ct);
                    }
                }

                if (!activePreload)
                    idleCycles++;
                else
                    idleCycles = 0;

                // ── Background fill ──
                bool canBackgroundFill =
                    !activePreload
                    && idleCycles >= _config.BackgroundFillIdleCycles
                    && pending < _config.MaxConcurrentDownloads
                    && chunksAhead >= _config.MinBufferAheadForBackgroundFill
                    && (_config.MaxBackgroundChunksPerSession == 0
                        || _backgroundChunksLoaded < _config.MaxBackgroundChunksPerSession);

                if (canBackgroundFill)
                {
                    int? target = FindNearestMissingChunk(current);

                    if (target.HasValue
                        && !IsChunkAvailable(target.Value)
                        && !_activeDownloads.ContainsKey(target.Value))
                    {
                        _ = SafeEnsureChunkAsync(target.Value, linkedCts.Token);
                        _backgroundChunksLoaded++;
                        await Task.Delay(_config.BackgroundFillIntervalMs, ct);
                    }
                }

                // ── Progress reporting ──
                int progress = (int)_cacheEntry.DownloadProgress;
                if (progress / 25 > lastReportedProgress / 25)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% " +
                              $"({_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks})");
                    lastReportedProgress = progress;
                }

                // ── RAM eviction ──
                if (_ramChunks.Count > _config.MaxRamChunks)
                    ReleaseRamBuffers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Подавляем ошибки в фоновом цикле
            }
        }
    }

    /// <summary>
    /// Безопасная обёртка для fire-and-forget загрузок.
    /// Предотвращает UnobservedTaskException (и остановки отладчика),
    /// если сеть обрывается в фоновой загрузке.
    /// </summary>
    private async Task SafeEnsureChunkAsync(int index, CancellationToken ct)
    {
        try
        {
            await EnsureChunkAsync(index, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exceptions.ChunkDownloadFatalException ex)
        {
            Log.Debug($"[Preload] Chunk {index} fatal error: {ex.Message}");
            // Не крашим приложение. Decoder loop дойдёт до этого чанка, 
            // вызовет EnsureChunkAsync сам и корректно передаст ошибку в UI.
        }
        catch (Exception ex)
        {
            Log.Debug($"[Preload] Chunk {index} unexpected error: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    private int? FindNearestMissingChunk(int currentChunk)
    {
        if (_cacheEntry == null) return null;
        int total = _cacheEntry.TotalChunks;

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
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_isOfflineMode)
            return [(0.0, 1.0)];

        if (_cacheEntry == null)
            return [];

        int total = _cacheEntry.TotalChunks;
        if (total == 0)
            return [];

        var ranges = new List<(double, double)>();
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
            ranges.Add(((double)rangeStart.Value / total, 1.0));

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