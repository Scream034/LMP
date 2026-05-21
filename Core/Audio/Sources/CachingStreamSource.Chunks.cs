using System.Buffers;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Nested Types

    /// <summary>
    /// Данные одного загруженного чанка с явной моделью владения буфером.
    /// </summary>
    /// <remarks>
    /// Инкапсулирует <see cref="IMemoryOwner{T}"/>, полученный из <see cref="MemoryPool{T}.Shared"/>.
    /// Вызов <see cref="Dispose"/> возвращает буфер в пул. Владелец объекта —
    /// <c>_ramChunks</c>; при эвикции вызывается <see cref="Dispose"/>.
    /// </remarks>
    internal sealed class ChunkData : IDisposable
    {
        private IMemoryOwner<byte>? _owner;
        private volatile bool _disposed;

        /// <summary>Слайс памяти, ограниченный реальной длиной данных чанка.</summary>
        public Memory<byte> Memory { get; }

        /// <summary>Реальная длина данных (может быть меньше размера арендованного буфера).</summary>
        public int Length { get; }

        /// <summary>
        /// Создаёт обёртку над арендованным буфером, принимая владение.
        /// </summary>
        /// <param name="owner">
        /// Владение передаётся в этот объект — вызывающий НЕ должен вызывать Dispose на owner после этого.
        /// </param>
        /// <param name="actualLength">Реальное количество байт данных в буфере.</param>
        public ChunkData(IMemoryOwner<byte> owner, int actualLength)
        {
            _owner = owner;
            Length = actualLength;
            Memory = owner.Memory[..actualLength];
        }

        /// <summary>Возвращает буфер в <see cref="MemoryPool{T}.Shared"/>.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner?.Dispose();
            _owner = null;
        }
    }

    #endregion

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private int _consecutive403Count;
    private int _requestSequenceNumber;
    private int _consecutiveRefreshFailures;

    #region Chunk Download Result

    /// <summary>Результат попытки загрузки одного чанка.</summary>
    private enum ChunkDownloadResult
    {
        /// <summary>Чанк успешно загружен или уже был доступен.</summary>
        Success,

        /// <summary>Сервер вернул 403 Forbidden — требуется refresh URL.</summary>
        Forbidden403,

        /// <summary>Сетевая ошибка — допускается retry с backoff.</summary>
        NetworkError,

        /// <summary>Неустранимая ошибка — дальнейшие попытки бессмысленны.</summary>
        Fatal,

        /// <summary>Операция отменена через CancellationToken.</summary>
        Cancelled,

        /// <summary>Истёк таймаут ожидания слота параллелизма.</summary>
        SlotTimeout,

        /// <summary>Запрошенный диапазон за пределами контента.</summary>
        OutOfRange
    }

    #endregion

    #region ReadAtAsync

    /// <summary>
    /// Читает данные из позиции <paramref name="position"/>, загружая чанк при необходимости.
    /// Порядок поиска: RAM-кэш → диск → сеть.
    /// </summary>
    /// <param name="position">Абсолютная байтовая позиция в контенте.</param>
    /// <param name="buffer">Целевой буфер.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Количество прочитанных байт; 0 если позиция за EOF.</returns>
    internal async Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        if (position >= _contentLength) return 0;

        int chunkIndex = (int)(position / _chunkSize);
        int offsetInChunk = (int)(position % _chunkSize);

        if (chunkIndex >= _totalChunks) return 0;

        // 1. RAM-кэш
        if (_ramChunks.TryGetValue(chunkIndex, out var ramEntry))
            return CopyFromChunk(ramEntry.Memory.Span, ramEntry.Length, offsetInChunk, buffer);

        // 2. Диск
        if (await TryLoadChunkFromDiskAsync(chunkIndex, ct) is { } diskEntry)
            return CopyFromChunk(diskEntry.Memory.Span, diskEntry.Length, offsetInChunk, buffer);

        // 3. Сеть — с retry по смене эпохи
        for (int attempt = 0; attempt < ReadAtMaxEpochRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);

                var result = await EnsureChunkAsync(chunkIndex, linkedCts.Token);

                if (result == ChunkDownloadResult.OutOfRange) return 0;

                if (_ramChunks.TryGetValue(chunkIndex, out ramEntry))
                    return CopyFromChunk(ramEntry.Memory.Span, ramEntry.Length, offsetInChunk, buffer);

                if (await TryLoadChunkFromDiskAsync(chunkIndex, ct) is { } afterNetworkDisk)
                    return CopyFromChunk(afterNetworkDisk.Memory.Span, afterNetworkDisk.Length, offsetInChunk, buffer);
            }
            catch (ChunkDownloadFatalException) { throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Debug($"[CachingSource] ReadAt chunk {chunkIndex}: epoch changed, " +
                          $"retry {attempt + 1}/{ReadAtMaxEpochRetries}");
                await Task.Delay(ReadAtEpochRetryDelayMs, ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new IOException($"Failed to load chunk {chunkIndex} after {ReadAtMaxEpochRetries} retries");
    }

    /// <summary>
    /// Пытается загрузить чанк с диска и добавить в RAM-кэш.
    /// Обрабатывает гонку: если другой поток уже добавил чанк в <see cref="_ramChunks"/> —
    /// возвращает буфер в пул и читает из существующего.
    /// </summary>
    /// <param name="chunkIndex">Индекс чанка.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>RAM-запись чанка или null, если чанк на диске отсутствует.</returns>
    /// <exception cref="FileNotFoundException">
    /// Выбрасывается, если метаданные считают чанк скачанным, но физический файл кэша отсутствует на диске
    /// или был удалён пользователем через настройки во время активного воспроизведения.
    /// </exception>
    private async Task<ChunkData?> TryLoadChunkFromDiskAsync(int chunkIndex, CancellationToken ct)
    {
        var diskResult = await _cacheManager.ReadChunkAsync(_cacheKey, chunkIndex, ct).ConfigureAwait(false);
        if (diskResult.HasValue)
        {
            var (owner, length) = diskResult.Value;
            var chunkData = new ChunkData(owner, length);

            if (_ramChunks.TryAdd(chunkIndex, chunkData))
                return chunkData;

            // Гонка: другой поток уже добавил этот чанк — возвращаем буфер и читаем из существующего.
            chunkData.Dispose();
            return _ramChunks.TryGetValue(chunkIndex, out var existing) ? existing : null;
        }

        // Если ReadChunkAsync вернул null, но при этом наш _cacheEntry считает этот чанк скачанным,
        // значит произошла принудительная инвалидация/очистка всего кэша пользователем прямо во время воспроизведения.
        if (_cacheEntry != null && _cacheEntry.IsChunkDownloaded(chunkIndex))
        {
            var filePath = _cacheManager.GetCachePath(_cacheKey);
            if (!File.Exists(filePath) || _cacheManager.GetCacheInfo(_cacheKey) == null)
            {
                throw new FileNotFoundException(
                    $"Cache for track {_trackId} was invalidated or deleted on disk during active playback.", filePath);
            }
        }

        return null;
    }

    /// <summary>
    /// Копирует данные из чанка в целевой буфер с учётом смещения <paramref name="offset"/>.
    /// </summary>
    /// <param name="chunkData">Данные чанка.</param>
    /// <param name="chunkLength">Реальная длина данных в чанке.</param>
    /// <param name="offset">Смещение внутри чанка.</param>
    /// <param name="buffer">Целевой буфер.</param>
    /// <returns>Количество скопированных байт.</returns>
    private static int CopyFromChunk(
        ReadOnlySpan<byte> chunkData, int chunkLength, int offset, Memory<byte> buffer)
    {
        int available = Math.Min(buffer.Length, chunkLength - offset);
        if (available <= 0) return 0;
        chunkData.Slice(offset, available).CopyTo(buffer.Span);
        return available;
    }

    #endregion

    #region EnsureChunkAsync

    /// <summary>
    /// Гарантирует доступность чанка с заданным индексом.
    /// Использует retry-логику, circuit breaker и координацию параллельных загрузок.
    /// </summary>
    /// <param name="index">Индекс чанка.</param>
    /// <param name="ct">Токен отмены (epoch или lifetime).</param>
    /// <returns>Результат загрузки.</returns>
    private async Task<ChunkDownloadResult> EnsureChunkAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _totalChunks) return ChunkDownloadResult.OutOfRange;

        long chunkStart = (long)index * _chunkSize;
        if (chunkStart >= _contentLength) return ChunkDownloadResult.OutOfRange;

        if (IsChunkAvailable(index)) return ChunkDownloadResult.Success;

        // Дедупликация: если уже есть активная загрузка этого чанка — ждём её.
        if (_activeDownloads.TryGetValue(index, out var existingTask))
        {
            await WaitForActiveDownloadAsync(existingTask, ct);
            if (IsChunkAvailable(index)) return ChunkDownloadResult.Success;
        }

        ct.ThrowIfCancellationRequested();
        CheckCircuitBreaker(index);

        int maxAttempts = _config.MaxNetworkRetries + _config.Max403BeforeCircuitBreak;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (IsChunkAvailable(index)) return ChunkDownloadResult.Success;

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
                        Log.Warn($"[CachingSource] Chunk {index}: network retry {attempt + 1}/{maxAttempts}, delay={delay}ms");
                        await Task.Delay(delay, ct);
                        continue;

                    case ChunkDownloadResult.SlotTimeout:
                        await Task.Delay(200, ct);
                        continue;

                    case ChunkDownloadResult.Cancelled:
                        if (!ct.IsCancellationRequested)
                        {
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
                // Другой поток создал задачу между нашей проверкой и TryAdd — ждём её.
                if (_activeDownloads.TryGetValue(index, out var concurrentTask))
                    await WaitForActiveDownloadAsync(concurrentTask, ct);

                if (IsChunkAvailable(index)) return ChunkDownloadResult.Success;
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
    /// Ждёт завершения активной загрузки, поглощая epoch-отмены и прочие исключения.
    /// Пробрасывает только <see cref="ChunkDownloadFatalException"/> и пользовательскую отмену.
    /// </summary>
    /// <param name="task">Задача активной загрузки.</param>
    /// <param name="ct">Токен отмены пользователя (lifetime/seek).</param>
    private static async Task WaitForActiveDownloadAsync(Task task, CancellationToken ct)
    {
        try { await task.WaitAsync(ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (ChunkDownloadFatalException) { throw; }
        catch { }
    }

    /// <summary>Проверяет circuit breaker перед началом загрузки.</summary>
    /// <param name="index">Индекс чанка — используется в сообщении исключения.</param>
    /// <exception cref="ChunkDownloadFatalException">Если накоплено слишком много 403.</exception>
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
    /// Вычисляет задержку retry с экспоненциальным backoff.
    /// Ограничена 10 секундами во избежание бесконечного ожидания.
    /// </summary>
    /// <param name="attempt">Номер попытки (от 1).</param>
    /// <returns>Задержка в миллисекундах.</returns>
    private int ComputeRetryDelay(int attempt)
    {
        int baseDelay = _config.NetworkRetryBaseDelayMs;
        return _config.UseExponentialBackoff
            ? Math.Min(baseDelay * (1 << attempt), 10_000)
            : baseDelay;
    }

    #endregion

    #region DownloadChunkCoreAsync

    /// <summary>
    /// Вычисляет HTTP Range, валидирует его и инициирует загрузку.
    /// Удерживает download slot на всё время HTTP-операции для реального
    /// ограничения параллелизма до <see cref="StreamingConfig.MaxConcurrentDownloads"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Почему slot удерживается на весь download:</b></para>
    /// <para>Без удержания — неограниченный параллелизм при seek + preload + on-demand read
    /// → connection pool exhaustion → timeout → decoder starvation → underrun.</para>
    /// </remarks>
    private async Task<ChunkDownloadResult> DownloadChunkCoreAsync(int index, CancellationToken ct)
    {
        if (_disposed || _cacheEntry!.IsChunkDownloaded(index))
            return _disposed ? ChunkDownloadResult.Cancelled : ChunkDownloadResult.Success;

        long start = (long)index * _chunkSize;
        long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);

        if (start >= _contentLength || start > end)
        {
            Log.Debug($"[CachingSource] Chunk {index} out of range: start={start}, contentLength={_contentLength}");
            return ChunkDownloadResult.OutOfRange;
        }

        bool gotSlot = false;

        try
        {
            // ═══ Guard: SemaphoreSlim.WaitAsync на disposed семафоре → ObjectDisposedException.
            // Проверяем _disposed перед обращением и ловим гонку в catch.
            if (_disposed) return ChunkDownloadResult.Cancelled;

            gotSlot = await _downloadSlots.WaitAsync(_config.DownloadSlotTimeoutMs, ct);
            if (!gotSlot) return ChunkDownloadResult.SlotTimeout;

            if (_disposed) return ChunkDownloadResult.Cancelled;

            if (_cacheEntry.IsChunkDownloaded(index))
                return ChunkDownloadResult.Success;

            if (ct.IsCancellationRequested)
                return ChunkDownloadResult.Cancelled;

            return await DownloadChunkHttpAsync(index, start, end, ct);
        }
        catch (ChunkDownloadFatalException) { throw; }
        catch (ObjectDisposedException) { return ChunkDownloadResult.Cancelled; }
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
                try { _downloadSlots.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// HTTP-загрузка чанка с явной моделью владения буфером через <see cref="MemoryPool{T}.Shared"/>.
    /// Владение передаётся в <see cref="ChunkData"/> → <c>_ramChunks</c> → возврат при эвикции.
    /// Для fire-and-forget записи на диск создаётся независимая копия через <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="index">Индекс чанка.</param>
    /// <param name="start">Начало HTTP Range в байтах.</param>
    /// <param name="end">Конец HTTP Range в байтах (включительно).</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task<ChunkDownloadResult> DownloadChunkHttpAsync(
        int index, long start, long end, CancellationToken ct)
    {
        int rn = Interlocked.Increment(ref _requestSequenceNumber);
        int expectedBytes = (int)(end - start + 1);

        Log.Debug($"[CachingSource] Chunk {index}: GET rn={rn}, range={start}-{end}");

        using var request = CreateChunkRequest(index, start, end, rn);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) { return ChunkDownloadResult.NetworkError; }
        catch (HttpRequestException) { return ChunkDownloadResult.NetworkError; }

        using (response)
        {
            if (ct.IsCancellationRequested) return ChunkDownloadResult.Cancelled;

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Interlocked.Increment(ref _consecutive403Count);
                await LogAndDiagnose403Async(index, request, response);
                return ChunkDownloadResult.Forbidden403;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Log.Debug($"[CachingSource] Chunk {index}: 416 (EOF)");
                return ChunkDownloadResult.OutOfRange;
            }

            Interlocked.Exchange(ref _consecutive403Count, 0);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
            {
                throw new ChunkDownloadFatalException(
                    "YouTube returned encrypted UMP format",
                    chunkIndex: index, consecutiveFailures: 0,
                    reason: ChunkDownloadFailureReason.UmpFormat, trackId: _trackId);
            }

            response.EnsureSuccessStatusCode();

            // Аренда через MemoryPool: владение передаётся в ChunkData.
            // IMemoryOwner<byte> гарантирует возврат в пул через Dispose,
            // исключая удержание рентованных буферов в RAM-кэше навсегда.
            IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(expectedBytes);
            int actualLength;

            try
            {
                using var contentStream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
                actualLength = await ReadStreamFullyAsync(
                    contentStream, memoryOwner.Memory[..expectedBytes], ct);
            }
            catch (Exception ex)
            {
                memoryOwner.Dispose();
                Log.Warn($"[CachingSource] Chunk {index} read error: {ex.Message}");
                return ChunkDownloadResult.NetworkError;
            }

            if (ct.IsCancellationRequested)
            {
                memoryOwner.Dispose();
                return ChunkDownloadResult.Cancelled;
            }

            if (actualLength == 0)
            {
                memoryOwner.Dispose();
                Log.Warn($"[CachingSource] Chunk {index}: empty response");
                return ChunkDownloadResult.NetworkError;
            }

            if (actualLength < expectedBytes)
            {
                bool isNearEof = start + actualLength >= _contentLength - _chunkSize;
                if (!isNearEof)
                {
                    memoryOwner.Dispose();
                    Log.Warn($"[CachingSource] Chunk {index} incomplete: {actualLength}/{expectedBytes}");
                    return ChunkDownloadResult.NetworkError;
                }
                Log.Debug($"[CachingSource] Chunk {index}: partial {actualLength}/{expectedBytes} (near EOF)");
            }

            // Передача владения в ChunkData. После этой строки вызывать
            // memoryOwner.Dispose() запрещено — он принадлежит chunkData.
            var chunkData = new ChunkData(memoryOwner, actualLength);

            if (!_ramChunks.TryAdd(index, chunkData))
            {
                // Другой поток уже сохранил этот чанк — возвращаем буфер.
                chunkData.Dispose();
            }
            else
            {
                // Копия для fire-and-forget: жизненный цикл не зависит от chunkData.
                // ArrayPool используется для краткосрочного буфера копии.
                byte[] diskCopy = ArrayPool<byte>.Shared.Rent(actualLength);
                chunkData.Memory.Span.CopyTo(diskCopy.AsSpan(0, actualLength));
                _ = WriteToDiskFireAndForgetAsync(index, diskCopy, actualLength);

                if (_ramChunks.Count > _config.MaxRamChunks)
                    EvictDistantRamChunks();
            }

            return ChunkDownloadResult.Success;
        }
    }

    /// <summary>
    /// Читает поток до <paramref name="buffer"/>.Length байт или EOF.
    /// <paramref name="ct"/> пробрасывается в <see cref="Stream.ReadAsync"/> — read прерывается при отмене.
    /// </summary>
    /// <param name="stream">Источник данных.</param>
    /// <param name="buffer">Целевой буфер.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Суммарное количество прочитанных байт.</returns>
    private static async ValueTask<int> ReadStreamFullyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// Fire-and-forget запись чанка на диск.
    /// Принимает независимую копию данных, арендованную из <see cref="ArrayPool{T}.Shared"/>,
    /// и возвращает её в пул после завершения записи.
    /// </summary>
    /// <param name="index">Индекс чанка.</param>
    /// <param name="rentedCopy">Арендованный буфер с данными.</param>
    /// <param name="length">Реальная длина данных в буфере.</param>
    private async Task WriteToDiskFireAndForgetAsync(int index, byte[] rentedCopy, int length)
    {
        try
        {
            await _cacheManager.WriteChunkAsync(
                _cacheKey, index, rentedCopy.AsMemory(0, length), CancellationToken.None);
        }
        catch (IOException ex)
        {
            Log.Warn($"[CachingSource] Disk write chunk {index}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedCopy);
        }
    }

    #endregion

    #region 403 Diagnostics

    /// <summary>Логирует расширенную диагностику при 403 Forbidden.</summary>
    /// <param name="index">Индекс чанка, при загрузке которого пришёл 403.</param>
    /// <param name="request">HTTP-запрос, вызвавший 403.</param>
    /// <param name="response">HTTP-ответ с кодом 403.</param>
    private async Task LogAndDiagnose403Async(
        int index, HttpRequestMessage request, HttpResponseMessage response)
    {
        int count = Volatile.Read(ref _consecutive403Count);
        var nParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "c");
        var reqUa = request.Headers.UserAgent.ToString();

        Log.Warn($"[CachingSource] ═══ 403 DIAGNOSTIC chunk {index} (consecutive={count}) ═══");
        Log.Warn($"[CachingSource]   c={cParam ?? "?"}, UA={reqUa[..Math.Min(reqUa.Length, 50)]}...");
        Log.Warn($"[CachingSource]   n-token: {nParam ?? "MISSING"} " +
                 $"(len={nParam?.Length ?? 0}, looks_encrypted={nParam?.Length is > 15 and < 25})");

        if (response.Headers.TryGetValues("X-Restrict-Formats-Hint", out var hints))
            Log.Warn($"[CachingSource]   Restrict-Hint: {string.Join(", ", hints)}");

        string responseBody = "";
        try { responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None); }
        catch { }

        if (responseBody.Length > 0)
            Log.Warn($"[CachingSource]   Body: {responseBody[..Math.Min(responseBody.Length, 200)]}");

        Log.Warn("[CachingSource] ════════════════════════════════════════════════════════");
    }

    #endregion

    #region URL Refresh

    /// <summary>
    /// Координированный URL refresh: только один поток выполняет refresh,
    /// остальные ждут его завершения. Содержит circuit breaker по числу неудачных refresh.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <exception cref="ChunkDownloadFatalException">
    /// Если circuit breaker открыт (слишком много последовательных неудач refresh).
    /// </exception>
    private async Task CoordinatedRefreshAsync(CancellationToken ct)
    {
        int refreshFailures = Volatile.Read(ref _consecutiveRefreshFailures);
        if (refreshFailures >= MaxRefreshFailuresBeforeCircuitBreak)
        {
            throw new ChunkDownloadFatalException(
                $"URL refresh circuit breaker OPEN: {refreshFailures} consecutive failures (encrypted n-token?)",
                chunkIndex: -1,
                consecutiveFailures: Volatile.Read(ref _consecutive403Count),
                reason: ChunkDownloadFailureReason.Forbidden403,
                trackId: _trackId,
                httpStatusCode: 403);
        }

        // ═══ Guard: _refreshLock может быть disposed при гонке с Dispose/DisposeAsync.
        if (_disposed) return;

        bool acquired;
        try
        {
            acquired = await _refreshLock.WaitAsync(0, ct);
        }
        catch (ObjectDisposedException) { return; }

        if (!acquired)
        {
            Log.Debug("[CachingSource] Waiting for concurrent refresh...");
            try
            {
                await _refreshLock.WaitAsync(ct);
                _refreshLock.Release();
            }
            catch (ObjectDisposedException) { return; }

            if (Volatile.Read(ref _consecutiveRefreshFailures) >= MaxRefreshFailuresBeforeCircuitBreak)
            {
                throw new ChunkDownloadFatalException(
                    "URL refresh circuit breaker OPEN after concurrent refresh",
                    chunkIndex: -1,
                    consecutiveFailures: Volatile.Read(ref _consecutive403Count),
                    reason: ChunkDownloadFailureReason.Forbidden403,
                    trackId: _trackId,
                    httpStatusCode: 403);
            }

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

            var previousUrl = _currentUrl;
            await RefreshUrlAsync(ct);
            _lastRefreshTime = DateTime.UtcNow;
            Interlocked.Exchange(ref _consecutive403Count, 0);
            Log.Info("[CachingSource] 403 counter reset after URL refresh");

            var newNToken = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
            var oldNToken = UrlEx.TryGetQueryParameterValue(previousUrl, "n");

            if (!string.IsNullOrEmpty(newNToken) &&
                string.Equals(newNToken, oldNToken, StringComparison.Ordinal))
            {
                int failures = Interlocked.Increment(ref _consecutiveRefreshFailures);
                Log.Warn($"[CachingSource] n-token unchanged after refresh " +
                         $"(attempt {failures}/{MaxRefreshFailuresBeforeCircuitBreak})");
            }
            else
            {
                Interlocked.Exchange(ref _consecutiveRefreshFailures, 0);
            }

            await Task.Delay(_config.PostRefreshDelayMs, ct);
        }
        finally
        {
            try { _refreshLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    #endregion

    #region HTTP Request Building

    /// <summary>
    /// Строит HTTP-запрос для загрузки чанка.
    /// Для YouTube-URL применяет chunk-специфичные параметры и UA; для прочих — generic UA.
    /// </summary>
    /// <param name="index">Индекс чанка (для диагностики).</param>
    /// <param name="start">Начало HTTP Range.</param>
    /// <param name="end">Конец HTTP Range (включительно).</param>
    /// <param name="rn">Монотонный номер запроса (request number) для YouTube.</param>
    /// <returns>Готовый <see cref="HttpRequestMessage"/>.</returns>
    private HttpRequestMessage CreateChunkRequest(int index, long start, long end, int rn)
    {
        bool isYouTube = _currentUrl.Contains("googlevideo.com/videoplayback");

        if (isYouTube)
        {
            string url = BuildYouTubeChunkUrl(_currentUrl, rn);
            LogChunkRequestParams(index, url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            SharedHttpClient.ApplyUserAgentFromUrl(request, url);

            Log.Debug($"[CachingSource] Chunk {index} UA: " +
                      $"{request.Headers.UserAgent.ToString()[..Math.Min(60, request.Headers.UserAgent.ToString().Length)]}...");
            return request;
        }

        var genericRequest = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
        genericRequest.Headers.Range = new RangeHeaderValue(start, end);
        genericRequest.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);
        return genericRequest;
    }

    /// <summary>Логирует ключевые параметры URL чанка для диагностики.</summary>
    /// <param name="index">Индекс чанка.</param>
    /// <param name="url">Итоговый URL запроса.</param>
    private static void LogChunkRequestParams(int index, string url)
    {
        var nParam = UrlEx.TryGetQueryParameterValue(url, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var sigParam = UrlEx.TryGetQueryParameterValue(url, "sig");

        Log.Debug($"[CachingSource] Chunk {index} URL: {url[..Math.Min(url.Length, 300)]}");
        Log.Debug($"[CachingSource] Chunk {index} params: c={cParam ?? "MISSING"}, " +
                  $"n={nParam?[..Math.Min(nParam.Length, 15)] ?? "MISSING"}..., " +
                  $"sig={(sigParam is not null ? $"{sigParam.Length}chars" : "MISSING")}");
    }

    /// <summary>
    /// Добавляет chunk-специфичные параметры <c>rn</c> и <c>rbuf</c> к базовому URL.
    /// Использует in-place замену через <see cref="UrlEx.SetQueryParameter"/> —
    /// существующие параметры обновляются, порядок остальных не изменяется.
    /// </summary>
    /// <param name="baseUrl">Базовый URL потока.</param>
    /// <param name="rn">Монотонный номер запроса.</param>
    /// <returns>URL с подставленными параметрами чанка.</returns>
    private static string BuildYouTubeChunkUrl(string baseUrl, int rn)
    {
        string url = UrlEx.SetQueryParameter(baseUrl, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");
        return url;
    }

    #endregion

    #region Chunk Helpers

    /// <summary>
    /// Проверяет доступность чанка: либо загружен на диск, либо находится в RAM-кэше.
    /// </summary>
    /// <param name="index">Индекс чанка.</param>
    /// <returns><c>true</c> если данные чанка доступны без сетевого запроса.</returns>
    private bool IsChunkAvailable(int index) =>
        _cacheEntry!.IsChunkDownloaded(index) || _ramChunks.ContainsKey(index);

    /// <summary>
    /// Эвикция удалённых чанков из RAM с корректным возвратом буферов в пул.
    /// Удаляет до <c>MaxRamChunks / 4</c> самых далёких от текущей позиции чанков.
    /// </summary>
    private void EvictDistantRamChunks()
    {
        int current = Volatile.Read(ref _currentChunk);

        var toEvict = _ramChunks.Keys
            .Where(i => Math.Abs(i - current) > _config.RamEvictionDistance)
            .OrderByDescending(i => Math.Abs(i - current))
            .Take(_config.MaxRamChunks / 4)
            .ToList();

        foreach (int idx in toEvict)
        {
            if (_ramChunks.TryRemove(idx, out var chunk))
                chunk.Dispose();
        }
    }

    #endregion
}