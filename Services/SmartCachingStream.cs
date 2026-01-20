using System.Diagnostics;
using System.Net.Http.Headers;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Поток с умным кэшированием на диск.
/// 
/// КРИТИЧЕСКИЕ ИСПРАВЛЕНИЯ:
/// 1. Все HTTP запросы с CancellationToken
/// 2. Небольшие chunk'и для быстрого старта
/// 3. Таймауты на блокировки
/// 4. Немедленное прерывание при Dispose
/// </summary>
public class SmartCachingStream : Stream
{
    private readonly string _trackId;
    private readonly string _url;
    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly RangeMap _ranges;
    private readonly FileStream _cacheFile;

    // Отмена ВСЕХ операций
    private readonly CancellationTokenSource _disposeCts = new();

    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly object _positionLock = new();
    private long _position;

    // Фоновая предзагрузка
    private CancellationTokenSource? _prefetchCts;
    private Task? _prefetchTask;

    /// <summary>
    /// Сигнал для prefetch: Read нуждается в данных, уступи!
    /// </summary>
    private volatile int _readPriorityRequests;

    /// <summary>
    /// CancellationToken для прерывания текущего prefetch chunk
    /// </summary>
    private CancellationTokenSource? _prefetchChunkCts;

    /// <summary>
    /// Время последнего Read (для адаптивной стратегии)
    /// </summary>
    private long _lastReadTicks;

    // Адаптивные размеры чанков
    private const int MinReadChunkSize = 32 * 1024;   // 32KB минимум
    private const int MaxReadChunkSize = 256 * 1024;  // 256KB максимум
    private const int ReadChunkSize = 64 * 1024;      // 64KB базовый
    private const int PrefetchChunkSize = 128 * 1024; // 128KB
    private const int PrefetchAheadBytes = 512 * 1024;
    private const int ReadLockTimeoutMs = 3000;       // 3 сек для Read (было 10!)
    private const int PrefetchLockTimeoutMs = 100;    // 100ms для Prefetch
    private const int DownloadTimeoutMs = 10000;       // 10 сек таймаут на chunk

    private volatile bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _contentLength;

    public override long Position
    {
        get { lock (_positionLock) return _position; }
        set => Seek(value, SeekOrigin.Begin);
    }

    public double DownloadProgress => _contentLength > 0
        ? (double)_ranges.DownloadedBytes / _contentLength * 100
        : 0;

    public bool IsFullyDownloaded => _ranges.IsFullyDownloaded(_contentLength);

    public SmartCachingStream(
        string trackId,
        string url,
        long contentLength,
        HttpClient http,
        StreamCacheManager cacheManager)
    {
        _trackId = trackId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _cachePath = cacheManager.GetCachePath(trackId);

        _ranges = cacheManager.LoadRanges(trackId);

        _cacheFile = new FileStream(
            _cachePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.RandomAccess);

        if (_cacheFile.Length < _contentLength)
        {
            _cacheFile.SetLength(_contentLength);
        }

        Debug.WriteLine($"[SmartStream] Opened {trackId}, {_ranges.DownloadedBytes}/{_contentLength} ({DownloadProgress:F1}%)");

        StartPrefetch(0);
    }

