namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <inheritdoc/>
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null) return false;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return false;

        long targetBytePos = seekInfo.Value.BytePosition;
        long segmentStartMs = seekInfo.Value.TimestampMs;

        if (targetBytePos >= _contentLength)
        {
            Log.Warn($"[CachingSource] Seek position {targetBytePos} >= contentLength {_contentLength}, clamping to EOF");
            targetBytePos = Math.Max(SeekLowerBound, _contentLength - SeekEndOffset);
        }

        int targetChunk = Math.Clamp((int)(targetBytePos / _chunkSize), 0, _totalChunks - 1);

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → byte {targetBytePos}, chunk {targetChunk}/{_totalChunks}");

        // Если TryPrefetchChunkForSeekAsync (запущенный параллельно из AudioPlayer)
        // успел загрузить чанк в _ramChunks ДО этой строки — он уже доступен.
        // ResetDownloadEpoch отменяет только IN-FLIGHT загрузки, данные в _ramChunks не трогает.
        ResetDownloadEpoch();

        Volatile.Write(ref _currentChunk, targetChunk);

        // ═══ FIX 4 (partial): Явный cancel pending reads — не через Position setter ═══
        _readStream!.SeekAndCancelPendingReads(targetBytePos);

        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        // Если prefetch успел положить чанк в _ramChunks — skip HTTP полностью.
        // IsChunkAvailable проверяет и _ramChunks, и дисковый кэш.
        if (_cacheEntry != null && !IsChunkAvailable(targetChunk))
        {
            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);

                Log.Debug($"[CachingSource] Seek: awaiting critical chunk {targetChunk}");
                await EnsureChunkAsync(targetChunk, linkedCts.Token, isCritical: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn($"[CachingSource] Seek: critical chunk {targetChunk} failed: {ex.Message}");
            }
        }

        if (_cacheEntry != null)
            _ = PreloadChunksForSeekFireAndForgetAsync(targetChunk);

        return true;
    }

    /// <summary>
    /// Best-effort prefetch целевого чанка для предстоящего seek.
    /// Вызывается из <see cref="AudioPlayer"/> ПАРАЛЛЕЛЬНО с остановкой decoder,
    /// до вызова <see cref="SeekAsync"/>.
    ///
    /// <para>Предыдущая реализация захватывала <c>CurrentDownloadToken</c> (epoch N),
    /// но <see cref="SeekAsync"/> первым делом вызывает <see cref="ResetDownloadEpoch"/>
    /// (cancel epoch N) — prefetch гарантированно отменялся до завершения.
    /// Теперь используется только <paramref name="ct"/> (lifetime token), который
    /// переживает epoch reset. Если чанк успевает попасть в <c>_ramChunks</c> —
    /// <see cref="SeekAsync"/> найдёт его через <see cref="IsChunkAvailable"/>
    /// и пропустит HTTP-запрос.</para>
    /// </summary>
    /// <param name="positionMs">Целевая позиция seek в миллисекундах.</param>
    /// <param name="ct">Токен отмены lifetime (НЕ epoch).</param>
    internal async Task TryPrefetchChunkForSeekAsync(long positionMs, CancellationToken ct)
    {
        if (_cacheEntry == null || _parser == null) return;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));
        int targetChunk = Math.Clamp((int)(targetBytePos / _chunkSize), 0, _totalChunks - 1);

        if (IsChunkAvailable(targetChunk)) return;

        try
        {
            // Epoch будет сброшен в SeekAsync.ResetDownloadEpoch().
            // Lifetime token отменяется только при полном Dispose/Stop.
            await EnsureChunkAsync(targetChunk, ct, isCritical: true).ConfigureAwait(false);
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
    /// <para>Если до завершения загрузки произойдёт новый seek (epoch change),
    /// все запросы будут отменены автоматически через <see cref="CurrentDownloadToken"/>.</para>
    /// </remarks>
    /// <param name="targetChunk">Индекс чанка целевой позиции seek.</param>
    private async Task PreloadChunksForSeekFireAndForgetAsync(int targetChunk)
    {
        try
        {
            var epochAtStart = Interlocked.Read(ref _downloadEpoch);
            var downloadToken = CurrentDownloadToken;

            int end = Math.Min(targetChunk + _config.SeekPreloadChunks, _totalChunks);
            int maxTasks = end - targetChunk;

            // ═══ : ArrayPool вместо new Task[] на каждый seek ═══
            var tasks = System.Buffers.ArrayPool<Task>.Shared.Rent(maxTasks);
            int taskCount = 0;

            try
            {
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
                    await Task.WhenAll(tasks.AsSpan(0, taskCount)).ConfigureAwait(false);
                }
            }
            finally
            {
                // Очищаем ссылки на завершённые Task перед возвратом в пул,
                // предотвращая удержание GC-корней.
                Array.Clear(tasks, 0, taskCount);
                System.Buffers.ArrayPool<Task>.Shared.Return(tasks);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Seek preload error: {ex.Message}");
        }
    }
}