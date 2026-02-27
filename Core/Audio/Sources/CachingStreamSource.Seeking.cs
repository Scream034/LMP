namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <inheritdoc/>
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null)
            return false;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null)
        {
            Log.Warn($"[CachingSource] No seek point for {positionMs}ms");
            return false;
        }

        long targetBytePos = seekInfo.Value.BytePosition;
        long segmentStartMs = seekInfo.Value.TimestampMs;
        int targetChunk = (int)(targetBytePos / _chunkSize);

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → " +
                  $"byte {targetBytePos}, chunk {targetChunk}/{_cacheEntry?.TotalChunks}");

        // Ждём завершения активных загрузок перед сбросом эпохи (graceful)
        await WaitForActiveDownloadsAsync(ct);

        // Сбрасываем эпоху — CancelAfter(50ms) даёт время завершить TLS фрейм
        CancellationToken newDownloadToken = ResetDownloadEpoch();

        // Очищаем словарь активных загрузок
        foreach (int key in _activeDownloads.Keys.ToList())
            _activeDownloads.TryRemove(key, out _);

        // Предзагружаем чанки для новой позиции
        if (!_isOfflineMode && _cacheEntry != null)
        {
            try
            {
                await PreloadChunksForSeekAsync(targetChunk, newDownloadToken);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        // Устанавливаем позицию стрима
        _readStream!.Position = targetBytePos;
        _currentChunk = targetChunk;

        // Сбрасываем парсер
        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        return true;
    }

    /// <summary>
    /// Ждёт завершения активных загрузок (до 100мс).
    /// Предотвращает обрыв HTTP запросов в середине TLS фрейма.
    /// </summary>
    private async Task WaitForActiveDownloadsAsync(CancellationToken ct)
    {
        if (_activeDownloads.IsEmpty)
            return;

        var activeTasks = _activeDownloads.Values.ToList();
        if (activeTasks.Count == 0)
            return;

        try
        {
            // Даём активным загрузкам до 100мс на завершение
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(100);
            
            await Task.WhenAll(activeTasks)
                .WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout или внешняя отмена — продолжаем с ResetEpoch
        }
        catch (Exception)
        {
            // Ошибки в загрузках — не блокируем seek
        }
    }

    private async Task PreloadChunksForSeekAsync(int targetChunk, CancellationToken ct)
    {
        if (_cacheEntry == null) return;

        var tasks = new List<Task>();
        int end = Math.Min(targetChunk + _config.SeekPreloadChunks, _cacheEntry.TotalChunks);

        for (int i = targetChunk; i < end; i++)
        {
            if (!IsChunkAvailable(i))
                tasks.Add(EnsureChunkAsync(i, ct));
        }

        if (tasks.Count > 0)
        {
            Log.Debug($"[CachingSource] Seek preloading {tasks.Count} chunks from {targetChunk}");
            await Task.WhenAll(tasks);
        }
    }
}