    /// <summary>
    /// ИСПРАВЛЕННЫЙ Read с приоритетом
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed) return 0;

        long pos;
        lock (_positionLock) { pos = _position; }

        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, Math.Min(ReadChunkSize, _contentLength - pos));
        if (count <= 0) return 0;

        // Записываем время Read для адаптивной стратегии
        Interlocked.Exchange(ref _lastReadTicks, Environment.TickCount64);

        try
        {
            // КЛЮЧЕВОЕ: Получаем данные с приоритетом
            if (!EnsureRangeDownloadedWithPriority(pos, count, _disposeCts.Token))
            {
                Debug.WriteLine($"[SmartStream] Failed to get data at {pos}");
                return 0;
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartStream] Read error: {ex.Message}");
            return 0;
        }

        // Читаем из кэш-файла
        int bytesRead = 0;
        try
        {
            // Используем короткий таймаут для file lock
            if (!_fileLock.Wait(1000, _disposeCts.Token))
            {
                Debug.WriteLine("[SmartStream] File lock timeout in Read");
                return 0;
            }

            try
            {
                lock (_positionLock)
                {
                    _cacheFile.Seek(_position, SeekOrigin.Begin);
                    bytesRead = _cacheFile.Read(buffer, offset, count);
                    _position += bytesRead;
                }
            }
            finally
            {
                _fileLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) return 0;

        long newPosition;
        lock (_positionLock)
        {
            newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _contentLength + offset,
                _ => throw new ArgumentException("Invalid SeekOrigin", nameof(origin))
            };

            newPosition = Math.Clamp(newPosition, 0, _contentLength);
            _position = newPosition;
        }

        Debug.WriteLine($"[SmartStream] Seek to {newPosition} ({newPosition * 100.0 / _contentLength:F1}%)");

        // Перезапускаем предзагрузку
        if (!_disposed)
        {
            StartPrefetch(newPosition);
        }

        return newPosition;
    }

    /// <summary>
    /// Получение данных с механизмом приоритета над prefetch
    /// </summary>
    private bool EnsureRangeDownloadedWithPriority(long start, int count, CancellationToken ct)
    {
        // Быстрый путь: данные уже есть
        if (_ranges.IsRangeComplete(start, start + count))
            return true;

        // ═══════════════════════════════════════════════════════════════
        // ПРИОРИТЕТ ЧТЕНИЯ: Прерываем prefetch если он работает
        // ═══════════════════════════════════════════════════════════════

        // 1. Сигнализируем что Read ждёт
        Interlocked.Increment(ref _readPriorityRequests);

        // 2. Прерываем текущий prefetch chunk
        try
        {
            _prefetchChunkCts?.Cancel();
        }
        catch { }

        try
        {
            // 3. Пытаемся получить download lock с КОРОТКИМ таймаутом
            //    (prefetch должен быстро освободить после Cancel)
            if (!_downloadLock.Wait(ReadLockTimeoutMs, ct))
            {
                Debug.WriteLine("[SmartStream] Read: download lock timeout (prefetch stuck?)");
                return false;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _readPriorityRequests);
        }

        try
        {
            if (_disposed) return false;

            // Повторная проверка после получения лока
            if (_ranges.IsRangeComplete(start, start + count))
                return true;

            var sw = Stopwatch.StartNew();
            bool success = DownloadRangeForRead(start, count, ct);

            if (success)
            {
                Debug.WriteLine($"[SmartStream] Read: downloaded {count}B at {start} in {sw.ElapsedMilliseconds}ms");
            }

            return success;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <summary>
    /// Загрузка для Read: минимальный размер, максимальный приоритет
    /// </summary>
    private bool DownloadRangeForRead(long start, int count, CancellationToken ct)
    {
        var missing = _ranges.FindMissingRange(start, start + count);
        if (missing == null) return true;

        var downloadStart = missing.Value.Start;
        // Для Read качаем только то что нужно + небольшой запас
        var downloadEnd = Math.Min(missing.Value.End, downloadStart + Math.Max(count, MinReadChunkSize));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(downloadStart, downloadEnd - 1);

            // Синхронный запрос с поддержкой отмены
            using var timeoutCts = new CancellationTokenSource(ReadLockTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[SmartStream] HTTP {response.StatusCode}");
                return false;
            }

            using var content = response.Content.ReadAsStream(linkedCts.Token);

            return WriteToCache(content, downloadStart, downloadEnd, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[SmartStream] Read download cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartStream] Read download error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Общий метод записи в кэш
    /// </summary>
    private bool WriteToCache(Stream content, long downloadStart, long downloadEnd, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long writePosition = downloadStart;
        int totalDownloaded = 0;

        while (!ct.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(buffer.Length, downloadEnd - writePosition);
            if (toRead <= 0) break;

            int read = content.Read(buffer, 0, toRead);
            if (read == 0) break;

            // File lock с коротким таймаутом
            if (!_fileLock.Wait(500, ct))
            {
                Debug.WriteLine("[SmartStream] File lock timeout during write");
                break;
            }

            try
            {
                _cacheFile.Seek(writePosition, SeekOrigin.Begin);
                _cacheFile.Write(buffer, 0, read);
            }
            finally
            {
                _fileLock.Release();
            }

            writePosition += read;
            totalDownloaded += read;
        }

        if (totalDownloaded > 0)
        {
            _cacheFile.Flush();
            _ranges.MarkComplete(downloadStart, downloadStart + totalDownloaded);
        }

        return totalDownloaded > 0;
    }

    #region Фоновая предзагрузка

    /// <summary>
    /// Предварительная буферизация для плавного старта воспроизведения.
    /// Вызывается ДО передачи потока в VLC.
    /// </summary>
    public async Task<bool> PreBufferAsync(int bytes, CancellationToken ct)
    {
        if (_disposed) return false;
        if (_ranges.IsRangeComplete(0, bytes)) return true;

        Debug.WriteLine($"[SmartStream] Pre-buffering {bytes} bytes...");
        var sw = Stopwatch.StartNew();

        try
        {
            // Получаем блокировку на загрузку
            if (!await _downloadLock.WaitAsync(5000, ct))
                return false;

            try
            {
                // Качаем начальный chunk
                await DownloadRangeAsync(0, bytes, ct);

                Debug.WriteLine($"[SmartStream] Pre-buffered in {sw.ElapsedMilliseconds}ms");
                return _ranges.IsRangeComplete(0, Math.Min(bytes, ReadChunkSize));
            }
            finally
            {
                _downloadLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartStream] Pre-buffer error: {ex.Message}");
            return false;
        }
    }

    private void StartPrefetch(long fromPosition)
    {
        if (_disposed) return;

        // Отменяем предыдущую задачу
        try { _prefetchCts?.Cancel(); } catch { }
        try { _prefetchChunkCts?.Cancel(); } catch { }

        _prefetchCts = new CancellationTokenSource();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _prefetchCts.Token,
            _disposeCts.Token);

        _prefetchTask = Task.Run(async () =>
        {
            try
            {
                await PrefetchLoopAsync(fromPosition, linkedCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SmartStream] Prefetch error: {ex.Message}");
            }
            finally
            {
                linkedCts.Dispose();
            }
        });
    }

    /// <summary>
    /// ИСПРАВЛЕННЫЙ Prefetch Loop: уступает приоритет Read
    /// </summary>
    private async Task PrefetchLoopAsync(long startPosition, CancellationToken ct)
    {
        long position = startPosition;
        long targetPosition = Math.Min(startPosition + PrefetchAheadBytes, _contentLength);

        while (!ct.IsCancellationRequested && position < targetPosition && position < _contentLength)
        {
            // ═══════════════════════════════════════════════════════════
            // КЛЮЧЕВОЕ: Проверяем приоритет Read ПЕРЕД захватом лока
            // ═══════════════════════════════════════════════════════════

            if (_readPriorityRequests > 0)
            {
                // Read ждёт — уступаем немедленно
                Debug.WriteLine("[SmartStream] Prefetch yielding to Read");
                await Task.Delay(100, ct);
                continue;
            }

            // Проверяем, есть ли данные
            if (_ranges.IsRangeComplete(position, Math.Min(position + PrefetchChunkSize, _contentLength)))
            {
                position += PrefetchChunkSize;
                continue;
            }

            // Пробуем получить лок с КОРОТКИМ таймаутом
            if (!await _downloadLock.WaitAsync(PrefetchLockTimeoutMs, ct))
            {
                // Лок занят — ждём и пробуем снова
                await Task.Delay(50, ct);
                continue;
            }

            try
            {
                // Проверяем приоритет ПОСЛЕ получения лока
                if (_readPriorityRequests > 0)
                {
                    Debug.WriteLine("[SmartStream] Prefetch releasing lock for Read");
                    continue; // finally освободит лок
                }

                if (_disposed) return;

                // Повторная проверка
                if (_ranges.IsRangeComplete(position, Math.Min(position + PrefetchChunkSize, _contentLength)))
                {
                    position += PrefetchChunkSize;
                    continue;
                }

                // ═══════════════════════════════════════════════════════
                // Создаём прерываемый CancellationToken для этого chunk
                // ═══════════════════════════════════════════════════════
                _prefetchChunkCts = new CancellationTokenSource();
                using var chunkLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, _prefetchChunkCts.Token);

                try
                {
                    await DownloadRangeAsync(position, PrefetchChunkSize, chunkLinkedCts.Token);
                    position += PrefetchChunkSize;
                }
                catch (OperationCanceledException) when (_prefetchChunkCts.IsCancellationRequested)
                {
                    // Прервано Read-ом — это нормально
                    Debug.WriteLine("[SmartStream] Prefetch chunk interrupted by Read priority");
                }
                finally
                {
                    _prefetchChunkCts.Dispose();
                    _prefetchChunkCts = null;
                }

                // Небольшая пауза между чанками чтобы не забивать сеть
                await Task.Delay(20, ct);
            }
            finally
            {
                _downloadLock.Release();
            }
        }

        if (!ct.IsCancellationRequested && !_disposed)
        {
            Debug.WriteLine($"[SmartStream] Prefetch complete: {DownloadProgress:F1}%");
            SaveMetadata();
        }
    }

    private async Task DownloadRangeAsync(long start, int count, CancellationToken ct)
    {
        count = (int)Math.Min(count, _contentLength - start);
        if (count <= 0) return;

        var missing = _ranges.FindMissingRange(start, start + count);
        if (missing == null) return;

        var downloadStart = missing.Value.Start;
        var downloadEnd = missing.Value.End;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(downloadStart, downloadEnd - 1);

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            response.EnsureSuccessStatusCode();

            await using var content = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[65536];
            long writePosition = downloadStart;

            while (!ct.IsCancellationRequested)
            {
                // Проверяем приоритет Read на каждой итерации
                if (_readPriorityRequests > 0)
                {
                    throw new OperationCanceledException("Read priority");
                }

                int read = await content.ReadAsync(buffer, ct);
                if (read == 0) break;

                await _fileLock.WaitAsync(ct);
                try
                {
                    _cacheFile.Seek(writePosition, SeekOrigin.Begin);
                    _cacheFile.Write(buffer, 0, read);
                }
                finally
                {
                    _fileLock.Release();
                }

                writePosition += read;
            }

            _cacheFile.Flush();
            _ranges.MarkComplete(downloadStart, writePosition);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartStream] Async download error: {ex.Message}");
        }
    }

    private void SaveMetadata()
    {
        try
        {
            _cacheManager.UpdateRanges(_trackId, _ranges);
        }
        catch { }
    }

    #endregion

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        Debug.WriteLine($"[SmartStream] Disposing... ({DownloadProgress:F1}% cached)");

        // КРИТИЧНО: Сначала устанавливаем флаг!
        _disposed = true;

        if (disposing)
        {
            // Отменяем ВСЕ операции
            try { _disposeCts.Cancel(); } catch { }
            try { _prefetchCts?.Cancel(); } catch { }

            // НЕ ждём завершения - просто освобождаем ресурсы
            // Задачи сами завершатся по CancellationToken

            // Сохраняем метаданные
            try { SaveMetadata(); } catch { }

            // Закрываем файл
            try
            {
                _cacheFile.Flush();
                _cacheFile.Dispose();
            }
            catch { }

            try { _downloadLock.Dispose(); } catch { }
            try { _fileLock.Dispose(); } catch { }
            try { _disposeCts.Dispose(); } catch { }
            try { _prefetchCts?.Dispose(); } catch { }

            Debug.WriteLine("[SmartStream] Disposed");
        }

        base.Dispose(disposing);
    }
}