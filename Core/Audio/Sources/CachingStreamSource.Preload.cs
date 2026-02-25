// Core/Audio/Sources/CachingStreamSource.Preload.cs

using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Preload Loop

    /// <summary>
    /// Фоновый цикл упреждающей загрузки чанков.
    /// </summary>
    /// <remarks>
    /// <para><b>Стратегия:</b></para>
    /// <list type="number">
    ///   <item>Приоритет: чанки впереди текущей позиции (preload ahead)</item>
    ///   <item>После заполнения буфера впереди — докачка остальных (background fill)</item>
    ///   <item>Все загрузки привязаны к текущей download epoch</item>
    /// </list>
    /// <para>
    /// При seek эпоха сменится → linked CTS отменится → цикл подхватит новую эпоху.
    /// </para>
    /// </remarks>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        int lastReportedProgress = -1;
        int idleCycles = 0;
        _backgroundChunksLoaded = 0;

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                await Task.Delay(PreloadIntervalMs, ct);

                if (_cacheEntry.IsComplete)
                    break;

                int current = Volatile.Read(ref _currentChunk);
                int pending = _activeDownloads.Count;

                if (pending >= MaxConcurrentDownloads)
                {
                    idleCycles = 0;
                    continue;
                }

                // ── Preload ahead ──
                bool activePreload = false;
                int chunksAhead = 0;

                // Связываем с текущей download epoch
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct, downloadToken);

                for (int i = 0; i <= PreloadAheadChunks && pending < MaxConcurrentDownloads; i++)
                {
                    int idx = current + i;
                    if (idx >= _cacheEntry.TotalChunks) break;

                    if (IsChunkAvailable(idx))
                    {
                        chunksAhead++;
                    }
                    else if (!_activeDownloads.ContainsKey(idx))
                    {
                        // Fire-and-forget с epoch token
                        _ = EnsureChunkAsync(idx, linkedCts.Token);
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
                    && idleCycles >= BackgroundFillIdleCycles
                    && pending < MaxConcurrentDownloads
                    && chunksAhead >= MinBufferAheadForBackgroundFill
                    && (MaxBackgroundChunksPerSession == 0
                        || _backgroundChunksLoaded < MaxBackgroundChunksPerSession);

                if (canBackgroundFill)
                {
                    int? target = FindNearestMissingChunk(current);

                    if (target.HasValue
                        && !IsChunkAvailable(target.Value)
                        && !_activeDownloads.ContainsKey(target.Value))
                    {
                        _ = EnsureChunkAsync(target.Value, linkedCts.Token);
                        _backgroundChunksLoaded++;
                        await Task.Delay(BackgroundFillIntervalMs, ct);
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
                if (_ramChunks.Count > MaxRamChunks)
                    ReleaseRamBuffers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Подавляем все ошибки в фоновом цикле — не валим приложение
            }
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Находит ближайший незагруженный чанк относительно текущей позиции.
    /// Ищет сначала вперёд, потом назад.
    /// </summary>
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

    /// <summary>
    /// Обновляет URL потока (при 403 Forbidden).
    /// </summary>
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