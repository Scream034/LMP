using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Метаданные кэшированного потока.
/// </summary>
public class StreamCacheMetadata
{
    public string TrackId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public long ContentLength { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public string RangesJson { get; set; } = "[]";
    public string Codec { get; set; } = "";
    public int Bitrate { get; set; }
    public string Container { get; set; } = "";
}

/// <summary>
/// Менеджер кэширования аудиопотоков.
/// 
/// Архитектура кэширования:
/// 
/// 1. StreamCache (автоматический):
///    - Заполняется при прослушивании трека
///    - Управляется лимитом AudioCacheLimitMb
///    - Старые файлы удаляются автоматически
///    - Полностью закэшированный трек → IsCached = true
/// 
/// 2. Downloads (по запросу):
///    - Только по явному запросу пользователя ИЛИ если AutoSaveToDownloads = true
///    - Файлы НЕ удаляются автоматически
///    - IsDownloaded = true
/// 
/// Разница:
/// - IsCached = доступен офлайн через кэш (может быть удалён)
/// - IsDownloaded = сохранён в Downloads (не удаляется)
/// </summary>
public class StreamCacheManager : IDisposable
{
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _promoteLock = new(1, 1);
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    // Кэш статусов для быстрой проверки без IO
    private readonly ConcurrentDictionary<string, bool> _fullyCachedCache = new();
    private readonly ConcurrentDictionary<string, bool> _promotedCache = new();

    /// <summary>
    /// Событие: формат закэширован.
    /// Параметры: trackId, container, bitrate, isCached
    /// </summary>
    public event Action<string, string, int, bool>? OnFormatCached;

    public StreamCacheManager(LibraryService library)
    {
        _library = library;
        _ = Task.Run(CleanupOldCacheAsync);
    }

    #region Cache Completion

    /// <summary>
    /// Вызывается когда трек полностью закэширован.
    /// НЕ копирует в Downloads автоматически (если не включено AutoSaveToDownloads).
    /// </summary>
    /// <param name="cacheId">ID кэша (может включать формат: trackId_container_bitrate).</param>
    /// <param name="originalTrackId">Оригинальный ID трека (без суффикса формата).</param>
    public void TriggerCacheCompleted(string cacheId, string? originalTrackId = null)
    {
        string trackId = originalTrackId ?? ExtractTrackId(cacheId);
        _fullyCachedCache[cacheId] = true;

        Task.Run(async () =>
        {
            try
            {
                var meta = TryGetMetadata(cacheId);
                string container = meta?.Container ?? "";
                int bitrate = meta?.Bitrate ?? 0;

                // Обновляем трек — помечаем как закэшированный
                var track = await _library.GetTrackAsync(trackId);
                if (track != null)
                {
                    track.MarkAsCached(container, bitrate);
                    await _library.AddOrUpdateTrackAsync(track);
                    Log.Info($"[StreamCache] ✓ {trackId} cached: {container}/{bitrate}kbps");
                }

                // Если включено автосохранение — копируем в Downloads
                if (_library.Settings.Storage.AutoSaveToDownloads)
                {
                    await PromoteToDownloadsAsync(cacheId, trackId);
                }

                // Уведомляем UI
                NotifyFormatCached(trackId, container, bitrate, true);
            }
            catch (Exception ex)
            {
                Log.Error($"[StreamCache] Cache completed error for {trackId}: {ex.Message}");
            }
        });
    }

    #endregion

    #region Promote to Downloads

