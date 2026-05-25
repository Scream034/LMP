namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Минимальный интервал между seek-initiated preload fire-and-forget запусками (мс).
    /// </summary>
    /// <remarks>
    /// <para>При scrubbing (быстрое перетаскивание слайдера) пользователь генерирует
    /// 5–15 seek'ов за 2–3 секунды. Каждый seek ранее запускал preload на 3 чанка →
    /// ~40 HTTP-запросов, из которых ~36 немедленно отменялись следующим epoch reset.</para>
    /// <para>Задержка 300ms подавляет промежуточные preload'ы: если следующий seek
    /// приходит раньше, предыдущий preload не запускается. Только финальная позиция
    /// (после стабилизации слайдера) инициирует реальную загрузку.</para>
    /// </remarks>
    private const int SeekPreloadDebounceMs = 300;

    /// <summary>
    /// Timestamp последнего вызова <see cref="SeekAsync"/> (UTC ticks).
    /// Используется для debounce seek-initiated preload.
    /// </summary>
    private long _lastSeekTimestamp;

    /// <inheritdoc/>
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct)
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

        // Записываем timestamp ДО epoch reset — debounce preload использует это значение.
        long seekTimestamp = Environment.TickCount64;
        Volatile.Write(ref _lastSeekTimestamp, seekTimestamp);

        ResetDownloadEpoch();

        Volatile.Write(ref _currentChunk, targetChunk);

        // Явный cancel pending reads — не через Position setter
        _readStream!.SeekAndCancelPendingReads(targetBytePos);

        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

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

        // Debounced preload: запускаем fire-and-forget загрузку окружающих чанков
        // только если в течение SeekPreloadDebounceMs не пришёл следующий seek.
        // При scrubbing промежуточные seek'и не генерируют бессмысленные HTTP-запросы,
        // которые будут немедленно отменены следующим epoch reset.
        if (_cacheEntry != null)
            _ = DebouncedPreloadForSeekAsync(targetChunk, seekTimestamp);

        return true;
    }

    /// <summary>
    /// Запускает preload окружающих чанков только если за время debounce не пришёл новый seek.
    /// </summary>
    /// <remarks>
    /// <para>При scrubbing (10 seek'ов за 3 сек) каждый промежуточный seek ранее
    /// запускал <see cref="PreloadChunksForSeekFireAndForgetAsync"/>, который стартовал
    /// 3 HTTP GET'а — итого ~30 запросов, из которых ~27 немедленно отменялись.
    /// Теперь реальный preload запускается только для финальной позиции.</para>
    /// </remarks>
    /// <param name="targetChunk">Чанк целевой позиции seek.</param>
    /// <param name="seekTimestamp">Timestamp вызова seek для проверки актуальности.</param>
    private async Task DebouncedPreloadForSeekAsync(int targetChunk, long seekTimestamp)
    {
        try
        {
            await Task.Delay(SeekPreloadDebounceMs).ConfigureAwait(false);

            // Если за время ожидания пришёл новый seek — этот preload уже неактуален.
            if (Volatile.Read(ref _lastSeekTimestamp) != seekTimestamp)
                return;

            if (_disposed || _cacheEntry is { IsComplete: true })
                return;

            await PreloadChunksForSeekFireAndForgetAsync(targetChunk).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Debounced preload error: {ex.Message}");
        }
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