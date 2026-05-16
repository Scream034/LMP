using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Models;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Cache;

public sealed class AudioCacheManager : IAsyncDisposable, IDisposable
{
    private readonly string _cacheDirectory;
    private readonly long _maxCacheSize;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    /// <summary>
    /// Reverse index: trackId → thread-safe множество cacheKey.
    /// Ускоряет поиск форматов трека с O(N) до O(1).
    /// Значение — <see cref="ConcurrentDictionary{TKey,TValue}"/> используется как
    /// thread-safe HashSet: key = cacheKey, value = 1 (byte sentinel).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _trackIndex = new();

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>Per-file семафоры для изоляции параллельных записей одного файла.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileWriteLocks = new();

    private readonly CancellationTokenSource _timerCts = new();
    private readonly Task _autoSaveTask;
    private volatile bool _disposed;

    /// <summary>Статический экземпляр — кэширует reflection-метаданные типов между сохранениями.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public event Action<string, string, int, bool>? OnFormatCached;
    public event Action? OnCacheCleared;

    public AudioCacheManager(string? cacheDirectory = null, long maxCacheSizeMb = 2048)
    {
        _cacheDirectory = cacheDirectory ?? G.Folder.AudioCache;
        _maxCacheSize = maxCacheSizeMb * 1024 * 1024;
        Directory.CreateDirectory(_cacheDirectory);
        LoadIndex();
        _autoSaveTask = AutoSaveLoopAsync(_timerCts.Token);
        Log.Info($"[AudioCache] Initialized: {_cacheDirectory}, max={maxCacheSizeMb}MB, entries={_entries.Count}");
    }

    #region Public API

