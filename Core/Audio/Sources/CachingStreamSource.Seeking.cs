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
            return false;

        long targetBytePos = seekInfo.Value.BytePosition;
        long segmentStartMs = seekInfo.Value.TimestampMs;
        int targetChunk = (int)(targetBytePos / _chunkSize);

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → " +
                  $"byte {targetBytePos}, chunk {targetChunk}/{_cacheEntry?.TotalChunks}");

        // Сбрасываем эпоху — preload loop перестанет запускать новые загрузки
        // Но НЕ ждём завершения активных — они завершатся сами
        ResetDownloadEpoch();

        // Обновляем позицию
        Volatile.Write(ref _currentChunk, targetChunk);
        _readStream!.Position = targetBytePos;
        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        // Загружаем нужные чанки — если уже в кэше, мгновенно
        if (!_isOfflineMode && _cacheEntry != null)
        {
            try
            {
                await PreloadChunksForSeekAsync(targetChunk, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Загружает чанки для новой позиции после seek.
    /// </summary>
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