using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Максимум попыток в <see cref="ReadAtAsync"/> при смене эпохи.
    /// </summary>
    private const int ReadAtMaxEpochRetries = 5;
    private const int ReadAtEpochRetryDelayMs = 50;

    /// <summary>
    /// Координация URL refresh: только один refresh одновременно.
    /// </summary>
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Timestamp последнего refresh.</summary>
    private DateTime _lastRefreshTime = DateTime.MinValue;

    /// <summary>Счётчик последовательных 403.</summary>
    private int _consecutive403Count;

    /// <summary>Последовательный номер запроса к YouTube (&amp;rn=).</summary>
    private int _requestSequenceNumber;

    #region Chunk Download Result

    /// <summary>
    /// Результат попытки загрузки чанка.
    /// </summary>
    private enum ChunkDownloadResult
    {
        /// <summary>Чанк успешно загружен.</summary>
        Success,

        /// <summary>HTTP 403 — нужен URL refresh.</summary>
        Forbidden403,

        /// <summary>Сетевая ошибка (IOException, HttpRequestException, timeout).</summary>
        NetworkError,

        /// <summary>Фатальная ошибка (UMP format).</summary>
        Fatal,

        /// <summary>Операция отменена (epoch change или dispose).</summary>
        Cancelled,

        /// <summary>Не удалось получить слот загрузки (все заняты).</summary>
        SlotTimeout
    }

    #endregion

    #region ReadAtAsync

    /// <summary>
    /// Читает данные из указанной позиции, загружая чанки по необходимости.
    /// </summary>
    internal async Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        if (position >= _contentLength)
            return 0;

        int chunkIndex = (int)(position / _chunkSize);
        int offsetInChunk = (int)(position % _chunkSize);

        // Быстрый путь: RAM
        if (_ramChunks.TryGetValue(chunkIndex, out var ramData))
            return CopyFromChunk(ramData, offsetInChunk, buffer);

        // Средний путь: диск
        var diskData = await _cacheManager.ReadChunkAsync(_cacheKey, chunkIndex, ct);
        if (diskData != null)
        {
            _ramChunks.TryAdd(chunkIndex, diskData);
            return CopyFromChunk(diskData, offsetInChunk, buffer);
        }

        // Медленный путь: загрузка
        for (int attempt = 0; attempt < ReadAtMaxEpochRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct, downloadToken);

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
    /// Гарантирует доступность чанка. Загружает если нужно, с retry и circuit breaker.
    /// </summary>
    private async Task EnsureChunkAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || IsChunkAvailable(index))
            return;

        // Ждём если кто-то уже качает этот чанк
        if (_activeDownloads.TryGetValue(index, out var existingTask))
        {
            try { await existingTask.WaitAsync(ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (ChunkDownloadFatalException) { throw; }
            catch { }

            if (IsChunkAvailable(index)) return;
        }

        ct.ThrowIfCancellationRequested();
        CheckCircuitBreaker(index);

        int maxAttempts = _config.MaxNetworkRetries + _config.Max403BeforeCircuitBreak;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Перепроверяем — мог загрузиться пока ждали
            if (IsChunkAvailable(index)) return;

            var downloadTask = DownloadChunkCoreAsync(index, ct);

            if (_activeDownloads.TryAdd(index, downloadTask))
            {
                ChunkDownloadResult result;
                try
                {
                    result = await downloadTask;
                }
                catch (ChunkDownloadFatalException) { throw; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Epoch change — можно retry
                    _activeDownloads.TryRemove(index, out _);
                    await Task.Delay(50, ct);
                    continue;
                }
                finally
                {
                    _activeDownloads.TryRemove(index, out _);
                }

                switch (result)
                {
                    case ChunkDownloadResult.Success:
                        return;

                    case ChunkDownloadResult.Forbidden403:
                        await CoordinatedRefreshAsync(ct);
                        continue;

                    case ChunkDownloadResult.NetworkError:
                        int delay = ComputeRetryDelay(attempt);
                        Log.Warn($"[CachingSource] Chunk {index}: network retry " +
                                 $"{attempt + 1}/{maxAttempts}, delay={delay}ms");
                        await Task.Delay(delay, ct);
                        continue;

                    case ChunkDownloadResult.SlotTimeout:
                        // Слоты заняты — короткая пауза
                        await Task.Delay(200, ct);
                        continue;

                    case ChunkDownloadResult.Cancelled:
                        // Отмена НЕ по нашему ct — epoch change
                        if (!ct.IsCancellationRequested)
                        {
                            Log.Debug($"[CachingSource] Chunk {index}: cancelled (epoch change), retry");
                            await Task.Delay(50, ct);
                            continue;
                        }
                        // Отмена по нашему ct — выходим
                        ct.ThrowIfCancellationRequested();
                        return;

                    case ChunkDownloadResult.Fatal:
                        return;
                }
            }
            else
            {
                // Другой поток уже качает — ждём его результат
                if (_activeDownloads.TryGetValue(index, out var concurrentTask))
                {
                    try { await concurrentTask.WaitAsync(ct); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (ChunkDownloadFatalException) { throw; }
                    catch { }
                }

                if (IsChunkAvailable(index)) return;
                // Не загрузился — retry в следующей итерации
            }
        }

        throw new ChunkDownloadFatalException(
            $"Failed to download chunk {index} after {maxAttempts} attempts",
            chunkIndex: index,
            consecutiveFailures: Volatile.Read(ref _consecutive403Count),
            reason: ChunkDownloadFailureReason.MaxRetriesExceeded,
            trackId: _trackId);
    }

    /// <summary>
    /// Проверяет circuit breaker перед загрузкой.
    /// </summary>
    private void CheckCircuitBreaker(int index)
    {
        int failures = Volatile.Read(ref _consecutive403Count);
        if (failures >= _config.Max403BeforeCircuitBreak)
        {
            throw new ChunkDownloadFatalException(
                $"Circuit breaker OPEN: {failures} consecutive 403s",
                chunkIndex: index,
                consecutiveFailures: failures,
                reason: ChunkDownloadFailureReason.Forbidden403,
                trackId: _trackId,
                httpStatusCode: 403);
        }
    }

    /// <summary>
    /// Вычисляет задержку retry с учётом exponential backoff.
    /// </summary>
    private int ComputeRetryDelay(int attempt)
    {
        int baseDelay = _config.NetworkRetryBaseDelayMs;

        if (_config.UseExponentialBackoff)
            return Math.Min(baseDelay * (1 << attempt), 10_000); // max 10s

        return baseDelay;
    }

    #endregion

    #region DownloadChunkCoreAsync

    /// <summary>
    /// Скачивает один чанк. Возвращает результат вместо глотания ошибок.
    /// </summary>
    private async Task<ChunkDownloadResult> DownloadChunkCoreAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || _cacheEntry.IsChunkDownloaded(index))
            return ChunkDownloadResult.Success;

        bool gotSlot = false;

        try
        {
            gotSlot = await _downloadSlots.WaitAsync(_config.DownloadSlotTimeoutMs, ct);
            if (!gotSlot)
                return ChunkDownloadResult.SlotTimeout;

            if (_cacheEntry.IsChunkDownloaded(index) || ct.IsCancellationRequested)
            {
                return _cacheEntry.IsChunkDownloaded(index)
                    ? ChunkDownloadResult.Success
                    : ChunkDownloadResult.Cancelled;
            }

            // Освобождаем slot СРАЗУ — HTTP запрос будет жить своей жизнью
            // Slot нужен только для throttling количества НОВЫХ запросов
            try { _downloadSlots.Release(); } catch { }
            gotSlot = false;

            return await DownloadChunkHttpAsync(index, ct);
        }
        catch (ChunkDownloadFatalException) { throw; }
        catch (OperationCanceledException) { return ChunkDownloadResult.Cancelled; }
        catch (Exception) when (ct.IsCancellationRequested || _disposed)
        {
            return ChunkDownloadResult.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Chunk {index} unexpected: {ex.Message}");
            return ChunkDownloadResult.NetworkError;
        }
        finally
        {
            if (gotSlot)
            {
                try { _downloadSlots.Release(); } catch { }
            }
        }
    }

    /// <summary>
    /// HTTP загрузка чанка. ВСЕГДА await'ит до конца — никогда не бросает
    /// HTTP операцию "в полёте". При отмене ct — просто отбрасывает результат.
    /// </summary>
    private async Task<ChunkDownloadResult> DownloadChunkHttpAsync(int index, CancellationToken ct)
    {
        long start = (long)index * _chunkSize;
        long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);
        int rn = Interlocked.Increment(ref _requestSequenceNumber);

        Log.Debug($"[CachingSource] Chunk {index}: GET rn={rn}, range={start}-{end}");

        using var request = CreateChunkRequest(index, start, end, rn);

        // ═══ SEND — всегда ждём до конца ═══
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        }
        catch (TaskCanceledException)
        {
            // HttpClient.Timeout
            return ChunkDownloadResult.NetworkError;
        }
        catch (HttpRequestException)
        {
            return ChunkDownloadResult.NetworkError;
        }

        using (response)
        {
            // Проверяем отмену ПОСЛЕ завершения SendAsync
            if (ct.IsCancellationRequested)
                return ChunkDownloadResult.Cancelled;

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                int count = Interlocked.Increment(ref _consecutive403Count);
                Log.Warn($"[CachingSource] 403 for chunk {index} (consecutive={count})");
                return ChunkDownloadResult.Forbidden403;
            }

            Interlocked.Exchange(ref _consecutive403Count, 0);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
            {
                throw new ChunkDownloadFatalException(
                    "YouTube returned encrypted UMP format",
                    chunkIndex: index, consecutiveFailures: 0,
                    reason: ChunkDownloadFailureReason.UmpFormat,
                    trackId: _trackId);
            }

            response.EnsureSuccessStatusCode();

            // ═══ READ — всегда ждём до конца ═══
            byte[] chunkData;
            try
            {
                chunkData = await response.Content.ReadAsByteArrayAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warn($"[CachingSource] Chunk {index} read: {ex.Message}");
                return ChunkDownloadResult.NetworkError;
            }

            // Проверяем отмену ПОСЛЕ чтения
            if (ct.IsCancellationRequested)
                return ChunkDownloadResult.Cancelled;

            // ═══ VALIDATE ═══
            int totalBytesToRead = (int)(end - start + 1);

            if (chunkData.Length < totalBytesToRead)
            {
                Log.Warn($"[CachingSource] Chunk {index} incomplete: " +
                         $"{chunkData.Length}/{totalBytesToRead}");
                return ChunkDownloadResult.NetworkError;
            }

            if (chunkData.Length > totalBytesToRead)
            {
                var trimmed = new byte[totalBytesToRead];
                Array.Copy(chunkData, trimmed, totalBytesToRead);
                chunkData = trimmed;
            }

            // ═══ SAVE ═══
            _ramChunks.TryAdd(index, chunkData);

            try
            {
                await _cacheManager.WriteChunkAsync(
                    _cacheKey, index, chunkData, CancellationToken.None);
            }
            catch (IOException ex)
            {
                Log.Warn($"[CachingSource] Disk write chunk {index}: {ex.Message}");
            }

            if (_ramChunks.Count > _config.MaxRamChunks)
                EvictDistantRamChunks();

            return ChunkDownloadResult.Success;
        }
    }

    #endregion

    #region URL Refresh

    /// <summary>
    /// Координированный URL refresh. Один поток делает refresh, остальные ждут.
    /// После успешного refresh сбрасывает счётчик 403.
    /// </summary>
    private async Task CoordinatedRefreshAsync(CancellationToken ct)
    {
        bool acquired = await _refreshLock.WaitAsync(0, ct);
        if (!acquired)
        {
            // Кто-то уже делает refresh — ждём
            Log.Debug("[CachingSource] Waiting for concurrent refresh...");
            await _refreshLock.WaitAsync(ct);
            _refreshLock.Release();
            await Task.Delay(_config.PostRefreshDelayMs, ct);
            return;
        }

        try
        {
            // Cooldown check
            var elapsed = DateTime.UtcNow - _lastRefreshTime;
            if (elapsed.TotalMilliseconds < _config.RefreshCooldownMs)
            {
                int waitMs = _config.RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
                Log.Debug($"[CachingSource] Refresh cooldown: {waitMs}ms");
                await Task.Delay(waitMs, ct);
            }

            // Выполняем refresh
            await RefreshUrlAsync(ct);
            _lastRefreshTime = DateTime.UtcNow;

            // Сбрасываем 403 счётчик после успешного refresh
            Interlocked.Exchange(ref _consecutive403Count, 0);
            Log.Info("[CachingSource] 403 counter reset after URL refresh");

            await Task.Delay(_config.PostRefreshDelayMs, ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    #endregion

    #region HTTP Request Building

    private HttpRequestMessage CreateChunkRequest(int index, long start, long end, int rn)
    {
        bool isYouTube = _currentUrl.Contains("googlevideo.com/videoplayback");

        if (isYouTube)
        {
            string url = AppendYouTubeParams(_currentUrl, rn);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            return request;
        }

        var genericRequest = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
        genericRequest.Headers.Range = new RangeHeaderValue(start, end);
        return genericRequest;
    }

    private static string AppendYouTubeParams(string baseUrl, int rn)
    {
        string url = UrlEx.RemoveQueryParameter(baseUrl, "rn");
        url = UrlEx.RemoveQueryParameter(url, "rbuf");
        url = UrlEx.SetQueryParameter(url, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");
        return url;
    }

    #endregion

    #region Chunk Helpers

    private bool IsChunkAvailable(int index) =>
        _cacheEntry!.IsChunkDownloaded(index) || _ramChunks.ContainsKey(index);

    private void EvictDistantRamChunks()
    {
        int current = Volatile.Read(ref _currentChunk);

        var toEvict = _ramChunks.Keys
            .Where(i => Math.Abs(i - current) > _config.RamEvictionDistance)
            .OrderByDescending(i => Math.Abs(i - current))
            .Take(_config.MaxRamChunks / 4)
            .ToList();

        foreach (int idx in toEvict)
            _ramChunks.TryRemove(idx, out _);
    }

    #endregion
}