    /// <summary>
    /// Проверяет наличие хотя бы одного полного кэша для трека. O(1) по reverse index.
    /// </summary>
    public bool IsTrackFullyCached(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return false;
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return false;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.IsComplete)
                return true;
        }
        return false;
    }

    /// <summary>Возвращает метаданные лучшего (по битрейту) полного кэша трека.</summary>
    public CacheEntry? FindBestCacheByTrackId(string trackId) => FindBestCache(trackId);

    /// <summary>
    /// Массовое обновление IsCached статуса треков.
    /// O(entries) вместо O(tracks × entries) благодаря reverse index.
    /// </summary>
    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        var trackMap = new Dictionary<string, List<TrackInfo>>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            if (track.IsDownloaded || track.IsCached || string.IsNullOrEmpty(track.Id))
                continue;

            if (!trackMap.TryGetValue(track.Id, out var list))
            {
                list = new List<TrackInfo>(1);
                trackMap[track.Id] = list;
            }
            list.Add(track);
        }

        if (trackMap.Count == 0) return;

        foreach (var (trackId, tracksList) in trackMap)
        {
            if (!_trackIndex.TryGetValue(trackId, out var keys)) continue;

            CacheEntry? bestEntry = null;

            foreach (var key in keys.Keys)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.IsComplete
                    && (bestEntry == null || entry.Bitrate > bestEntry.Bitrate))
                {
                    bestEntry = entry;
                }
            }

            if (bestEntry != null)
            {
                foreach (var track in tracksList)
                    track.MarkAsCached(bestEntry.Format.ToString(), bestEntry.Bitrate);
            }
        }
    }

    /// <summary>Проверяет, полностью ли загружен кэш по ключу.</summary>
    public bool IsFullyCached(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) && entry.IsComplete;

    /// <summary>Возвращает лучший (по битрейту) полный кэш трека или null.</summary>
    public CacheEntry? FindBestCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return null;

        CacheEntry? best = null;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && (best == null || entry.Bitrate > best.Bitrate))
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>Проверяет наличие хотя бы одного загруженного чанка.</summary>
    public bool HasPartialCache(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) && entry.DownloadedChunks > 0;

    /// <summary>Возвращает метаданные записи кэша или null.</summary>
    public CacheEntry? GetCacheInfo(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) ? entry : null;

    /// <summary>Возвращает путь к файлу кэша по SHA-256 ключу.</summary>
    public string GetCachePath(string cacheKey)
    {
        var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))[..16];
        return Path.Combine(_cacheDirectory, safeId + CacheFileExtension);
    }

    /// <summary>Обновляет время последнего доступа к записи.</summary>
    public void Touch(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
            entry.LastAccessedAt = DateTime.UtcNow;
    }

    /// <summary>Создаёт или обновляет запись кэша. Потокобезопасно через <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>.</summary>
    public CacheEntry CreateOrUpdate(
        string cacheKey, string trackId, string url, long totalSize,
        AudioFormat format, AudioCodec codec, int bitrate = 0,
        long durationMs = -1, int chunkSize = ChunkSize)
    {
        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = url,
            TotalSize = totalSize,
            Format = format,
            Codec = codec,
            Bitrate = bitrate,
            DurationMs = durationMs,
            ChunkSize = chunkSize,
            TotalChunks = (int)Math.Ceiling((double)totalSize / chunkSize),
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        entry.OriginalUrl = url;
        entry.LastAccessedAt = DateTime.UtcNow;
        if (bitrate > 0) entry.Bitrate = bitrate;
        if (durationMs > 0) entry.DurationMs = durationMs;

        AddToTrackIndex(trackId, cacheKey);
        return entry;
    }

    /// <summary>Помечает кэш полностью загруженным и уведомляет подписчиков.</summary>
    public void MarkComplete(string cacheKey, long? durationMs = null, int? bitrate = null)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;

        entry.IsComplete = true;
        entry.CompletedAt = DateTime.UtcNow;
        entry.LastAccessedAt = DateTime.UtcNow;
        if (durationMs.HasValue) entry.DurationMs = durationMs.Value;
        if (bitrate.HasValue) entry.Bitrate = bitrate.Value;

        UpdateFileSizeCache(entry);
        Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
        _ = SaveIndexAsync();
        RaiseFormatCached(entry);
    }

    /// <summary>
    /// Записывает чанк на диск с per-file эксклюзивным доступом.
    /// Seek + Write не являются атомарной операцией — без lock'а параллельная запись
    /// двух чанков одного файла приводит к corruption данных.
    /// </summary>
    public async Task WriteChunkAsync(
        string cacheKey, int chunkIndex, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;

        // Быстрая проверка без лока — hot path для уже завершённых записей.
        if (entry.IsComplete) return;

        var fileLock = _fileWriteLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct);

        try
        {
            // ═══ RE-CHECK IsComplete ВНУТРИ ЛОКА ═══
            // Гонка: ResumeCacheFromDownloadedFileAsync или параллельный WriteChunkAsync
            // мог выставить IsComplete=true пока мы ждали WaitAsync.
            // Без повторной проверки запись поверх полного файла вызовет corrupted stream.
            if (entry.IsComplete) return;

            var filePath = GetCachePath(cacheKey);
            long offset = (long)chunkIndex * entry.ChunkSize;

            await using var fs = new FileStream(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(data, ct);

            entry.MarkChunkDownloaded(chunkIndex);
            entry.LastAccessedAt = DateTime.UtcNow;

            if (!entry.IsComplete && entry.DownloadedChunks >= entry.TotalChunks)
            {
                entry.IsComplete = true;
                entry.CompletedAt = DateTime.UtcNow;
                UpdateFileSizeCache(entry);
                Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
                RaiseFormatCached(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Write chunk failed: {ex.Message}");
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Читает чанк с диска в буфер из <see cref="MemoryPool{T}.Shared"/>.
    /// Возвращает <see cref="IMemoryOwner{T}"/> — вызывающий обязан вызвать <c>Dispose()</c>.
    /// Возвращает <c>null</c> если чанк недоступен или произошла ошибка.
    /// </summary>
    public async Task<(IMemoryOwner<byte> Owner, int Length)?> ReadChunkAsync(
        string cacheKey, int chunkIndex, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return null;
        if (!entry.IsChunkDownloaded(chunkIndex)) return null;

        var filePath = GetCachePath(cacheKey);
        if (!File.Exists(filePath)) return null;

        long offset = (long)chunkIndex * entry.ChunkSize;
        int size = (int)Math.Min(entry.ChunkSize, entry.TotalSize - offset);
        if (size <= 0) return null;

        var memoryOwner = MemoryPool<byte>.Shared.Rent(size);

        try
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            var buffer = memoryOwner.Memory[..size];

            while (totalRead < size)
            {
                int read = await fs.ReadAsync(buffer[totalRead..], ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != size)
            {
                memoryOwner.Dispose();
                return null;
            }

            entry.LastAccessedAt = DateTime.UtcNow;
            return (memoryOwner, size);
        }
        catch
        {
            memoryOwner.Dispose();
            return null;
        }
    }

    /// <summary>Открывает поток для чтения полностью закэшированного файла.</summary>
    public Stream? OpenCachedStream(string cacheKey)
    {
        if (!IsFullyCached(cacheKey)) return null;

        Touch(cacheKey);
        return new FileStream(
            GetCachePath(cacheKey),
            FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: CacheFileBufferSize);
    }

    /// <summary>Удаляет запись кэша и соответствующий файл с диска.</summary>
    public void RemoveCache(string cacheKey)
    {
        if (!_entries.TryRemove(cacheKey, out var entry)) return;

        RemoveFromTrackIndex(entry.TrackId, cacheKey);

        var filePath = GetCachePath(cacheKey);
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch { }

        _ = SaveIndexAsync();
    }

    /// <summary>Удаляет наиболее старые записи если суммарный размер превышает лимит.</summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var stats = GetStats();
        if (stats.TotalSizeBytes <= _maxCacheSize) return;

        Log.Info($"[AudioCache] Cleanup needed: {stats.TotalSizeBytes / 1024 / 1024}MB > {_maxCacheSize / 1024 / 1024}MB");

        long totalSize = stats.TotalSizeBytes;

        var entries = _entries.Values
            .OrderBy(e => e.LastAccessedAt)
            .ToList();

        foreach (var entry in entries)
        {
            if (totalSize <= _maxCacheSize * CacheCleanupThreshold) break;
            totalSize -= entry.ActualFileSize;
            RemoveCache(entry.CacheKey);
        }

        Log.Info($"[AudioCache] Cleanup complete, new size: {totalSize / 1024 / 1024}MB");
    }

    #endregion

    #region Resume Cache From Downloaded File

    /// <summary>
    /// Дозаполняет chunk-кэш из уже скачанного полного файла.
    ///
    /// <para><b>Архитектура:</b></para>
    /// <para>Вместо копирования файла — читаем недостающие диапазоны байт из скачанного файла
    /// и пишем их через <see cref="WriteChunkAsync"/>. Это сохраняет семантику chunk-кэша:
    /// <see cref="CacheEntry.DownloadedChunks"/> растёт постепенно, прогресс-бар плавный,
    /// <see cref="CacheEntry.IsComplete"/> выставляется естественным образом.</para>
    ///
    /// <para><b>Порядок записи: [startChunkHint..end] затем [0..startChunkHint)</b></para>
    /// <para>Сначала чанки вокруг текущей позиции воспроизведения — decoder получает данные
    /// из RAM-кэша (через <see cref="ReadChunkAsync"/> → <c>_ramChunks</c>) немедленно,
    /// без обращения к сети.</para>
    ///
    /// <para><b>Один переиспользуемый буфер:</b> ArrayPool аренда происходит один раз
    /// на всё время resume (не N раз на каждый чанк).</para>
    ///
    /// <para><b>TotalSize mismatch:</b> clen из YouTube URL иногда отличается от реального
    /// размера файла. После прохода всех чанков проверяем достижение порога 99% и принудительно
    /// выставляем IsComplete если порог достигнут.</para>
    /// </summary>
    /// <param name="trackId">ID трека.</param>
    /// <param name="downloadedFilePath">Путь к полностью скачанному файлу (Downloads/).</param>
    /// <param name="format">Формат контейнера скачанного файла.</param>
    /// <param name="bitrate">Реальный битрейт скачанного файла (kbps).</param>
    /// <param name="startChunkHint">
    /// Индекс чанка текущей позиции воспроизведения.
    /// Resume стартует с этого чанка для минимизации latency decoder'а.
    /// 0 = начало файла (безопасный дефолт).
    /// </param>
    /// <param name="ct">Токен отмены. Частично заполненный кэш при отмене корректен.</param>
    public async Task ResumeCacheFromDownloadedFileAsync(
        string trackId,
        string downloadedFilePath,
        AudioFormat format,
        int bitrate,
        int startChunkHint = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId) || !File.Exists(downloadedFilePath))
            return;

        var downloadedInfo = new FileInfo(downloadedFilePath);
        if (downloadedInfo.Length == 0)
            return;

        string cacheKey = AudioSourceFactory.BuildCacheKey(trackId, format, bitrate);

        // Быстрая проверка — если уже complete, ничего не делаем.
        if (_entries.TryGetValue(cacheKey, out var existingEntry) && existingEntry.IsComplete)
        {
            Log.Debug($"[AudioCache] Resume skipped: {cacheKey} already complete");
            return;
        }

        long fileSize = downloadedInfo.Length;

        // ═══ СОЗДАЁМ ENTRY ЕСЛИ НЕ СУЩЕСТВУЕТ ═══
        // Сценарий: трек никогда не воспроизводился, только скачан.
        // CreateOrUpdate создаст entry с правильными метаданными.
        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = "",
            TotalSize = fileSize,
            Format = format,
            Codec = AudioSourceFactory.GetCodecForFormat(format),
            Bitrate = bitrate,
            ChunkSize = ChunkSize,
            TotalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize),
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        AddToTrackIndex(trackId, cacheKey);

        int chunkSize = entry.ChunkSize;
        int totalChunks = entry.TotalChunks;
        int clampedStart = Math.Clamp(startChunkHint, 0, totalChunks - 1);

        Log.Info($"[AudioCache] Resuming cache from downloaded file: {cacheKey}, " +
                 $"chunks={totalChunks}, start={clampedStart}, file={fileSize / 1024}KB");

        // ═══ ОДИН БУФЕР НА ВСЁ ВРЕМЯ RESUME ═══
        // Переиспользуем один ArrayPool-буфер вместо N аллокаций.
        // Безопасно: await WriteChunkAsync гарантирует завершение записи до следующего read.
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            // FileShare.Read: файл может быть открыт антивирусом или файловым менеджером.
            await using var sourceStream = new FileStream(
                downloadedFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            // ═══ ПОРЯДОК: [startChunk..end] затем [0..startChunk) ═══
            // Приоритет чанкам вокруг текущей позиции воспроизведения.
            // Decoder получает данные из кэша немедленно без сетевых запросов.
            await WriteChunkRangeAsync(
                entry, sourceStream, rentedBuffer,
                fromChunk: clampedStart, toChunkExclusive: totalChunks,
                fileSize, cacheKey, ct);

            if (!ct.IsCancellationRequested && clampedStart > 0)
            {
                await WriteChunkRangeAsync(
                    entry, sourceStream, rentedBuffer,
                    fromChunk: 0, toChunkExclusive: clampedStart,
                    fileSize, cacheKey, ct);
            }

            // ═══ MISMATCH GUARD: clen из URL vs реальный размер файла ═══
            // YouTube иногда возвращает clen, отличающийся от реального размера на несколько байт.
            // Это приводит к тому что TotalChunks(clen) > реальных чанков в файле,
            // и entry.IsComplete никогда не становится true через штатный путь.
            // Если >99% чанков помечены — принудительно завершаем.
            if (!entry.IsComplete && !ct.IsCancellationRequested)
            {
                double completionRatio = (double)entry.DownloadedChunks / entry.TotalChunks;
                if (completionRatio >= 0.99)
                {
                    var fileLock = _fileWriteLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                    await fileLock.WaitAsync(ct);
                    try
                    {
                        if (!entry.IsComplete)
                        {
                            entry.IsComplete = true;
                            entry.CompletedAt = DateTime.UtcNow;
                            UpdateFileSizeCache(entry);
                            Log.Info($"[AudioCache] Cache complete via mismatch guard: {cacheKey} " +
                                     $"({entry.DownloadedChunks}/{entry.TotalChunks} chunks, ratio={completionRatio:P1})");
                            _ = SaveIndexAsync();
                            RaiseFormatCached(entry);
                        }
                    }
                    finally
                    {
                        fileLock.Release();
                    }
                }
            }

            if (entry.IsComplete)
                Log.Info($"[AudioCache] Resume complete: {cacheKey}");
            else if (ct.IsCancellationRequested)
                Log.Debug($"[AudioCache] Resume cancelled: {cacheKey} " +
                          $"({entry.DownloadedChunks}/{entry.TotalChunks} chunks written)");
        }
        catch (OperationCanceledException)
        {
            // Частично заполненный кэш корректен — при следующем воспроизведении
            // CachingStreamSource докачает оставшиеся чанки по сети.
            Log.Debug($"[AudioCache] Resume cancelled: {cacheKey}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Resume failed for {cacheKey}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    /// <summary>
    /// Записывает диапазон чанков [fromChunk, toChunkExclusive) из sourceStream в кэш.
    /// Использует переданный переиспользуемый буфер — вызывающий владеет его жизненным циклом.
    /// </summary>
    private async Task WriteChunkRangeAsync(
        CacheEntry entry,
        FileStream sourceStream,
        byte[] rentedBuffer,
        int fromChunk,
        int toChunkExclusive,
        long fileSize,
        string cacheKey,
        CancellationToken ct)
    {
        int chunkSize = entry.ChunkSize;

        for (int i = fromChunk; i < toChunkExclusive && !ct.IsCancellationRequested; i++)
        {
            // Чанк уже есть — пропускаем (он мог быть загружен параллельным preload'ом).
            if (entry.IsChunkDownloaded(i)) continue;

            long offset = (long)i * chunkSize;
            if (offset >= fileSize) break;

            int expectedBytes = (int)Math.Min(chunkSize, fileSize - offset);

            // Позиционируем поток только если нужно — sequential read не требует seek.
            if (sourceStream.Position != offset)
                sourceStream.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            while (totalRead < expectedBytes)
            {
                int read = await sourceStream.ReadAsync(
                    rentedBuffer.AsMemory(totalRead, expectedBytes - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead == 0) continue;

            // Передаём фактически прочитанные байты. Последний чанк может быть короче chunkSize.
            await WriteChunkAsync(cacheKey, i, rentedBuffer.AsMemory(0, totalRead), ct);

            // IsComplete выставлен WriteChunkAsync — можно выйти досрочно.
            if (entry.IsComplete) return;
        }
    }

    #endregion

    #region Statistics

    /// <summary>Возвращает статистику кэша без IO (использует кэшированные размеры файлов).</summary>
    public CacheStats GetStats()
    {
        long totalSize = 0;
        int completeCount = 0;
        int partialCount = 0;
        int totalCount = 0;

        foreach (var entry in _entries.Values)
        {
            totalCount++;
            totalSize += entry.ActualFileSize;
            if (entry.IsComplete) completeCount++;
            else if (entry.DownloadedChunks > 0) partialCount++;
        }

        return new CacheStats
        {
            TotalEntries = totalCount,
            CompleteEntries = completeCount,
            PartialEntries = partialCount,
            TotalSizeBytes = totalSize,
            MaxSizeBytes = _maxCacheSize
        };
    }

    /// <summary>Возвращает (fileCount, sizeMb) для совместимости.</summary>
    public (int FileCount, int SizeMb) GetStatsCompact()
    {
        var stats = GetStats();
        return (stats.CompleteEntries, (int)(stats.TotalSizeBytes / 1024 / 1024));
    }

    /// <summary>Возвращает статистику папки Downloads.</summary>
    public static (int FileCount, int SizeMb) GetDownloadsStats()
    {
        try
        {
            var dir = new DirectoryInfo(G.Folder.Downloads);
            if (!dir.Exists) return (0, 0);
            var files = dir.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            long totalBytes = files.Sum(f => f.Length);
            return (files.Length, (int)(totalBytes / 1024 / 1024));
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] GetDownloadsStats error: {ex.Message}");
            return (0, 0);
        }
    }

    /// <summary>Возвращает список полностью закэшированных форматов трека.</summary>
    public List<(string Container, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(string, int)>();
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return result;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.IsComplete)
                result.Add((entry.Format.ToString(), entry.Bitrate));
        }
        return result;
    }

    /// <summary>Проверяет, закэширован ли конкретный формат/битрейт трека.</summary>
    public bool IsFormatCached(string trackId, string container, int bitrate)
    {
        if (!Enum.TryParse<AudioFormat>(container, true, out var format)) return false;
        return IsFullyCached(BuildCacheKey(trackId, format, bitrate));
    }

    /// <summary>Строит уникальный ключ кэша с нормализованным битрейтом.</summary>
    public static string BuildCacheKey(string trackId, AudioFormat format, int bitrate)
    {
        int normalizedBitrate = AudioConstants.NormalizeBitrate(bitrate);
        return $"{trackId}_{format}_{normalizedBitrate}";
    }

    /// <inheritdoc cref="AudioConstants.NormalizeBitrate"/>
    [Obsolete("Use AudioConstants.NormalizeBitrate() directly.")]
    public static int NormalizeBitrate(int bitrate) => AudioConstants.NormalizeBitrate(bitrate);

    #endregion

    #region Export to Downloads

    /// <summary>Экспортирует полностью закэшированный трек в папку Downloads.</summary>
    public async Task<bool> ExportTrackToDownloadsAsync(
        string trackId,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct = default)
    {
        var entry = FindBestCache(trackId);
        if (entry == null)
        {
            Log.Warn($"[AudioCache] Track {trackId} not fully cached, cannot export");
            return false;
        }
        return await PromoteCacheToDownloadsAsync(entry, getTrackFunc, updateTrackFunc, ct);
    }

    private async Task<bool> PromoteCacheToDownloadsAsync(
        CacheEntry entry,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct)
    {
        if (!await _saveLock.WaitAsync(1000, ct)) return false;

        try
        {
            var track = await getTrackFunc(entry.TrackId);
            if (track == null)
            {
                Log.Warn($"[AudioCache] Track not found: {entry.TrackId}");
                return false;
            }

            if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
            {
                Log.Debug($"[AudioCache] Already downloaded: {track.Title}");
                return true;
            }

            var cachePath = GetCachePath(entry.CacheKey);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[AudioCache] Cache file not found: {cachePath}");
                return false;
            }

            var fileInfo = new FileInfo(cachePath);
            if (fileInfo.Length < entry.TotalSize)
            {
                Log.Warn($"[AudioCache] Incomplete cache file: {fileInfo.Length} < {entry.TotalSize}");
                return false;
            }

            string ext = entry.Format switch
            {
                AudioFormat.WebM => "webm",
                AudioFormat.Mp4 => "m4a",
                AudioFormat.Ogg => "ogg",
                _ => "audio"
            };

            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string destPath = Path.Combine(G.Folder.Downloads, safeName);

            if (File.Exists(destPath))
            {
                var existing = new FileInfo(destPath);
                if (existing.Length == entry.TotalSize)
                {
                    track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
                    await updateTrackFunc(track);
                    return true;
                }

                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{entry.Bitrate}kbps.{ext}");
            }

            Log.Info($"[AudioCache] Exporting to Downloads: {Path.GetFileName(destPath)}");
            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
            await updateTrackFunc(track);
            OnFormatCached?.Invoke(entry.TrackId, entry.Format.ToString(), entry.Bitrate, true);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Export failed: {ex.Message}");
            return false;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    #endregion

    #region Clear & Maintenance

    /// <summary>Полностью очищает кэш аудио: записи, файлы, reverse index.</summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        if (!await _saveLock.WaitAsync(5000, ct))
        {
            Log.Warn("[AudioCache] ClearAllAsync: couldn't acquire lock");
            return;
        }

        try
        {
            Log.Info("[AudioCache] Clearing all cache...");
            _entries.Clear();
            _trackIndex.Clear();

            var dir = new DirectoryInfo(_cacheDirectory);
            if (dir.Exists)
            {
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
            }

            Log.Info("[AudioCache] Cache cleared");
        }
        finally
        {
            _saveLock.Release();
        }

        try { OnCacheCleared?.Invoke(); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnCacheCleared handler error: {ex.Message}"); }
    }

    /// <summary>Удаляет файлы из папки Downloads.</summary>
    public static async Task ClearDownloadsAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var dir = new DirectoryInfo(G.Folder.Downloads);
                if (!dir.Exists) return;

                Log.Info("[AudioCache] Clearing downloads folder...");
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
                Log.Info("[AudioCache] Downloads cleared");
            }
            catch (Exception ex) { Log.Error($"[AudioCache] ClearDownloadsAsync error: {ex.Message}"); }
        }, ct);
    }

    /// <summary>Удаляет все форматы кэша для указанного трека.</summary>
    public void RemoveTrackCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;

        var keysToRemove = keys.Keys.ToList();
        foreach (var key in keysToRemove)
            RemoveCache(key);

        Log.Debug($"[AudioCache] Removed {keysToRemove.Count} cache entries for track {trackId}");
    }

    /// <summary>Удаляет незавершённые записи кэша.</summary>
    public async Task RemoveIncompleteAsync(CancellationToken ct = default)
    {
        var incomplete = _entries
            .Where(kv => !kv.Value.IsComplete)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in incomplete)
            RemoveCache(key);

        if (incomplete.Count > 0)
        {
            Log.Info($"[AudioCache] Removed {incomplete.Count} incomplete cache entries");
            await SaveIndexAsync();
        }
    }

    /// <summary>Удаляет записи без файлов и файлы без записей.</summary>
    public async Task ValidateAndCleanupAsync(CancellationToken ct = default)
    {
        var orphanedEntries = new List<string>();

        foreach (var (key, entry) in _entries)
        {
            if (!File.Exists(GetCachePath(key)))
                orphanedEntries.Add(key);
            else
                UpdateFileSizeCache(entry);
        }

        foreach (var key in orphanedEntries)
        {
            if (_entries.TryRemove(key, out var entry))
                RemoveFromTrackIndex(entry.TrackId, key);
        }

        var validFiles = _entries.Keys
            .Select(GetCachePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dir = new DirectoryInfo(_cacheDirectory);
        if (dir.Exists)
        {
            foreach (var file in dir.GetFiles($"*{CacheFileExtension}"))
            {
                if (!validFiles.Contains(file.FullName))
                {
                    try { file.Delete(); Log.Debug($"[AudioCache] Deleted orphaned file: {file.Name}"); }
                    catch { }
                }
            }
        }

        if (orphanedEntries.Count > 0)
        {
            Log.Info($"[AudioCache] Validation: removed {orphanedEntries.Count} orphaned entries");
            await SaveIndexAsync();
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Добавляет cacheKey в reverse index trackId.
    /// Значение — sentinel byte (1): используем <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// как thread-safe HashSet без аллокаций <see cref="List{T}"/>.
    /// </summary>
    private void AddToTrackIndex(string trackId, string cacheKey)
    {
        var keys = _trackIndex.GetOrAdd(trackId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 1);
    }

    /// <summary>
    /// Удаляет cacheKey из reverse index. Удаляет trackId если форматов не осталось.
    /// </summary>
    private void RemoveFromTrackIndex(string trackId, string cacheKey)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;
        keys.TryRemove(cacheKey, out _);
        if (keys.IsEmpty) _trackIndex.TryRemove(trackId, out _);
    }

    /// <summary>Обновляет кэшированный размер файла без IO при последующих вызовах GetStats.</summary>
    private void UpdateFileSizeCache(CacheEntry entry)
    {
        try
        {
            var filePath = GetCachePath(entry.CacheKey);
            if (File.Exists(filePath))
                entry.ActualFileSize = new FileInfo(filePath).Length;
        }
        catch { }
    }

    private void RaiseFormatCached(CacheEntry entry)
    {
        try { OnFormatCached?.Invoke(entry.TrackId, entry.Format.ToString(), entry.Bitrate, false); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnFormatCached handler error: {ex.Message}"); }
    }

    private void LoadIndex()
    {
        var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
        if (!File.Exists(indexPath)) return;

        try
        {
            var json = File.ReadAllText(indexPath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.CacheKey)) continue;

                    if (File.Exists(GetCachePath(entry.CacheKey)))
                    {
                        entry.RestoreChunkMask();
                        UpdateFileSizeCache(entry);
                        _entries.TryAdd(entry.CacheKey, entry);
                        AddToTrackIndex(entry.TrackId, entry.CacheKey);
                    }
                }
            }

            Log.Debug($"[AudioCache] Loaded {_entries.Count} entries");
        }
        catch (Exception ex) { Log.Warn($"[AudioCache] Failed to load index: {ex.Message}"); }
    }

    private async Task SaveIndexAsync()
    {
        if (_disposed) return;
        if (!await _saveLock.WaitAsync(CacheSaveLockTimeoutMs)) return;

        try
        {
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            var entries = _entries.Values.ToList();

            foreach (var entry in entries)
                entry.SaveChunkMask();

            var json = JsonSerializer.Serialize(entries, s_jsonOptions);
            await File.WriteAllTextAsync(indexPath, json);
        }
        catch (Exception ex) { Log.Warn($"[AudioCache] Failed to save index: {ex.Message}"); }
        finally { _saveLock.Release(); }
    }

    private async Task AutoSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CacheAutoSaveIntervalMs, ct);
                await SaveIndexAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"[AudioCache] Auto-save error: {ex.Message}"); }
        }
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();
        try { _autoSaveTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { SaveIndexAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();
        try { await _autoSaveTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        await SaveIndexAsync();
        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    #endregion
}

public sealed class CacheEntry
{
    public string CacheKey { get; init; } = "";
    public string TrackId { get; init; } = "";
    public string OriginalUrl { get; set; } = "";
    public long TotalSize { get; init; }
    public AudioFormat Format { get; init; }
    public AudioCodec Codec { get; set; }
    public int Bitrate { get; set; }
    public long DurationMs { get; set; } = -1;
    public int ChunkSize { get; init; }
    public int TotalChunks { get; init; }

    private int _downloadedChunks;

    public int DownloadedChunks
    {
        get => Volatile.Read(ref _downloadedChunks);
        set => Volatile.Write(ref _downloadedChunks, value);
    }

    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsComplete { get; set; }
    public long ActualFileSize { get; set; }
    public string? ChunkMaskData { get; set; }

    [JsonIgnore] private int[]? _chunkBits;

    [JsonIgnore]
    public double DownloadProgress =>
        TotalChunks == 0 ? 0 : (double)DownloadedChunks / TotalChunks * 100;

    /// <summary>Проверяет, загружен ли чанк с заданным индексом. Потокобезопасно.</summary>
    public bool IsChunkDownloaded(int index)
    {
        EnsureChunkBits();
        if (index < 0 || index >= TotalChunks) return false;
        return (Volatile.Read(ref _chunkBits![index >> 5]) & (1 << (index & 31))) != 0;
    }

    /// <summary>Помечает чанк загруженным через CAS-цикл. Потокобезопасно.</summary>
    public void MarkChunkDownloaded(int index)
    {
        EnsureChunkBits();
        if (index < 0 || index >= TotalChunks) return;

        int word = index >> 5;
        int bit = 1 << (index & 31);
        int current = Volatile.Read(ref _chunkBits![word]);

        if ((current & bit) != 0) return;

        int original;
        do
        {
            original = current;
            current = Interlocked.CompareExchange(ref _chunkBits![word], original | bit, original);
        } while (current != original);

        if ((original & bit) == 0)
            Interlocked.Increment(ref _downloadedChunks);
    }

    /// <summary>
    /// Сериализует битовую маску чанков в Base64 для сохранения в JSON-индекс.
    /// Использует <see cref="ArrayPool{T}.Shared"/> для промежуточного буфера.
    /// </summary>
    public void SaveChunkMask()
    {
        if (_chunkBits == null) return;

        int byteCount = _chunkBits.Length * sizeof(int);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Buffer.BlockCopy(_chunkBits, 0, rented, 0, byteCount);
            ChunkMaskData = Convert.ToBase64String(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Восстанавливает битовую маску из Base64 и пересчитывает <see cref="DownloadedChunks"/>.</summary>
    public void RestoreChunkMask()
    {
        if (string.IsNullOrEmpty(ChunkMaskData)) return;

        EnsureChunkBits();

        try
        {
            var bytes = Convert.FromBase64String(ChunkMaskData);
            Buffer.BlockCopy(bytes, 0, _chunkBits!, 0, Math.Min(bytes.Length, _chunkBits!.Length * sizeof(int)));

            int count = 0;
            for (int i = 0; i < TotalChunks; i++)
                if (IsChunkDownloaded(i)) count++;

            Volatile.Write(ref _downloadedChunks, count);
        }
        catch { }
    }

    private void EnsureChunkBits()
    {
        _chunkBits ??= new int[(TotalChunks + 31) >> 5];
    }
}

public readonly struct CacheStats
{
    public int TotalEntries { get; init; }
    public int CompleteEntries { get; init; }
    public int PartialEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public long MaxSizeBytes { get; init; }

    public double UsagePercent =>
        MaxSizeBytes == 0 ? 0 : (double)TotalSizeBytes / MaxSizeBytes * 100;

    public string TotalSizeFormatted => FormatSize(TotalSizeBytes);
    public string MaxSizeFormatted => FormatSize(MaxSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}