    /// <summary>
    /// Копирует файл из кэша в Downloads.
    /// Вызывается по запросу пользователя или автоматически (если AutoSaveToDownloads).
    /// </summary>
    /// <param name="cacheId">ID кэша.</param>
    /// <param name="originalTrackId">Оригинальный ID трека.</param>
    /// <returns>True если успешно скопировано.</returns>
    public async Task<bool> PromoteToDownloadsAsync(string cacheId, string? originalTrackId = null)
    {
        string trackId = originalTrackId ?? ExtractTrackId(cacheId);

        if (_promotedCache.TryGetValue(cacheId, out bool done) && done)
        {
            Log.Debug($"[CachePromote] Already promoted: {cacheId}");
            return true;
        }

        if (!await _promoteLock.WaitAsync(100))
            return false;

        try
        {
            // Повторная проверка под локом
            if (_promotedCache.TryGetValue(cacheId, out done) && done)
                return true;

            var track = await _library.GetTrackAsync(trackId);
            if (track == null)
            {
                Log.Warn($"[CachePromote] Track not found: {trackId}");
                return false;
            }

            // Если уже скачан — ничего не делаем
            if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
            {
                _promotedCache[cacheId] = true;
                return true;
            }

            var meta = TryGetMetadata(cacheId);
            if (meta == null)
            {
                Log.Warn($"[CachePromote] No metadata for: {cacheId}");
                return false;
            }

            var cachePath = GetCachePath(cacheId);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[CachePromote] Cache file not found: {cachePath}");
                return false;
            }

            var ranges = RangeMap.Deserialize(meta.RangesJson);
            if (!ranges.IsFullyDownloaded(meta.ContentLength))
            {
                Log.Warn($"[CachePromote] Not fully cached: {cacheId}");
                return false;
            }

            // Формируем путь назначения
            string ext = !string.IsNullOrEmpty(meta.Container) ? meta.Container : "m4a";
            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string destPath = Path.Combine(G.Folder.Downloads, safeName);

            // Проверяем существующий файл
            if (File.Exists(destPath))
            {
                var info = new FileInfo(destPath);
                if (info.Length == meta.ContentLength)
                {
                    // Файл уже есть и совпадает по размеру
                    track.MarkAsDownloaded(destPath, meta.Container, meta.Bitrate);
                    await _library.AddOrUpdateTrackAsync(track);
                    _promotedCache[cacheId] = true;
                    Log.Debug($"[CachePromote] File already exists: {safeName}");
                    return true;
                }

                // Файл есть но другой размер — добавляем битрейт к имени
                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{meta.Bitrate}kbps.{ext}");
            }

            Log.Info($"[CachePromote] Saving: {Path.GetFileName(destPath)}");
            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, meta.Container, meta.Bitrate);
            await _library.AddOrUpdateTrackAsync(track);

