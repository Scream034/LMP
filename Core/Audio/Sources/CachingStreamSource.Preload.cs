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
    /// <para><b>Два режима:</b></para>
    /// <list type="bullet">
    ///   <item><b>Suspend mode:</b> Загружает только critical read-ahead (5 чанков вперёд).
    ///     Чанки загружаются последовательно (await) для надёжности при тротлинге сети.
    ///     Блокируется на _suspendGate до Resume или timeout 500ms.</item>
    ///   <item><b>Normal mode:</b> Полный preload с parallel downloads и background fill.</item>
    /// </list>
    /// </summary>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        int idleCycles = 0;
        _backgroundChunksLoaded = 0;

#if DEBUG
        int lastReportedProgress = -1;
#endif

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                bool isSuspended = !_suspendGate.IsSet;

                if (isSuspended)
                {
                    // ═══ SUSPEND MODE: Только critical read-ahead ═══
                    // Гарантирует бесперебойное воспроизведение при свёрнутом окне.
                    // В фоне ОС тротлит ThreadPool (EcoQoS), но выделенный decoder thread
                    // продолжает работать. Нужно обеспечить ему данные.
                    int current = Volatile.Read(ref _currentChunk);
                    var epochAtStart = Interlocked.Read(ref _downloadEpoch);
                    var downloadToken = CurrentDownloadToken;

                    // ═══ 5 чанков вместо 3 ═══
                    // 5 чанков × 128KB = 640KB ≈ 40 секунд при 128kbps.
                    // При тротлинге сети в фоне 3 чанков (~24 сек) может быть мало
                    // если переключение трека совпало с медленным ответом от YouTube.
                    int criticalAhead = Math.Min(5, _config.ReadAheadChunks + 1);

                    for (int i = 0; i <= criticalAhead; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (Interlocked.Read(ref _downloadEpoch) != epochAtStart) break;

                        int idx = current + i;
                        if (idx >= _totalChunks) break;

                        if (!IsChunkAvailable(idx) && !_activeDownloads.ContainsKey(idx))
                        {
                            // ═══ await вместо fire-and-forget ═══
                            // В suspend mode надёжность важнее параллелизма.
                            // Sequential загрузка гарантирует что каждый чанк
                            // реально загружен перед переходом к следующему.
                            // Fire-and-forget в фоне может "застрять" на ThreadPool scheduling.
                            try
                            {
                                await EnsureChunkAsync(idx, downloadToken);
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
                                // Продолжаем — следующий чанк может быть доступен
                            }
                        }
                    }

                    // ═══ _suspendGate.Wait вместо Task.Delay ═══
                    // Мгновенный resume вместо ожидания до 200ms.
                    // При Resume() _suspendGate.Set() немедленно разблокирует Wait.
                    // Timeout 500ms — периодическая проверка critical chunks.
                    try
                    {
                        _suspendGate.Wait(500, ct);
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
                // ═══ Для частично закэшированных треков (resumed sessions) ═══
                // Если read-ahead полностью покрыт, но трек не полный — немедленный
                // background fill без ожидания idleCycles. При 12/20 чанков и позиции
                // в начале файла preload loop мог простаивать 6+ секунд, оставляя
                // пробелы в кэше. Seek в непокрытую область → сетевой fetch → underruns.
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

                    // Для resumed partial cache — до 3 чанков за цикл вместо 1.
                    // 3 × 128KB = 384KB/цикл. При PreloadIntervalMs=200 → ~1.5MB/сек
                    // фонового заполнения. Seek в пробел станет on-demand вместо обязательного.
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

                    await Task.Delay(_config.BackgroundFillIntervalMs, ct);
                }

                // RAM eviction
                if (_ramChunks.Count > _config.MaxRamChunks)
                    ReleaseRamBuffers();

#if DEBUG
                // ═══ Progress reporting (moved out of #if DEBUG, deduplicated) ═══
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
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {   
        if (_cacheEntry == null) return [];

        int total = Math.Min(_cacheEntry.TotalChunks, _totalChunks);
        if (total == 0) return [];

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