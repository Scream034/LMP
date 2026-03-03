using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Suspend/Resume

    /// <summary>
    /// Приостанавливает фоновую загрузку (при сворачивании окна).
    /// </summary>
    public void Suspend()
    {
        _suspendGate.Reset();
        Log.Debug("[CachingSource] Suspended");
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

    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        int lastReportedProgress = -1;
        int idleCycles = 0;
        _backgroundChunksLoaded = 0;

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                bool isSuspended = !_suspendGate.IsSet;

                if (isSuspended)
                {
                    // ═══ SUSPEND MODE: Только critical read-ahead ═══
                    // Гарантирует что при автосмене трека в фоне
                    // чанки вокруг текущей позиции будут загружены
                    int current = Volatile.Read(ref _currentChunk);
                    var epochAtStart = Interlocked.Read(ref _downloadEpoch);
                    var downloadToken = CurrentDownloadToken;

                    // Загружаем 2-3 чанка вперёд — достаточно для бесперебойного
                    // воспроизведения, но не нагружаем сеть
                    int criticalAhead = Math.Min(3, _config.ReadAheadChunks);

                    for (int i = 0; i <= criticalAhead; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (Interlocked.Read(ref _downloadEpoch) != epochAtStart) break;

                        int idx = current + i;
                        if (idx >= _totalChunks) break;

                        if (!IsChunkAvailable(idx) && !_activeDownloads.ContainsKey(idx))
                        {
                            _ = SafeEnsureChunkAsync(idx, downloadToken);
                        }
                    }

                    // Ждём resume или timeout — проверяем каждые 200ms
                    try
                    {
                        await Task.Delay(200, ct);
                    }
                    catch (OperationCanceledException) { break; }

                    continue;
                }

                // ═══ NORMAL MODE: Полный preload ═══
                await Task.Delay(_config.PreloadIntervalMs, ct);

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
                        await Task.Delay(50, ct);
                    }
                }

                if (Interlocked.Read(ref _downloadEpoch) != epochNormal)
                    continue;

                if (!activePreload) idleCycles++;
                else idleCycles = 0;

                // Background fill
                bool canBackgroundFill =
                    !activePreload
                    && idleCycles >= _config.BackgroundFillIdleCycles
                    && pending < _config.MaxConcurrentDownloads
                    && chunksAhead >= _config.MinBufferAheadForBackgroundFill
                    && (_config.MaxBackgroundChunksPerSession == 0
                        || _backgroundChunksLoaded < _config.MaxBackgroundChunksPerSession);

                if (canBackgroundFill)
                {
                    if (Interlocked.Read(ref _downloadEpoch) != epochNormal)
                        continue;

                    int? target = FindNearestMissingChunk(currentNormal);

                    if (target.HasValue
                        && target.Value < _totalChunks
                        && !IsChunkAvailable(target.Value)
                        && !_activeDownloads.ContainsKey(target.Value))
                    {
                        _ = SafeEnsureChunkAsync(target.Value, tokenNormal);
                        _backgroundChunksLoaded++;
                        await Task.Delay(_config.BackgroundFillIntervalMs, ct);
                    }
                }

                // Progress reporting
                int progress = (int)_cacheEntry.DownloadProgress;
                if (progress / 25 > lastReportedProgress / 25)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% " +
                              $"({_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks})");
                    lastReportedProgress = progress;
                }

                // RAM eviction
                if (_ramChunks.Count > _config.MaxRamChunks)
                    ReleaseRamBuffers();
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

        // Используем реальное количество чанков
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
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_isOfflineMode)
            return [(0.0, 1.0)];

        if (_cacheEntry == null)
            return [];

        // Используем реальное количество чанков
        int total = Math.Min(_cacheEntry.TotalChunks, _totalChunks);
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