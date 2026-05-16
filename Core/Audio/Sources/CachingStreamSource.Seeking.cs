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

        if (targetBytePos >= _contentLength)
        {
            Log.Warn($"[CachingSource] Seek position {targetBytePos} >= contentLength {_contentLength}, clamping to EOF");
            targetBytePos = Math.Max(0, _contentLength - 1);
        }

        int targetChunk = (int)(targetBytePos / _chunkSize);
        targetChunk = Math.Clamp(targetChunk, 0, _totalChunks - 1);

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → " +
                  $"byte {targetBytePos}, chunk {targetChunk}/{_totalChunks}");

        ResetDownloadEpoch();

        Volatile.Write(ref _currentChunk, targetChunk);
        _readStream!.Position = targetBytePos;
        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        if (!_isOfflineMode && _cacheEntry != null && !IsChunkAvailable(targetChunk))
        {
            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);

                Log.Debug($"[CachingSource] Seek: awaiting critical chunk {targetChunk}");
                await EnsureChunkAsync(targetChunk, linkedCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn($"[CachingSource] Seek: critical chunk {targetChunk} failed: {ex.Message}");
            }
        }

        if (!_isOfflineMode && _cacheEntry != null)
            _ = PreloadChunksForSeekFireAndForgetAsync(targetChunk);

        return true;
    }

    /// <summary>
    /// Best-effort prefetch целевого чанка для предстоящего seek.
    /// Вызывается из <see cref="AudioPlayer"/> ПАРАЛЛЕЛЬНО с остановкой decoder,
    /// до вызова <see cref="SeekAsync"/>.
    ///
    /// <para><b>Логика overlap:</b> Decoder останавливается за 1–50мс.
    /// За это время prefetch успевает загрузить чанк с SSD (~0.5мс) или по сети (~50-200мс).
    /// Если чанк окажется в <c>_ramChunks</c> до того как <see cref="SeekAsync"/>
    /// вызовет <see cref="ResetDownloadEpoch"/> — <see cref="IsChunkAvailable"/> вернёт
    /// true и SeekAsync не будет ждать сеть.</para>
    ///
    /// <para><b>Безопасность:</b> Если prefetch не успеет до epoch reset — он будет
    /// отменён и SeekAsync загрузит чанк самостоятельно через стандартный путь.</para>
    /// </summary>
    /// <param name="positionMs">Целевая позиция seek в миллисекундах.</param>
    /// <param name="ct">Токен отмены lifetime.</param>
    internal async Task TryPrefetchChunkForSeekAsync(long positionMs, CancellationToken ct)
    {
        if (_isOfflineMode || _cacheEntry == null || _parser == null) return;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));
        int targetChunk = Math.Clamp((int)(targetBytePos / _chunkSize), 0, _totalChunks - 1);

        if (IsChunkAvailable(targetChunk)) return;

        try
        {
            var downloadToken = CurrentDownloadToken;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);
            await EnsureChunkAsync(targetChunk, linkedCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Prefetch chunk {targetChunk}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fire-and-forget preload чанков для seek позиции.
    /// Не блокирует возврат из <see cref="SeekAsync"/> — decoder может начать
    /// чтение немедленно, даже если чанки ещё не загружены.
    /// </summary>
    /// <remarks>
    /// Если до завершения загрузки произойдёт новый seek (epoch change),
    /// все запросы будут отменены автоматически через <see cref="CurrentDownloadToken"/>.
    /// </remarks>
    private async Task PreloadChunksForSeekFireAndForgetAsync(int targetChunk)
    {
        try
        {
            var epochAtStart = Interlocked.Read(ref _downloadEpoch);
            var downloadToken = CurrentDownloadToken;

            int end = Math.Min(targetChunk + _config.SeekPreloadChunks, _totalChunks);
            int maxTasks = end - targetChunk;

            var tasks = new Task[maxTasks];
            int taskCount = 0;

            for (int i = targetChunk; i < end; i++)
            {
                if (Interlocked.Read(ref _downloadEpoch) != epochAtStart)
                {
                    Log.Debug("[CachingSource] Seek preload cancelled: epoch changed");
                    return;
                }

                if (!IsChunkAvailable(i))
                    tasks[taskCount++] = SafeEnsureChunkAsync(i, downloadToken);
            }

            if (taskCount > 0)
            {
                Log.Debug($"[CachingSource] Seek preloading {taskCount} chunks from {targetChunk}");
                await Task.WhenAll(tasks[..taskCount]);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Seek preload error: {ex.Message}");
        }
    }
}