            _promotedCache[cacheId] = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachePromote] Error: {ex.Message}");
            return false;
        }
        finally
        {
            _promoteLock.Release();
        }
    }

    /// <summary>
    /// Экспортирует трек из кэша в Downloads по ID трека.
    /// Используется из UI (команда "Сохранить в папку").
    /// </summary>
    /// <param name="trackId">ID трека.</param>
    /// <returns>True если успешно.</returns>
    public async Task<bool> ExportTrackToDownloadsAsync(string trackId)
    {
        if (IsFullyCached(trackId))
        {
            return await PromoteToDownloadsAsync(trackId, trackId);
        }

        Log.Warn($"[StreamCache] Track {trackId} not fully cached, cannot export");
        return false;
    }

    #endregion

    #region Cache Status

    /// <summary>
    /// Проверяет, полностью ли закэширован файл.
    /// </summary>
    public bool IsFullyCached(string cacheId)
    {
        // Быстрая проверка из памяти
        if (_fullyCachedCache.TryGetValue(cacheId, out bool cached) && cached)
            return true;

        // Проверка с диска
        var meta = TryGetMetadata(cacheId);
        if (meta == null) return false;

        var cachePath = GetCachePath(cacheId);
        if (!File.Exists(cachePath)) return false;

        var ranges = RangeMap.Deserialize(meta.RangesJson);
        bool isFull = ranges.IsFullyDownloaded(meta.ContentLength);

        if (isFull)
            _fullyCachedCache[cacheId] = true;

        return isFull;
    }

    /// <summary>
    /// Проверяет, был ли кэш уже промоутнут в Downloads.
    /// </summary>
    public bool IsPromoted(string cacheId) =>
        _promotedCache.TryGetValue(cacheId, out bool promoted) && promoted;

    /// <summary>
    /// Проверяет, скачан ли конкретный формат.
    /// </summary>
    public bool IsFormatCached(string trackId, string container, int bitrate)
    {
        // Проверяем основной кэш
        if (IsFullyCached(trackId))
        {
            var meta = TryGetMetadata(trackId);
            if (meta != null &&
                string.Equals(meta.Container, container, StringComparison.OrdinalIgnoreCase) &&
                meta.Bitrate == bitrate)
                return true;
        }

        // Проверяем кэш с суффиксом формата
        string cacheId = $"{trackId}_{container}_{bitrate}";
        return IsFullyCached(cacheId);
    }

    /// <summary>
    /// Возвращает список закэшированных форматов для трека.
    /// </summary>
    public List<(string Container, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(string, int)>();

        // Проверяем основной кэш
        var baseMeta = TryGetMetadata(trackId);
        if (baseMeta != null && !string.IsNullOrEmpty(baseMeta.Container))
        {
            var ranges = RangeMap.Deserialize(baseMeta.RangesJson);
            if (ranges.IsFullyDownloaded(baseMeta.ContentLength))
                result.Add((baseMeta.Container, baseMeta.Bitrate));
        }

        // Можно добавить сканирование форматов с суффиксами если нужно

        return result;
    }

    /// <summary>
    /// Обновляет IsCached для треков на основе состояния кэша.
    /// Вызывается при загрузке списка треков.
    /// </summary>
    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        foreach (var track in tracks)
        {
            if (track.IsDownloaded) continue; // Скачанные не трогаем

            bool isCached = IsFullyCached(track.Id);
            if (isCached != track.IsCached)
            {
                track.IsCached = isCached;

                if (isCached)
                {
                    var meta = TryGetMetadata(track.Id);
                    if (meta != null)
                    {
                        if (!string.IsNullOrEmpty(meta.Container))
                            track.PreferredContainer = meta.Container;
                        if (meta.Bitrate > 0)
                            track.PreferredBitrate = meta.Bitrate;
                    }
                }
            }
        }
    }

    #endregion

    #region Metadata Operations

    public static string GetCachePath(string cacheId)
    {
        var safeId = GetSafeFileName(cacheId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.cache");
    }

    public static string GetMetaPath(string cacheId)
    {
        var safeId = GetSafeFileName(cacheId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.meta");
    }

    public static StreamCacheMetadata? TryGetMetadata(string cacheId)
    {
        var metaPath = GetMetaPath(cacheId);
        if (!File.Exists(metaPath)) return null;
        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize(json, AppJsonContext.DefaultCompact.StreamCacheMetadata);
        }
        catch { return null; }
    }

    public static StreamCacheMetadata LoadOrCreateMetadata(string cacheId, string url, long contentLength)
    {
        var meta = TryGetMetadata(cacheId);

        if (meta != null && meta.ContentLength == contentLength)
        {
            meta.LastAccessedAt = DateTime.UtcNow;
            meta.SourceUrl = url;
            SaveMetadata(cacheId, meta);
            return meta;
        }

        // Сохраняем codec/bitrate из старых метаданных если были
        string existingCodec = meta?.Codec ?? "";
        int existingBitrate = meta?.Bitrate ?? 0;
        string existingContainer = meta?.Container ?? "";

        var newMeta = new StreamCacheMetadata
        {
            TrackId = cacheId,
            SourceUrl = url,
            ContentLength = contentLength,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            RangesJson = "[]",
            Codec = existingCodec,
            Bitrate = existingBitrate,
            Container = existingContainer
        };

        var cachePath = GetCachePath(cacheId);
        if (File.Exists(cachePath))
            try { File.Delete(cachePath); } catch { }

        SaveMetadata(cacheId, newMeta);
        Log.Debug($"[StreamCache] Created metadata for {cacheId}: {contentLength} bytes");

        return newMeta;
    }

    public static void SaveMetadata(string cacheId, StreamCacheMetadata meta)
    {
        try
        {
            var metaPath = GetMetaPath(cacheId);
            var json = JsonSerializer.Serialize(meta, AppJsonContext.DefaultCompact.StreamCacheMetadata);
            File.WriteAllText(metaPath, json);
        }
        catch { }
    }

    public static void UpdateRanges(string cacheId, RangeMap ranges)
    {
        var meta = TryGetMetadata(cacheId);
        if (meta != null)
        {
            meta.RangesJson = ranges.Serialize();
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(cacheId, meta);
        }
    }

    public static void UpdateStreamInfo(string cacheId, string codec, int bitrate, string container)
    {
        var meta = TryGetMetadata(cacheId);

        if (meta == null)
        {
            // Создаём новые метаданные если их нет
            meta = new StreamCacheMetadata
            {
                TrackId = cacheId,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                RangesJson = "[]"
            };
        }

        meta.Codec = codec;
        meta.Bitrate = bitrate;
        meta.Container = container;
        meta.LastAccessedAt = DateTime.UtcNow;

        SaveMetadata(cacheId, meta);

        Log.Debug($"[StreamCache] UpdateStreamInfo: {cacheId} → {codec}/{bitrate}kbps/{container}");
    }

    #endregion

    #region Statistics & Cleanup

    /// <summary>
    /// Возвращает статистику StreamCache.
    /// </summary>
    public static (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var cacheDir = G.Folder.StreamCache;
            if (!Directory.Exists(cacheDir)) return (0, 0);

            var files = Directory.GetFiles(cacheDir, "*.cache");
            long totalSize = files.Sum(f => new FileInfo(f).Length);

            return (files.Length, totalSize / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Возвращает статистику папки Downloads.
    /// </summary>
    public static (int FileCount, long SizeMb) GetDownloadsStats()
    {
        try
        {
            var downloadsDir = G.Folder.Downloads;
            if (!Directory.Exists(downloadsDir)) return (0, 0);

            var files = Directory.GetFiles(downloadsDir);
            long totalSize = files.Sum(f => new FileInfo(f).Length);

            return (files.Length, totalSize / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Очищает весь StreamCache.
    /// </summary>
    public async Task ClearAllAsync()
    {
        await _cleanupLock.WaitAsync();
        try
        {
            var cacheDir = G.Folder.StreamCache;
            if (!Directory.Exists(cacheDir)) return;

            foreach (var file in Directory.GetFiles(cacheDir))
            {
                try { File.Delete(file); } catch { }
            }

            _fullyCachedCache.Clear();
            _promotedCache.Clear();

            Log.Info("[StreamCache] All cache cleared");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    /// <summary>
    /// Очищает папку Downloads.
    /// </summary>
    public async Task ClearDownloadsAsync()
    {
        try
        {
            var downloadsDir = G.Folder.Downloads;
            if (!Directory.Exists(downloadsDir)) return;

            foreach (var file in Directory.GetFiles(downloadsDir))
            {
                try { File.Delete(file); } catch { }
            }

            Log.Info("[StreamCache] All downloads cleared");
        }
        catch (Exception ex)
        {
            Log.Error($"[StreamCache] ClearDownloads error: {ex.Message}");
        }
    }

    /// <summary>
    /// Автоматическая очистка старых файлов кэша.
    /// </summary>
    private async Task CleanupOldCacheAsync()
    {
        if (!await _cleanupLock.WaitAsync(0)) return;

        try
        {
            var cacheDir = G.Folder.StreamCache;
            if (!Directory.Exists(cacheDir)) return;

            var files = Directory.GetFiles(cacheDir, "*.cache")
                .Select(f => new FileInfo(f)).ToList();

            long totalSize = files.Sum(f => f.Length);
            long maxBytes = (long)_library.Settings.Storage.AudioCacheLimitMb * 1024 * 1024;

            if (totalSize <= maxBytes) return;

            Log.Info($"[StreamCache] Cleanup: {totalSize / 1024 / 1024}MB > {maxBytes / 1024 / 1024}MB limit");

            // Сортируем по времени последнего доступа
            var toDelete = files
                .OrderBy(f => f.LastAccessTime)
                .TakeWhile(f =>
                {
                    if (totalSize <= maxBytes * 0.7) return false;
                    totalSize -= f.Length;
                    return true;
                })
                .ToList();

            foreach (var f in toDelete)
            {
                try
                {
                    f.Delete();
                    var metaPath = Path.ChangeExtension(f.FullName, ".meta");
                    if (File.Exists(metaPath)) File.Delete(metaPath);
                }
                catch { }
            }

            Log.Info($"[StreamCache] Deleted {toDelete.Count} old files");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    #endregion

    #region Helpers

    private void NotifyFormatCached(string trackId, string container, int bitrate, bool isCached)
    {
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                OnFormatCached?.Invoke(trackId, container, bitrate, isCached);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    OnFormatCached?.Invoke(trackId, container, bitrate, isCached));
        }
        catch (Exception ex)
        {
            Log.Warn($"[StreamCache] NotifyFormatCached error: {ex.Message}");
        }
    }

    private static string ExtractTrackId(string cacheId)
    {
        var parts = cacheId.Split('_');
        if (parts.Length >= 3 && int.TryParse(parts[^1], out _) && IsKnownContainer(parts[^2]))
            return string.Join('_', parts[..^2]);
        return cacheId;
    }

    private static bool IsKnownContainer(string s) =>
        s is "m4a" or "opus" or "webm" or "mp3" or "ogg" or "aac" or "flac" or "mp4";

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    private static string GetSafeFileName(string id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(bytes)[..32];
    }

    public void Dispose()
    {
        _cleanupLock.Dispose();
        _promoteLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}