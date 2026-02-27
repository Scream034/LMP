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

            if (IsChunkAvailable(index)) return;
        }

        ct.ThrowIfCancellationRequested();
        CheckCircuitBreaker(index);

        // Retry loop: network errors + 403 refresh
        int maxAttempts = _config.MaxNetworkRetries + _config.Max403BeforeCircuitBreak;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var downloadTask = DownloadChunkCoreAsync(index, ct);

            if (_activeDownloads.TryAdd(index, downloadTask))
            {
                ChunkDownloadResult result;
                try { result = await downloadTask; }
                catch (ChunkDownloadFatalException) { throw; }
                finally { _activeDownloads.TryRemove(index, out _); }

                switch (result)
                {
                    case ChunkDownloadResult.Success:
                        return;

                    case ChunkDownloadResult.Forbidden403:
                        // Refresh URL и повторить
                        await CoordinatedRefreshAsync(ct);
                        continue;

                    case ChunkDownloadResult.NetworkError:
                        // Exponential backoff
                        int delay = ComputeRetryDelay(attempt);
                        Log.Warn($"[CachingSource] Chunk {index}: network retry " +
                                 $"{attempt + 1}/{maxAttempts}, delay={delay}ms");
                        await Task.Delay(delay, ct);
                        continue;

                    case ChunkDownloadResult.SlotTimeout:
                        // Короткая пауза и повтор
                        await Task.Delay(100, ct);
                        continue;

                    case ChunkDownloadResult.Cancelled:
                        ct.ThrowIfCancellationRequested();
                        return;

                    case ChunkDownloadResult.Fatal:
                        // Уже выброшено из DownloadChunkCoreAsync
                        return;
                }
            }
            else
            {
                // Другой поток уже качает — ждём его
                if (_activeDownloads.TryGetValue(index, out var concurrentTask))
                {
                    try { await concurrentTask.WaitAsync(ct); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (ChunkDownloadFatalException) { throw; }
                }

                if (IsChunkAvailable(index)) return;
            }
        }

        // Все попытки исчерпаны
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
            // Здесь оставляем 'ct', чтобы мгновенно отменить ожидание свободного слота
            gotSlot = await _downloadSlots.WaitAsync(_config.DownloadSlotTimeoutMs, ct);
            if (!gotSlot)
                return ChunkDownloadResult.SlotTimeout;

            if (_cacheEntry.IsChunkDownloaded(index))
                return ChunkDownloadResult.Success;

            ct.ThrowIfCancellationRequested();

            long start = (long)index * _chunkSize;
            long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);
            int rn = Interlocked.Increment(ref _requestSequenceNumber);

            // ═══════════════════════════════════════════════════════════════
            // ИЗМЕНЕНИЕ ЗДЕСЬ: Независимый токен для HTTP запроса.
            // ═══════════════════════════════════════════════════════════════
            using var httpCts = new CancellationTokenSource(_config.DownloadTimeoutMs);

            // Если сработал 'ct' (пользователь сделал Seek), даем HTTP клиенту 
            // 50мс на мягкое закрытие TLS соединения, чтобы не крашнуть пул потоков.
            using var reg = ct.Register(() =>
            {
                try { httpCts.CancelAfter(50); } catch { }
            });

            using var request = CreateChunkRequest(index, start, end, rn);

            Log.Debug($"[CachingSource] Chunk {index}: GET rn={rn}, range={start}-{end}");

            HttpResponseMessage? response = null;
            try
            {
                // ИСПОЛЬЗУЕМ httpCts.Token вместо timeoutCts.Token
                response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, httpCts.Token);
            }
            catch (HttpRequestException ex) when (IsSslRelated(ex))
            {
                Log.Warn($"[CachingSource] Chunk {index} SSL error on send: {ex.Message}");
                return ct.IsCancellationRequested
                    ? ChunkDownloadResult.Cancelled
                    : ChunkDownloadResult.NetworkError;
            }
            catch (IOException ex) when (IsSslRelated(ex))
            {
                Log.Warn($"[CachingSource] Chunk {index} SSL/IO error on send: {ex.Message}");
                return ct.IsCancellationRequested
                    ? ChunkDownloadResult.Cancelled
                    : ChunkDownloadResult.NetworkError;
            }

            ct.ThrowIfCancellationRequested();

            using (response)
            {
                // ── 403 Handling ──
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    int count = Interlocked.Increment(ref _consecutive403Count);
                    Log.Warn($"[CachingSource] 403 for chunk {index} " +
                             $"(consecutive={count}, rn={rn})");
                    return ChunkDownloadResult.Forbidden403;
                }

                Interlocked.Exchange(ref _consecutive403Count, 0);

                // ── UMP detection ──
                if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
                {
                    Log.Error($"[CachingSource] Chunk {index}: UMP format — FATAL");
                    throw new ChunkDownloadFatalException(
                        "YouTube returned encrypted UMP format",
                        chunkIndex: index,
                        consecutiveFailures: 0,
                        reason: ChunkDownloadFailureReason.UmpFormat,
                        trackId: _trackId);
                }

                response.EnsureSuccessStatusCode();

                byte[] data;
                try
                {
                    // ИСПОЛЬЗУЕМ httpCts.Token
                    data = await response.Content.ReadAsByteArrayAsync(httpCts.Token);
                }
                catch (HttpRequestException ex) when (IsSslRelated(ex))
                {
                    Log.Warn($"[CachingSource] Chunk {index} SSL error on read: {ex.Message}");
                    return ct.IsCancellationRequested
                        ? ChunkDownloadResult.Cancelled
                        : ChunkDownloadResult.NetworkError;
                }
                catch (IOException ex) when (IsSslRelated(ex))
                {
                    Log.Warn($"[CachingSource] Chunk {index} SSL/IO error on read: {ex.Message}");
                    return ct.IsCancellationRequested
                        ? ChunkDownloadResult.Cancelled
                        : ChunkDownloadResult.NetworkError;
                }

                ct.ThrowIfCancellationRequested();

                if (index == 0)
                    LogFirstChunkDiagnostics(data);

                _ramChunks.TryAdd(index, data);

                try
                {
                    await _cacheManager.WriteChunkAsync(
                        _cacheKey, index, data, CancellationToken.None);
                }
                catch (IOException ex)
                {
                    Log.Warn($"[CachingSource] Disk write failed for chunk {index}: {ex.Message}");
                }

                if (_ramChunks.Count > _config.MaxRamChunks)
                    EvictDistantRamChunks();

                return ChunkDownloadResult.Success;
            }
        }
        catch (ChunkDownloadFatalException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ChunkDownloadResult.Cancelled;
        }
        catch (HttpRequestException ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Warn($"[CachingSource] Chunk {index} network error: {ex.Message}");
            return ct.IsCancellationRequested
                ? ChunkDownloadResult.Cancelled
                : ChunkDownloadResult.NetworkError;
        }
        catch (IOException ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Warn($"[CachingSource] Chunk {index} IO error: {ex.Message}");
            return ct.IsCancellationRequested
                ? ChunkDownloadResult.Cancelled
                : ChunkDownloadResult.NetworkError;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Warn($"[CachingSource] Chunk {index} unexpected: {ex.Message}");
            return ct.IsCancellationRequested
                ? ChunkDownloadResult.Cancelled
                : ChunkDownloadResult.NetworkError;
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
    /// Проверяет, связана ли ошибка с SSL/TLS.
    /// </summary>
    private static bool IsSslRelated(Exception ex)
    {
        // Проверяем всю цепочку inner exceptions
        var current = ex;
        while (current != null)
        {
            string msg = current.Message;
            string typeName = current.GetType().Name;

            if (typeName.Contains("Ssl", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Tls", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
                return true;

            if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("secure channel", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("decryption", StringComparison.OrdinalIgnoreCase))
                return true;

            current = current.InnerException;
        }

        return false;
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

    private static void LogFirstChunkDiagnostics(byte[] data)
    {
        if (data.Length < 8) return;

        var hex = string.Join(" ", data.Take(16).Select(b => b.ToString("X2")));
        Log.Debug($"[CachingSource] First chunk bytes: {hex}");

        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            Log.Debug("[CachingSource] ✅ Valid WebM/EBML container");

        if (data.Length >= 8)
        {
            var ftyp = System.Text.Encoding.ASCII.GetString(data, 4, 4);
            if (ftyp == "ftyp")
                Log.Debug("[CachingSource] ✅ Valid MP4 container");
        }
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