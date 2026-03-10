using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Memory;
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
        SlotTimeout,

        /// <summary>Чанк за пределами файла — не ошибка, просто нечего качать.</summary>
        OutOfRange
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

        if (chunkIndex >= _totalChunks)
            return 0;

        // Быстрый путь: RAM
        if (_ramChunks.TryGetValue(chunkIndex, out var ramEntry))
            return CopyFromChunk(ramEntry.Data, ramEntry.Length, offsetInChunk, buffer);

        // Средний путь: диск
        var diskData = await _cacheManager.ReadChunkAsync(_cacheKey, chunkIndex, ct);
        if (diskData != null)
        {
            _ramChunks.TryAdd(chunkIndex, new ChunkData(diskData, diskData.Length));
            return CopyFromChunk(diskData, diskData.Length, offsetInChunk, buffer);
        }

        // Медленный путь: загрузка из сети
        for (int attempt = 0; attempt < ReadAtMaxEpochRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct, downloadToken);

                var result = await EnsureChunkAsync(chunkIndex, linkedCts.Token);

                if (result == ChunkDownloadResult.OutOfRange)
                    return 0;

                if (_ramChunks.TryGetValue(chunkIndex, out ramEntry))
                    return CopyFromChunk(ramEntry.Data, ramEntry.Length, offsetInChunk, buffer);

                diskData = await _cacheManager.ReadChunkAsync(_cacheKey, chunkIndex, ct);
                if (diskData != null)
                {
                    _ramChunks.TryAdd(chunkIndex, new ChunkData(diskData, diskData.Length));
                    return CopyFromChunk(diskData, diskData.Length, offsetInChunk, buffer);
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

    /// <summary>
    /// Копирует данные из чанка в целевой буфер с учётом смещения.
    /// </summary>
    private static int CopyFromChunk(byte[] chunkData, int chunkLength, int offset, Memory<byte> buffer)
    {
        int available = Math.Min(buffer.Length, chunkLength - offset);
        if (available <= 0) return 0;
        chunkData.AsSpan(offset, available).CopyTo(buffer.Span);
        return available;
    }

    #endregion

    #region EnsureChunkAsync

    /// <summary>
    /// Гарантирует доступность чанка. Загружает если нужно, с retry и circuit breaker.
    /// Возвращает результат операции вместо броска исключений при штатных ситуациях.
    /// </summary>
    private async Task<ChunkDownloadResult> EnsureChunkAsync(int index, CancellationToken ct)
    {
        // Валидация индекса чанка — за пределами файла не качаем
        if (index < 0 || index >= _totalChunks)
            return ChunkDownloadResult.OutOfRange;

        // Проверяем, есть ли данные для этого чанка (start >= contentLength)
        long chunkStart = (long)index * _chunkSize;
        if (chunkStart >= _contentLength)
            return ChunkDownloadResult.OutOfRange;

        if (_cacheEntry == null || IsChunkAvailable(index))
            return ChunkDownloadResult.Success;

        // Ждём если кто-то уже качает этот чанк
        if (_activeDownloads.TryGetValue(index, out var existingTask))
        {
            try
            {
                await existingTask.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (ChunkDownloadFatalException) { throw; }
            catch { }

            if (IsChunkAvailable(index))
                return ChunkDownloadResult.Success;
        }

        ct.ThrowIfCancellationRequested();
        CheckCircuitBreaker(index);

        int maxAttempts = _config.MaxNetworkRetries + _config.Max403BeforeCircuitBreak;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Перепроверяем — мог загрузиться пока ждали
            if (IsChunkAvailable(index))
                return ChunkDownloadResult.Success;

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
                    case ChunkDownloadResult.OutOfRange:
                        return result;

                    case ChunkDownloadResult.Forbidden403:
                        await CoordinatedRefreshAsync(ct);
                        continue;

                    case ChunkDownloadResult.NetworkError:
                        int delay = attempt == 0 ? 50 : ComputeRetryDelay(attempt);
                        Log.Warn($"[CachingSource] Chunk {index}: network retry " +
                                 $"{attempt + 1}/{maxAttempts}, delay={delay}ms");
                        await Task.Delay(delay, ct);
                        continue;

                    case ChunkDownloadResult.SlotTimeout:
                        await Task.Delay(200, ct);
                        continue;

                    case ChunkDownloadResult.Cancelled:
                        if (!ct.IsCancellationRequested)
                        {
                            Log.Debug($"[CachingSource] Chunk {index}: cancelled (epoch change), retry");
                            await Task.Delay(50, ct);
                            continue;
                        }
                        ct.ThrowIfCancellationRequested();
                        return result;

                    case ChunkDownloadResult.Fatal:
                        return result;
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

                if (IsChunkAvailable(index))
                    return ChunkDownloadResult.Success;
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
    /// Скачивает один чанк. Вычисляет и валидирует HTTP Range перед запросом.
    /// Возвращает результат вместо глотания ошибок.
    /// </summary>
    private async Task<ChunkDownloadResult> DownloadChunkCoreAsync(int index, CancellationToken ct)
    {
        if (_cacheEntry == null || _cacheEntry.IsChunkDownloaded(index))
            return ChunkDownloadResult.Success;

        // Вычисляем и валидируем range
        long start = (long)index * _chunkSize;
        long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);

        // Невалидный range — чанк полностью за пределами файла
        if (start >= _contentLength || start > end)
        {
            Log.Debug($"[CachingSource] Chunk {index} out of range: " +
                      $"start={start}, contentLength={_contentLength}");
            return ChunkDownloadResult.OutOfRange;
        }

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

            try { _downloadSlots.Release(); } catch { }
            gotSlot = false;

            return await DownloadChunkHttpAsync(index, start, end, ct);
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
    /// HTTP загрузка чанка с предвычисленным и валидным range.
    /// Использует ChunkPool для избежания LOH аллокаций.
    /// ВСЕГДА await'ит до конца — никогда не бросает HTTP операцию "в полёте".
    /// </summary>
    private async Task<ChunkDownloadResult> DownloadChunkHttpAsync(
        int index, long start, long end, CancellationToken ct)
    {
        int rn = Interlocked.Increment(ref _requestSequenceNumber);
        int expectedBytes = (int)(end - start + 1);

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
            return ChunkDownloadResult.NetworkError;
        }
        catch (HttpRequestException)
        {
            return ChunkDownloadResult.NetworkError;
        }

        using (response)
        {
            if (ct.IsCancellationRequested)
                return ChunkDownloadResult.Cancelled;

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                int count = Interlocked.Increment(ref _consecutive403Count);

                // ═══ EXTENDED 403 DIAGNOSTIC ═══
                var nParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
                var cParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "c");
                var reqUa = request.Headers.UserAgent.ToString();

                Log.Warn($"[CachingSource] ═══ 403 DIAGNOSTIC chunk {index} (consecutive={count}) ═══");
                Log.Warn($"[CachingSource]   c={cParam ?? "?"}, UA={reqUa[..Math.Min(reqUa.Length, 50)]}...");
                Log.Warn($"[CachingSource]   n-token: {nParam ?? "MISSING"} " +
                         $"(len={nParam?.Length ?? 0}, looks_encrypted={nParam?.Length is > 15 and < 25})");

                // Проверяем заголовки ответа для доп. информации
                if (response.Headers.TryGetValues("X-Restrict-Formats-Hint", out var hints))
                    Log.Warn($"[CachingSource]   Restrict-Hint: {string.Join(", ", hints)}");

                var responseBody = "";
                try { responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None); }
                catch { /* ignore */ }
                if (responseBody.Length > 0)
                    Log.Warn($"[CachingSource]   Response body: {responseBody[..Math.Min(responseBody.Length, 200)]}");

                Log.Warn($"[CachingSource] ═══════════════════════════════════════════════════════");

                return ChunkDownloadResult.Forbidden403;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Log.Debug($"[CachingSource] Chunk {index}: 416 Range Not Satisfiable (EOF)");
                return ChunkDownloadResult.OutOfRange;
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

            // ═══ READ — через pooled buffer для избежания LOH ═══
            byte[] rentedBuffer = ChunkPool.Shared.Rent(expectedBytes);
            int actualLength;

            try
            {
                using var contentStream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
                actualLength = await ReadStreamFullyAsync(contentStream, rentedBuffer, expectedBytes);
            }
            catch (Exception ex)
            {
                ChunkPool.Shared.Return(rentedBuffer);
                Log.Warn($"[CachingSource] Chunk {index} read: {ex.Message}");
                return ChunkDownloadResult.NetworkError;
            }

            if (ct.IsCancellationRequested)
            {
                ChunkPool.Shared.Return(rentedBuffer);
                return ChunkDownloadResult.Cancelled;
            }

            // ═══ VALIDATE ═══
            if (actualLength == 0)
            {
                ChunkPool.Shared.Return(rentedBuffer);
                Log.Warn($"[CachingSource] Chunk {index}: empty response");
                return ChunkDownloadResult.NetworkError;
            }

            if (actualLength < expectedBytes)
            {
                if (start + actualLength >= _contentLength - _chunkSize)
                {
                    Log.Debug($"[CachingSource] Chunk {index}: partial read " +
                              $"{actualLength}/{expectedBytes} (near EOF)");
                }
                else
                {
                    ChunkPool.Shared.Return(rentedBuffer);
                    Log.Warn($"[CachingSource] Chunk {index} incomplete: " +
                             $"{actualLength}/{expectedBytes}");
                    return ChunkDownloadResult.NetworkError;
                }
            }

            // ═══ SAVE ═══
            byte[] chunkData;
            if (rentedBuffer.Length == actualLength)
            {
                chunkData = rentedBuffer;
            }
            else
            {
                chunkData = new byte[actualLength];
                Buffer.BlockCopy(rentedBuffer, 0, chunkData, 0, actualLength);
                ChunkPool.Shared.Return(rentedBuffer);
            }

            _ramChunks.TryAdd(index, new ChunkData(chunkData, actualLength));

            _ = WriteToDiskFireAndForgetAsync(index, chunkData);

            if (_ramChunks.Count > _config.MaxRamChunks)
                EvictDistantRamChunks();

            return ChunkDownloadResult.Success;
        }
    }

    /// <summary>
    /// Полностью читает данные из потока в буфер (с учётом partial reads от TCP).
    /// </summary>
    private static async ValueTask<int> ReadStreamFullyAsync(
        Stream stream, byte[] buffer, int maxBytes)
    {
        int totalRead = 0;

        while (totalRead < maxBytes)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, maxBytes - totalRead));

            if (read == 0)
                break; // EOF

            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// Fire-and-forget запись чанка на диск.
    /// Ошибки логируются, но не прерывают воспроизведение.
    /// </summary>
    private async Task WriteToDiskFireAndForgetAsync(int index, byte[] data)
    {
        try
        {
            await _cacheManager.WriteChunkAsync(_cacheKey, index, data, CancellationToken.None);
        }
        catch (IOException ex)
        {
            Log.Warn($"[CachingSource] Disk write chunk {index}: {ex.Message}");
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
            Log.Debug("[CachingSource] Waiting for concurrent refresh...");
            await _refreshLock.WaitAsync(ct);
            _refreshLock.Release();
            await Task.Delay(_config.PostRefreshDelayMs, ct);
            return;
        }

        try
        {
            var elapsed = DateTime.UtcNow - _lastRefreshTime;
            if (elapsed.TotalMilliseconds < _config.RefreshCooldownMs)
            {
                int waitMs = _config.RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
                Log.Debug($"[CachingSource] Refresh cooldown: {waitMs}ms");
                await Task.Delay(waitMs, ct);
            }

            await RefreshUrlAsync(ct);
            _lastRefreshTime = DateTime.UtcNow;

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
            string url = BuildYouTubeChunkUrl(_currentUrl, rn);

            // ═══ DIAGNOSTIC: URL + ключевые параметры для верификации ═══
            var nParam = UrlEx.TryGetQueryParameterValue(url, "n");
            var cParam = UrlEx.TryGetQueryParameterValue(url, "c");
            var sigParam = UrlEx.TryGetQueryParameterValue(url, "sig");

            Log.Debug($"[CachingSource] Chunk {index} URL: {url[..Math.Min(url.Length, 300)]}");
            Log.Debug($"[CachingSource] Chunk {index} params: " +
                      $"c={cParam ?? "MISSING"}, " +
                      $"n={nParam?[..Math.Min(nParam.Length, 15)] ?? "MISSING"}..., " +
                      $"sig={(sigParam is not null ? $"{sigParam.Length}chars,ends=...{sigParam[^Math.Min(6, sigParam.Length)..]}]" : "MISSING")}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);

            // ═══ FIX: User-Agent соответствует клиенту из URL ═══
            // URL содержит c=WEB_REMIX → нужен Chrome UA, НЕ Android VR!
            SharedHttpClient.ApplyUserAgentFromUrl(request, url);

            // ═══ DIAGNOSTIC: подтверждаем какой UA используется ═══
            var appliedUa = request.Headers.UserAgent.ToString();
            Log.Debug($"[CachingSource] Chunk {index} UA: {appliedUa[..Math.Min(appliedUa.Length, 60)]}...");

            return request;
        }

        var genericRequest = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
        genericRequest.Headers.Range = new RangeHeaderValue(start, end);
        genericRequest.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);
        return genericRequest;
    }

    /// <summary>
    /// Строит URL для chunk-запроса к YouTube.
    /// SRP: chunk-специфичные параметры (rn, rbuf) добавляются ЗДЕСЬ,
    /// а не в StreamClient. StreamClient готовит "чистый" base URL.
    /// 
    /// Использует UrlEx.SetQueryParameter для in-place замены —
    /// если rn/rbuf уже есть, они обновятся на месте, если нет — добавятся в конец.
    /// Порядок остальных параметров (n, sig, sparams и т.д.) гарантированно сохраняется.
    /// </summary>
    private static string BuildYouTubeChunkUrl(string baseUrl, int rn)
    {
        // SetQueryParameter делает in-place замену если параметр существует,
        // или append если нет. Порядок остальных параметров не меняется.
        string url = UrlEx.SetQueryParameter(baseUrl, "rn", rn.ToString());
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