using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Максимум попыток в <see cref="ReadAtAsync"/> при смене эпохи.
    /// </summary>
    private const int ReadAtMaxEpochRetries = 5;

    /// <summary>
    /// Пауза между retry в <see cref="ReadAtAsync"/> при смене эпохи (ms).
    /// </summary>
    private const int ReadAtEpochRetryDelayMs = 50;

    /// <summary>
    /// Координация URL refresh: только один refresh одновременно.
    /// </summary>
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// Timestamp последнего успешного refresh — для cooldown.
    /// </summary>
    private DateTime _lastRefreshTime = DateTime.MinValue;

    /// <summary>
    /// Счётчик последовательных 403 после refresh — для circuit breaker.
    /// </summary>
    private int _consecutive403Count;

    /// <summary>
    /// Минимальный интервал между refresh запросами (ms).
    /// </summary>
    private const int RefreshCooldownMs = 3000;

    /// <summary>
    /// Максимум последовательных 403 перед остановкой попыток.
    /// </summary>
    private const int Max403BeforeGiveUp = 3;

    /// <summary>
    /// Задержка перед retry после refresh (ms).
    /// </summary>
    private const int PostRefreshRetryDelayMs = 500;

    // ═══════════════════════════════════════════════════════
    // YOUTUBE STREAMING — Sequential Request Number
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Последовательный номер запроса к YouTube (параметр &amp;rn=).
    /// Инкрементируется перед каждым HTTP запросом чанка.
    /// </summary>
    private int _requestSequenceNumber;

    #region ReadAtAsync

    /// <summary>
    /// Читает данные из указанной позиции, загружая чанки по необходимости.
    /// </summary>
    /// <exception cref="ChunkDownloadFatalException">
    /// Выбрасывается когда загрузка чанка невозможна (403 circuit breaker, UMP format и т.д.).
    /// </exception>
    internal async Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        // ── Настоящий EOF ──
        if (position >= _contentLength)
            return 0;

        int chunkIndex = (int)(position / ChunkSize);
        int offsetInChunk = (int)(position % ChunkSize);

        // ── Быстрый путь: чанк в RAM ──
        if (_ramChunks.TryGetValue(chunkIndex, out var ramData))
            return CopyFromChunk(ramData, offsetInChunk, buffer);

        // ── Средний путь: чанк на диске ──
        var diskData = await _cacheManager.ReadChunkAsync(_cacheKey, chunkIndex, ct);
        if (diskData != null)
        {
            _ramChunks.TryAdd(chunkIndex, diskData);
            return CopyFromChunk(diskData, offsetInChunk, buffer);
        }

        // ── Медленный путь: нужно скачать ──
        for (int attempt = 0; attempt < ReadAtMaxEpochRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct, downloadToken);

                // EnsureChunkAsync теперь выбрасывает ChunkDownloadFatalException
                await EnsureChunkAsync(chunkIndex, linkedCts.Token);

                if (_ramChunks.TryGetValue(chunkIndex, out ramData))
                    return CopyFromChunk(ramData, offsetInChunk, buffer);

                diskData = await _cacheManager.ReadChunkAsync(_cacheKey, chunkIndex, ct);
                if (diskData != null)
                {
                    _ramChunks.TryAdd(chunkIndex, diskData);
                    return CopyFromChunk(diskData, offsetInChunk, buffer);
                }
            }
            catch (ChunkDownloadFatalException)
            {
                // Пробрасываем фатальные ошибки — декодер должен узнать
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Debug($"[CachingSource] ReadAt chunk {chunkIndex}: " +
                          $"epoch changed, retry {attempt + 1}/{ReadAtMaxEpochRetries}");

                await Task.Delay(ReadAtEpochRetryDelayMs, ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new IOException(
            $"Failed to load chunk {chunkIndex} after {ReadAtMaxEpochRetries} retries");
    }

    /// <summary>
    /// Копирует данные из чанка в выходной буфер.
    /// </summary>
    private static int CopyFromChunk(byte[] chunkData, int offset, Memory<byte> buffer)
    {
        int available = Math.Min(buffer.Length, chunkData.Length - offset);
        if (available <= 0) return 0;

        chunkData.AsSpan(offset, available).CopyTo(buffer.Span);
        return available;
    }

    #endregion

    #region EnsureChunkAsync

    /// <summary>
    /// Гарантирует доступность чанка (загружает если нужно, ждёт если уже качается).
    /// </summary>
    /// <exception cref="ChunkDownloadFatalException">
    /// Выбрасывается когда circuit breaker открыт или загрузка невозможна.
    /// </exception>
    private async Task EnsureChunkAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || IsChunkAvailable(index))
            return;

        if (_activeDownloads.TryGetValue(index, out var existingTask))
        {
            try { await existingTask.WaitAsync(ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (ChunkDownloadFatalException) { throw; }

            if (IsChunkAvailable(index)) return;
        }

        ct.ThrowIfCancellationRequested();

        // ══════════════════════════════════════════════════════════════
        // CIRCUIT BREAKER CHECK — перед началом любых попыток
        // ══════════════════════════════════════════════════════════════
        int currentFailures = Volatile.Read(ref _consecutive403Count);
        if (currentFailures >= Max403BeforeGiveUp)
        {
            Log.Error($"[CachingSource] Chunk {index}: circuit breaker OPEN " +
                      $"({currentFailures} consecutive 403s), throwing fatal exception");

            throw new ChunkDownloadFatalException(
                $"Stream unavailable: {currentFailures} consecutive HTTP 403 responses",
                chunkIndex: index,
                consecutiveFailures: currentFailures,
                reason: ChunkDownloadFailureReason.Forbidden403,
                trackId: _trackId,
                httpStatusCode: 403);
        }

        // Retry loop для 403 → refresh → retry
        for (int attempt = 0; attempt < Max403BeforeGiveUp; attempt++)
        {
            var downloadTask = DownloadChunkCoreAsync(index, ct);

            if (_activeDownloads.TryAdd(index, downloadTask))
            {
                try { await downloadTask; }
                catch (ChunkDownloadFatalException) { throw; }
                finally { _activeDownloads.TryRemove(index, out _); }
            }
            else
            {
                if (_activeDownloads.TryGetValue(index, out var concurrentTask))
                {
                    try { await concurrentTask.WaitAsync(ct); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (ChunkDownloadFatalException) { throw; }
                }
            }

            // Успех — чанк скачан
            if (IsChunkAvailable(index))
                return;

            // Чанк не скачан — проверяем circuit breaker
            ct.ThrowIfCancellationRequested();

            currentFailures = Volatile.Read(ref _consecutive403Count);
            if (currentFailures >= Max403BeforeGiveUp)
            {
                Log.Error($"[CachingSource] Chunk {index}: circuit breaker triggered " +
                          $"after attempt {attempt + 1}");

                throw new ChunkDownloadFatalException(
                    $"Stream unavailable: circuit breaker open after {currentFailures} failures",
                    chunkIndex: index,
                    consecutiveFailures: currentFailures,
                    reason: ChunkDownloadFailureReason.Forbidden403,
                    trackId: _trackId,
                    httpStatusCode: 403);
            }

            await Task.Delay(PostRefreshRetryDelayMs * (attempt + 1), ct);
        }

        // Все попытки исчерпаны
        throw new ChunkDownloadFatalException(
            $"Failed to download chunk {index} after {Max403BeforeGiveUp} attempts",
            chunkIndex: index,
            consecutiveFailures: Volatile.Read(ref _consecutive403Count),
            reason: ChunkDownloadFailureReason.MaxRetriesExceeded,
            trackId: _trackId);
    }

    #endregion

    #region DownloadChunkCoreAsync

    /// <summary>
    /// Скачивает один чанк по HTTP Range request.
    /// </summary>
    /// <exception cref="ChunkDownloadFatalException">
    /// Выбрасывается при UMP формате или превышении лимита 403.
    /// </exception>
    private async Task DownloadChunkCoreAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || _cacheEntry.IsChunkDownloaded(index))
            return;

        bool gotSlot = false;

        try
        {
            gotSlot = await _downloadSlots.WaitAsync(DownloadSlotTimeoutMs, ct);
            if (!gotSlot) return;

            if (_cacheEntry.IsChunkDownloaded(index)) return;
            ct.ThrowIfCancellationRequested();

            long start = (long)index * ChunkSize;
            long end = Math.Min(start + ChunkSize - 1, _contentLength - 1);

            // Последовательный номер запроса для YouTube
            int rn = Interlocked.Increment(ref _requestSequenceNumber);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DownloadTimeoutMs);

            using var request = CreateChunkRequest(index, start, end, rn);

            Log.Debug($"[CachingSource] Chunk {index}: GET rn={rn}, range={start}-{end}");

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            ct.ThrowIfCancellationRequested();

            // ═══════════════════════════════════════════════════════════
            // 403 HANDLING — координированный refresh с cooldown
            // ═══════════════════════════════════════════════════════════
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                int count = Interlocked.Increment(ref _consecutive403Count);

                if (count > Max403BeforeGiveUp)
                {
                    Log.Error($"[CachingSource] Chunk {index}: {count} consecutive 403s — FATAL");

                    throw new ChunkDownloadFatalException(
                        $"HTTP 403 Forbidden: exceeded {Max403BeforeGiveUp} consecutive failures",
                        chunkIndex: index,
                        consecutiveFailures: count,
                        reason: ChunkDownloadFailureReason.Forbidden403,
                        trackId: _trackId,
                        httpStatusCode: 403);
                }

                Log.Warn($"[CachingSource] 403 for chunk {index} (attempt {count}, rn={rn})");

                // Координированный refresh — только один поток делает запрос
                await CoordinatedRefreshAsync(ct);

                // НЕ retry здесь — EnsureChunkAsync попробует снова
                return;
            }

            // Успешный ответ — сбрасываем счётчик 403
            Interlocked.Exchange(ref _consecutive403Count, 0);

            // ═══════════════════════════════════════════════════════════
            // UMP FORMAT DETECTION — фатальная ошибка
            // ═══════════════════════════════════════════════════════════
            if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
            {
                Log.Error($"[CachingSource] Chunk {index}: received UMP (encrypted) format — FATAL");

                throw new ChunkDownloadFatalException(
                    "YouTube returned encrypted UMP format instead of raw audio. " +
                    "This stream requires different client configuration.",
                    chunkIndex: index,
                    consecutiveFailures: 0,
                    reason: ChunkDownloadFailureReason.UmpFormat,
                    trackId: _trackId);
            }

            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);

            ct.ThrowIfCancellationRequested();

            if (index == 0)
                LogFirstChunkDiagnostics(data);

            _ramChunks.TryAdd(index, data);

            try
            {
                await _cacheManager.WriteChunkAsync(_cacheKey, index, data, CancellationToken.None);
            }
            catch (IOException ex)
            {
                Log.Warn($"[CachingSource] Disk write failed for chunk {index}: {ex.Message}");
            }

            if (_ramChunks.Count > MaxRamChunks)
                EvictDistantRamChunks();
        }
        catch (ChunkDownloadFatalException)
        {
            // Пробрасываем фатальные ошибки
            throw;
        }
        catch (OperationCanceledException)
        {
            // Отмена — не фатальная ошибка
        }
        catch (HttpRequestException) when (ct.IsCancellationRequested)
        {
            // Отмена во время HTTP — не фатальная ошибка
        }
        catch (IOException) when (ct.IsCancellationRequested)
        {
            // Отмена во время IO — не фатальная ошибка
        }
        catch (HttpRequestException ex)
        {
            // Сетевая ошибка — логируем, но не фатальная (retry в EnsureChunkAsync)
            if (!ct.IsCancellationRequested)
                Log.Warn($"[CachingSource] Chunk {index} network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Warn($"[CachingSource] Chunk {index} download failed: {ex.Message}");
        }
        finally
        {
            if (gotSlot)
            {
                try { _downloadSlots.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>
    /// Координированный URL refresh с cooldown и dedup.
    /// Только один поток делает реальный refresh — остальные ждут.
    /// </summary>
    private async Task CoordinatedRefreshAsync(CancellationToken ct)
    {
        var elapsed = DateTime.UtcNow - _lastRefreshTime;
        if (elapsed.TotalMilliseconds < RefreshCooldownMs)
        {
            Log.Debug($"[CachingSource] Refresh cooldown: " +
                      $"{RefreshCooldownMs - (int)elapsed.TotalMilliseconds}ms remaining");

            int waitMs = RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
            if (waitMs > 0)
                await Task.Delay(waitMs, ct);

            return;
        }

        bool acquired = await _refreshLock.WaitAsync(0, ct);
        if (!acquired)
        {
            Log.Debug("[CachingSource] Waiting for concurrent refresh...");
            await _refreshLock.WaitAsync(ct);
            _refreshLock.Release();
            await Task.Delay(PostRefreshRetryDelayMs, ct);
            return;
        }

        try
        {
            elapsed = DateTime.UtcNow - _lastRefreshTime;
            if (elapsed.TotalMilliseconds < RefreshCooldownMs)
            {
                Log.Debug("[CachingSource] Refresh already done by another thread");
                return;
            }

            await RefreshUrlAsync(ct);
            _lastRefreshTime = DateTime.UtcNow;
            await Task.Delay(PostRefreshRetryDelayMs, ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Создаёт HTTP Range request для чанка.
    /// Для YouTube добавляет &amp;rn= (sequential request number).
    /// </summary>
    private HttpRequestMessage CreateChunkRequest(int index, long start, long end, int rn)
    {
        bool isYouTube = _currentUrl.Contains("googlevideo.com/videoplayback");

        if (isYouTube)
        {
            // Используем оптимизированные хелперы из UrlEx
            string url = AppendYouTubeParams(_currentUrl, rn);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            return request;
        }

        var genericRequest = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
        genericRequest.Headers.Range = new RangeHeaderValue(start, end);
        return genericRequest;
    }

    /// <summary>
    /// Добавляет YouTube-specific параметры к URL потока.
    /// Использует оптимизированные хелперы <see cref="UrlEx"/>.
    /// </summary>
    private static string AppendYouTubeParams(string baseUrl, int rn)
    {
        // Удаляем старые параметры через UrlEx (zero-alloc enumeration)
        string url = UrlEx.RemoveQueryParameter(baseUrl, "rn");
        url = UrlEx.RemoveQueryParameter(url, "rbuf");

        // Добавляем новые параметры
        url = UrlEx.SetQueryParameter(url, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");

        return url;
    }

    /// <summary>
    /// Логирует диагностику первого чанка (формат контейнера).
    /// </summary>
    private static void LogFirstChunkDiagnostics(byte[] data)
    {
        if (data.Length < 8) return;

        var hex = string.Join(" ", data.Take(16).Select(b => b.ToString("X2")));
        Log.Debug($"[CachingSource] First chunk bytes: {hex}");

        var ftyp = System.Text.Encoding.ASCII.GetString(data, 4, 4);
        if (ftyp == "ftyp")
            Log.Debug("[CachingSource] ✅ Valid MP4 container");

        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            Log.Debug("[CachingSource] ✅ Valid WebM/EBML container");
    }

    #endregion

    #region Chunk Helpers

    /// <summary>
    /// Проверяет доступность чанка (RAM или диск).
    /// </summary>
    private bool IsChunkAvailable(int index) =>
        _cacheEntry!.IsChunkDownloaded(index) || _ramChunks.ContainsKey(index);

    /// <summary>
    /// Вытесняет из RAM чанки, далёкие от текущей позиции.
    /// </summary>
    private void EvictDistantRamChunks()
    {
        int current = Volatile.Read(ref _currentChunk);

        var toEvict = _ramChunks.Keys
            .Where(i => Math.Abs(i - current) > RamEvictionDistance)
            .OrderByDescending(i => Math.Abs(i - current))
            .Take(MaxRamChunks / 4)
            .ToList();

        foreach (int idx in toEvict)
            _ramChunks.TryRemove(idx, out _);
    }

    #endregion
}