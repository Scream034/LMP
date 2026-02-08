using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LMP.Core.Data.Repositories;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Identity Map / L1 Cache for TrackInfo objects.
/// минимизированы аллокации, batch-операции, ValueTask для hot paths.
/// </summary>
public sealed class TrackRegistry
{
    // StringComparer.Ordinal для лучшей производительности
    private readonly ConcurrentDictionary<string, WeakReference<TrackInfo>> _cache = 
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TrackInfo> _pinned = 
        new(StringComparer.Ordinal);

    public StreamCacheManager? CacheManager { get; set; }

    private readonly ITrackRepository? _repository;
    private readonly IPlaylistRepository? _playlists;

    public TrackRegistry(ITrackRepository? repository = null, IPlaylistRepository? playlists = null)
    {
        _repository = repository;
        _playlists = playlists;
    }

    /// <summary>
    /// Registers or updates a track. Returns the canonical instance.
    /// минимизированы проверки и аллокации.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo RegisterOrUpdate(TrackInfo incoming)
    {
        if (string.IsNullOrEmpty(incoming.Id)) return incoming;

        // 1. Check pinned (Strong Reference) — fastest path
        if (_pinned.TryGetValue(incoming.Id, out var pinned))
        {
            pinned.UpdateMetadata(incoming);
            return pinned;
        }

        // 2. Check weak cache
        if (_cache.TryGetValue(incoming.Id, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            cached.UpdateMetadata(incoming);
            return cached;
        }

        // 3. New registration
        _cache[incoming.Id] = new WeakReference<TrackInfo>(incoming);

        // Обновляем статус кэширования БЕЗ синхронных IO операций
        if (CacheManager != null && !incoming.IsDownloaded && !incoming.IsCached)
        {
            // Быстрая проверка из памяти
            if (CacheManager.IsFullyCached(incoming.Id))
            {
                var meta = StreamCacheManager.TryGetMetadata(incoming.Id);
                if (meta != null)
                {
                    incoming.MarkAsCached(meta.Container, meta.Bitrate);
                }
                else
                {
                    incoming.IsCached = true;
                }
            }
        }

        return incoming;
    }

    /// <summary>
    /// Gets track from L1 cache only (no DB access).
    /// ValueTask для sync-path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo? TryGet(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_pinned.TryGetValue(id, out var pinned)) return pinned;
        if (_cache.TryGetValue(id, out var weak) && weak.TryGetTarget(out var cached)) return cached;

        return null;
    }

    /// <summary>
    /// Gets track from cache or loads from database.
    /// ValueTask для fast path (cache hit).
    /// </summary>
    public async ValueTask<TrackInfo?> GetOrLoadAsync(string id, CancellationToken ct = default)
    {
        var cached = TryGet(id);
        if (cached != null) return cached;

        if (_repository == null) return null;

        var fromDb = await _repository.GetByIdAsync(id, ct);
        if (fromDb == null) return null;

        if (_playlists != null)
        {
            fromDb.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(id, ct);
        }

        var canonical = RegisterOrUpdate(fromDb);
        UpdatePinStatusInternal(canonical);

        return canonical;
    }

    /// <summary>
    /// Batch preload tracks into cache.
    /// один SQL-запрос вместо N.
    /// </summary>
    public async Task PreloadAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (_repository == null) return;

        // используем HashSet для быстрой проверки дубликатов
        var toLoadSet = new HashSet<string>(StringComparer.Ordinal);
        
        foreach (var id in ids)
        {
            if (TryGet(id) == null)
                toLoadSet.Add(id);
        }

        if (toLoadSet.Count == 0) return;

        var loaded = await _repository.GetByIdsAsync(toLoadSet, ct);
        if (loaded.Count == 0) return;

        // Batch загрузка плейлистов — ОДИН SQL запрос
        Dictionary<string, HashSet<string>>? playlistsMap = null;
        if (_playlists != null)
        {
            var loadedIds = new List<string>(loaded.Count);
            for (int i = 0; i < loaded.Count; i++)
                loadedIds.Add(loaded[i].Id);
            
            playlistsMap = await _playlists.GetPlaylistsForTracksAsync(loadedIds, ct);
        }

