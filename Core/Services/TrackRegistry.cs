using System.Collections.Concurrent;
using LMP.Core.Data.Repositories;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Identity Map / L1 Cache for TrackInfo objects.
/// Combines in-memory caching with database backing.
/// </summary>
public sealed class TrackRegistry
{
    private readonly ConcurrentDictionary<string, WeakReference<TrackInfo>> _cache = new();
    private readonly ConcurrentDictionary<string, TrackInfo> _pinned = new();

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
    /// </summary>
    public TrackInfo RegisterOrUpdate(TrackInfo incoming)
    {
        if (string.IsNullOrEmpty(incoming.Id)) return incoming;

        TrackInfo result;

        // 1. Check pinned (Strong Reference)
        if (_pinned.TryGetValue(incoming.Id, out var pinned))
        {
            pinned.UpdateMetadata(incoming);
            result = pinned;
        }
        // 2. Check weak cache
        else if (_cache.TryGetValue(incoming.Id, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            cached.UpdateMetadata(incoming);
            result = cached;
        }
        // 3. New registration
        else
        {
            _cache[incoming.Id] = new WeakReference<TrackInfo>(incoming);
            result = incoming;
        }

        // Обновляем статус кэширования, если менеджер доступен
        if (CacheManager != null && !result.IsDownloaded && !result.IsCached)
        {
            // Простая проверка без тяжелых операций, так как этот метод вызывается часто
            if (CacheManager.IsFullyCached(result.Id))
            {
                var meta = StreamCacheManager.TryGetMetadata(result.Id);
                if (meta != null)
                {
                    result.MarkAsCached(meta.Container, meta.Bitrate);
                }
                else
                {
                    result.IsCached = true;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets track from L1 cache only (no DB access).
    /// </summary>
    public TrackInfo? TryGet(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_pinned.TryGetValue(id, out var pinned)) return pinned;
        if (_cache.TryGetValue(id, out var weak) && weak.TryGetTarget(out var cached)) return cached;

        return null;
    }

    /// <summary>
    /// Gets track from cache or loads from database.
    /// </summary>
    public async Task<TrackInfo?> GetOrLoadAsync(string id, CancellationToken ct = default)
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

        // Автоматически пиним при загрузке из БД, если нужно
        var canonical = RegisterOrUpdate(fromDb);
        UpdatePinStatusInternal(canonical); 
        
        return canonical;
    }

    /// <summary>
    /// Batch preload tracks into cache.
    /// </summary>
    public async Task PreloadAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (_repository == null) return;

        var toLoad = ids.Where(id => TryGet(id) == null).Distinct().ToList();
        if (toLoad.Count == 0) return;

        var loaded = await _repository.GetByIdsAsync(toLoad, ct);

        foreach (var track in loaded)
        {
            if (_playlists != null)
            {
                track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, ct);
            }
            var t = RegisterOrUpdate(track);
            UpdatePinStatusInternal(t);
        }
    }

    /// <summary>
    /// Updates pinning status based on track importance.
    /// Returns true if track was added to pinned.
    /// </summary>
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
                MemoryDiagnostics.TrackBytes("TrackRegistry.Pinned", 1024); // Примерный размер
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

    public void UpdatePinStatus(TrackInfo track)
    {
        UpdatePinStatusInternal(track);
    }

    public async Task HydrateAsync(CancellationToken ct = default)
    {
        if (_repository == null) return;

        Log.Info("[TrackRegistry] Hydrating from database...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var allLoaded = new List<TrackInfo>();

        var liked = await _repository.GetLikedAsync(1000, 0, ct);
        allLoaded.AddRange(liked);

        var downloaded = await _repository.GetDownloadedAsync(1000, 0, ct);
        allLoaded.AddRange(downloaded);

        // Recent tracks might not need to be pinned permanently, but useful to have in cache
        var recent = await _repository.GetRecentlyPlayedAsync(100, ct);
        allLoaded.AddRange(recent);

        foreach (var t in allLoaded)
        {
            if (_playlists != null) t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
            
            var canonical = RegisterOrUpdate(t);
            UpdatePinStatusInternal(canonical);
        }

        // Обновляем статусы кэша для всех запиненных треков
        CacheManager?.HydrateCacheStatus(_pinned.Values);

        sw.Stop();
        Log.Info($"[TrackRegistry] Hydrated {_pinned.Count} pinned tracks in {sw.ElapsedMilliseconds}ms");
        
        MemoryDiagnostics.SetBytes("TrackRegistry.Pinned", _pinned.Count * 1024);
    }

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

    public int CleanupDeadReferences()
    {
        var dead = _cache
            .Where(kvp => !kvp.Value.TryGetTarget(out _) && !_pinned.ContainsKey(kvp.Key))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in dead)
            _cache.TryRemove(key, out _);

        return dead.Count;
    }

    public void Clear()
    {
        MemoryDiagnostics.SetBytes("TrackRegistry.Pinned", 0);
        _cache.Clear();
        _pinned.Clear();
    }
}