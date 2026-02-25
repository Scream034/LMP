// Core/Audio/Sources/CachingStreamSource.Seeking.cs

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Алгоритм seek:</b></para>
    /// <list type="number">
    ///   <item>Находим ближайшую точку seek в контейнере (Cluster/moof boundary)</item>
    ///   <item>Вызываем <see cref="ResetDownloadEpoch"/> — все старые загрузки тихо умирают</item>
    ///   <item>Очищаем <see cref="_activeDownloads"/> — старые таски больше не отслеживаются</item>
    ///   <item>Предзагружаем чанки для новой позиции с новым download token</item>
    ///   <item>Устанавливаем позицию стрима — будит застрявшие Read() в парсере</item>
    ///   <item>Сбрасываем парсер для чтения с новой позиции</item>
    /// </list>
    /// <para>
    /// После возврата из этого метода декодер может вызывать <see cref="ReadFrameAsync"/>,
    /// которая через <see cref="ReadAtAsync"/> получит данные из новой эпохи.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null)
            return false;

        // ── Находим позицию для seek ──
        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null)
        {
            Log.Warn($"[CachingSource] No seek point for {positionMs}ms");
            return false;
        }

        long targetBytePos = seekInfo.Value.BytePosition;
        long segmentStartMs = seekInfo.Value.TimestampMs;
        int targetChunk = (int)(targetBytePos / AudioConstants.ChunkSize);

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → " +
                  $"byte {targetBytePos}, chunk {targetChunk}/{_cacheEntry?.TotalChunks}");

        // ── Сбрасываем эпоху загрузок ──
        // Все старые DownloadChunkCoreAsync получат OperationCanceledException
        // и тихо завершатся в catch (OperationCanceledException) { }
        CancellationToken newDownloadToken = ResetDownloadEpoch();

        // ── Очищаем словарь активных загрузок ──
        // Старые таски отменены через epoch CTS — не ждём, просто забываем
        foreach (int key in _activeDownloads.Keys.ToList())
            _activeDownloads.TryRemove(key, out _);

        // ── Предзагружаем чанки для новой позиции ──
        if (!_isOfflineMode && _cacheEntry != null)
        {
            try
            {
                await PreloadChunksForSeekAsync(targetChunk, newDownloadToken);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Новый seek пришёл пока грузили — предыдущий seek отменён
                return false;
            }
        }

        // ── Устанавливаем позицию стрима ──
        // Setter AsyncCachingReadStream.Position:
        //   1. Отменяет _readCts → застрявшие Read() получают OCE
        //   2. Создаёт новый _readCts
        //   3. Volatile.Write(position)
        _readStream!.Position = targetBytePos;
        _currentChunk = targetChunk;

        // ── Сбрасываем парсер ──
        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        return true;
    }

    /// <summary>
    /// Предзагружает чанки вокруг целевой позиции seek.
    /// </summary>
    /// <param name="targetChunk">Чанк, с которого начнётся чтение после seek.</param>
    /// <param name="ct">Токен новой эпохи загрузки.</param>
    private async Task PreloadChunksForSeekAsync(int targetChunk, CancellationToken ct)
    {
        if (_cacheEntry == null) return;

        var tasks = new List<Task>();
        int end = Math.Min(targetChunk + AudioConstants.SeekPreloadChunks, _cacheEntry.TotalChunks);

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