        for (int i = 0; i < loaded.Count; i++)
        {
            var track = loaded[i];
            
            if (playlistsMap != null && playlistsMap.TryGetValue(track.Id, out var pls))
            {
                track.InPlaylists = pls;
            }

            var canonical = RegisterOrUpdate(track);
            UpdatePinStatusInternal(canonical);
        }
    }

    /// <summary>
    /// Updates pinning status based on track importance.
    /// инлайнинг и минимизация условий.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool UpdatePinStatusInternal(TrackInfo track)
    {
        bool shouldPin = track.IsLiked ||
                        track.IsDownloaded ||
                        track.IsDisliked ||
                        track.InPlaylists.Count > 0;

        if (shouldPin)
        {
            if (_pinned.TryAdd(track.Id, track))
            {
                MemoryDiagnostics.TrackBytes("TrackRegistry.Pinned", 1024);
                return true;
            }
        }
        else
        {
            if (_pinned.TryRemove(track.Id, out _))
            {
                MemoryDiagnostics.UntrackBytes("TrackRegistry.Pinned", 1024);
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdatePinStatus(TrackInfo track)
    {
        UpdatePinStatusInternal(track);
    }

    /// <summary>
    /// Hydrates registry from database.
    /// batch загрузка плейлистов одним запросом.
    /// </summary>
    public async Task HydrateAsync(CancellationToken ct = default)
    {
        if (_repository == null) return;

        Log.Info("[TrackRegistry] Hydrating from database...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // pre-allocate с запасом
        var allLoaded = new List<TrackInfo>(2200);

        var liked = await _repository.GetLikedAsync(1000, 0, ct);
        allLoaded.AddRange(liked);

        var downloaded = await _repository.GetDownloadedAsync(1000, 0, ct);
        allLoaded.AddRange(downloaded);

        var recent = await _repository.GetRecentlyPlayedAsync(100, ct);
        allLoaded.AddRange(recent);

        // КРИТИЧНАЯ ОДИН batch-запрос для всех плейлистов
        var allIds = new HashSet<string>(allLoaded.Count, StringComparer.Ordinal);
        for (int i = 0; i < allLoaded.Count; i++)
            allIds.Add(allLoaded[i].Id);

        Dictionary<string, HashSet<string>>? playlistsMap = null;

        if (_playlists != null && allIds.Count > 0)
        {
            playlistsMap = await _playlists.GetPlaylistsForTracksAsync(allIds, ct);
        }

        for (int i = 0; i < allLoaded.Count; i++)
        {
            var t = allLoaded[i];
            
            if (playlistsMap != null && playlistsMap.TryGetValue(t.Id, out var pls))
            {
                t.InPlaylists = pls;
            }

            var canonical = RegisterOrUpdate(t);
            UpdatePinStatusInternal(canonical);
        }

        CacheManager?.HydrateCacheStatus(_pinned.Values);

        sw.Stop();
        Log.Info($"[TrackRegistry] Hydrated {_pinned.Count} pinned tracks in {sw.ElapsedMilliseconds}ms");

        MemoryDiagnostics.SetBytes("TrackRegistry.Pinned", _pinned.Count * 1024);
    }

    /// <summary>
    /// Flushes pinned tracks to database.
    /// batch upsert вместо N запросов.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_repository == null) return;

        var tracks = _pinned.Values.ToList();
        if (tracks.Count == 0) return;

        try
        {
            await _repository.UpsertBatchAsync(tracks, ct);
            Log.Info($"[TrackRegistry] Flushed {tracks.Count} tracks to database");
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackRegistry] Flush failed: {ex.Message}");
        }
    }

    public IEnumerable<TrackInfo> GetPinnedTracks() => _pinned.Values;

    /// <summary>
    /// Cleans up dead weak references.
    /// используем ArrayPool для буфера удаляемых ключей.
    /// </summary>
    public int CleanupDeadReferences()
    {
        // Оценка максимального размера — все кэшированные записи
        var maxDeadCount = _cache.Count;
        
        var deadKeysArray = ArrayPool<string>.Shared.Rent(maxDeadCount);
        int deadCount = 0;

        try
        {
            foreach (var kvp in _cache)
            {
                if (!kvp.Value.TryGetTarget(out _) && !_pinned.ContainsKey(kvp.Key))
                {
                    if (deadCount < deadKeysArray.Length)
                    {
                        deadKeysArray[deadCount++] = kvp.Key;
                    }
                }
            }

            for (int i = 0; i < deadCount; i++)
            {
                _cache.TryRemove(deadKeysArray[i], out _);
            }

            return deadCount;
        }
        finally
        {
            ArrayPool<string>.Shared.Return(deadKeysArray, clearArray: true);
        }
    }

    public void Clear()
    {
        MemoryDiagnostics.SetBytes("TrackRegistry.Pinned", 0);
        _cache.Clear();
        _pinned.Clear();
    }
}