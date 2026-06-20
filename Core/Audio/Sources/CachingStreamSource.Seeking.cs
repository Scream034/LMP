namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Стратегия:</b> Seek всегда возвращает <c>true</c> после установки позиции.
    /// Если данных нет — декодер заблокируется в фоне на <see cref="ReadAtAsync"/>,
    /// а <c>AudioPlayer</c> перейдёт в <c>Buffering</c> и автоматически возобновит
    /// воспроизведение, когда decoder/ring buffer наполнится.</para>
    /// <para>Это полностью развязывает actor loop плеера от задержек сети.</para>
    /// <para><b>Epoch reset policy:</b> на высокой задержке (600ms+) убийство
    /// TCP/TLS-соединений через <see cref="ResetDownloadEpoch"/> катастрофически дорого
    /// (переподключение 5–18 секунд). Поэтому epoch сбрасывается ТОЛЬКО когда
    /// данных на seek-позиции нет локально и нужна новая загрузка.
    /// Если данные уже есть — preload loop естественно подстроится
    /// к новой <see cref="_currentReadOffset"/> на следующей итерации (~500ms).</para>
    /// </remarks>
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

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → byte {targetBytePos}");

        //  1. Перемещение позиции потока и отмена pending reads 
        // НЕ делаем ResetDownloadEpoch() здесь: это убивает TCP/TLS-соединения,
        // что на высокой задержке стоит 5–18 секунд переподключения.
        _readStream!.SeekAndCancelPendingReads(targetBytePos);
        Volatile.Write(ref _currentReadOffset, targetBytePos);

        //  2. Сброс парсера для чтения с новой позиции 
        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        //  3. Быстрый путь: данные уже есть локально 
        if (HasMinimalLocalSeekStartData(targetBytePos))
        {
            Log.Debug($"[CachingSource] Seek: sufficient local prefix at {targetBytePos}, starting immediately");
            // Epoch НЕ сбрасываем: preload loop подстроится к новой позиции
            // через _currentReadOffset на следующей итерации (~500ms).
            // Живые TCP-соединения сохраняются — критично для high-latency сетей.
            _ = PreloadRangeForSeekFireAndForgetAsync(targetBytePos);
            return true;
        }

        //  4. Медленный путь: данных нет, нужна сетевая загрузка 
        // Только здесь сбрасываем epoch: это переключает preload loop
        // на новую позицию и отменяет загрузки, которые уже бесполезны.
        ResetDownloadEpoch();

        int minimalBytes = GetMinimalSeekStartBytes(targetBytePos);
        if (minimalBytes <= 0)
            return true;

        try
        {
            var downloadToken = CurrentDownloadToken;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);
            linkedCts.CancelAfter(ComputeAdaptiveSeekCriticalTimeoutMs(minimalBytes));

            Log.Debug($"[CachingSource] Seek: downloading minimal {minimalBytes} bytes at {targetBytePos}");

            await EnsureRangeAsync(targetBytePos, minimalBytes, linkedCts.Token, isCritical: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return false;

            Log.Debug("[CachingSource] Seek: critical download timed out, decoder will block until data arrives");
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Seek: critical range failed: {ex.Message}");
        }

        //  5. Запуск фоновой предзагрузки 
        _ = PreloadRangeForSeekFireAndForgetAsync(targetBytePos);

        return true;
    }

    /// <summary>
    /// Best-effort prefetch минимального startup prefix для предстоящего seek.
    /// Вызывается из <c>AudioPlayer</c> ПАРАЛЛЕЛЬНО с остановкой decoder,
    /// до вызова <see cref="SeekAsync"/>.
    /// </summary>
    /// <param name="positionMs">Целевая позиция seek в миллисекундах.</param>
    /// <param name="ct">Токен отмены (lifetime, НЕ epoch).</param>
    internal async Task TryPrefetchChunkForSeekAsync(long positionMs, CancellationToken ct)
    {
        if (_cacheEntry == null || _parser == null) return;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));

        if (HasMinimalLocalSeekStartData(targetBytePos))
            return;

        int prefetchLength = GetMinimalSeekStartBytes(targetBytePos);
        if (prefetchLength <= 0) return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ComputeAdaptiveSeekCriticalTimeoutMs(prefetchLength));

            await EnsureRangeAsync(targetBytePos, prefetchLength, timeoutCts.Token, isCritical: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Prefetch range {targetBytePos}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fire-and-forget preload диапазона вокруг seek-позиции.
    /// Не блокирует возврат из <see cref="SeekAsync"/>.
    /// </summary>
    /// <remarks>
    /// Если до завершения загрузки произойдёт новый seek (epoch change),
    /// все запросы отменятся автоматически через <see cref="CurrentDownloadToken"/>.
    /// </remarks>
    private async Task PreloadRangeForSeekFireAndForgetAsync(long targetBytePos)
    {
        try
        {
            long epochAtStart = Interlocked.Read(ref _downloadEpoch);
            var downloadToken = CurrentDownloadToken;
            int preloadLength = GetAdaptiveSeekPreloadBytes();

            if (targetBytePos >= _contentLength)
                return;

            if (preloadLength > _contentLength - targetBytePos)
                preloadLength = (int)(_contentLength - targetBytePos);

            if (preloadLength <= 0)
                return;

            if (Interlocked.Read(ref _downloadEpoch) != epochAtStart)
                return;

            if (!IsRangeLocallyAvailable(targetBytePos, preloadLength))
            {
                Log.Debug($"[CachingSource] Seek preloading range at {targetBytePos}, bytes={preloadLength}");

                await EnsureRangeAsync(targetBytePos, preloadLength, downloadToken, isCritical: false)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Seek preload error: {ex.Message}");
        }
    }
}