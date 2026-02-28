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

        // ═══ ВАЛИДАЦИЯ: не выходим за пределы файла ═══
        if (targetBytePos >= _contentLength)
        {
            Log.Warn($"[CachingSource] Seek position {targetBytePos} >= contentLength {_contentLength}, clamping to EOF");
            targetBytePos = Math.Max(0, _contentLength - 1);
        }

        int targetChunk = (int)(targetBytePos / _chunkSize);

        // Ограничиваем chunk index валидным диапазоном
        targetChunk = Math.Clamp(targetChunk, 0, _totalChunks - 1);

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → " +
                  $"byte {targetBytePos}, chunk {targetChunk}/{_totalChunks}");

        // ═══ EPOCH RESET — отменяем старые загрузки ═══
        // Preload loop перестанет запускать новые загрузки.
        // Активные загрузки завершатся сами (через OperationCanceledException).
        ResetDownloadEpoch();

        // ═══ POSITION UPDATE ═══
        Volatile.Write(ref _currentChunk, targetChunk);
        _readStream!.Position = targetBytePos;
        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        // ═══ NON-BLOCKING PRELOAD ═══
        // Fire-and-forget: запускаем загрузку нужных чанков в фоне.
        // Decoder loop начнёт читать немедленно — если чанк ещё не загружен,
        // ReadAtAsync скачает его on-demand (через EnsureChunkAsync).
        // Preload здесь — оптимизация, а не обязательное условие.
        if (!_isOfflineMode && _cacheEntry != null)
        {
            _ = PreloadChunksForSeekFireAndForgetAsync(targetChunk);
        }

        return true;
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

            var tasks = new List<Task>();

            for (int i = targetChunk; i < end; i++)
            {
                // Проверяем epoch — если новый seek произошёл, прерываемся
                if (Interlocked.Read(ref _downloadEpoch) != epochAtStart)
                {
                    Log.Debug("[CachingSource] Seek preload cancelled: epoch changed");
                    return;
                }

                if (!IsChunkAvailable(i))
                    tasks.Add(SafeEnsureChunkAsync(i, downloadToken));
            }

            if (tasks.Count > 0)
            {
                Log.Debug($"[CachingSource] Seek preloading {tasks.Count} chunks from {targetChunk}");
                await Task.WhenAll(tasks);
            }
        }
        catch (OperationCanceledException)
        {
            // Epoch changed или dispose — ожидаемо
        }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Seek preload error: {ex.Message}");
        }
    }
}