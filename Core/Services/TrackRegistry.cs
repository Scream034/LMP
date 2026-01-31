// Core/Services/TrackRegistry.cs
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
    // Weak references for transient tracks (search results)
    private readonly ConcurrentDictionary<string, WeakReference<TrackInfo>> _cache = new();

    // Strong references for important tracks (library items)
    private readonly ConcurrentDictionary<string, TrackInfo> _pinned = new();

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

        // Check pinned first (faster)
        if (_pinned.TryGetValue(incoming.Id, out var pinned))
        {
            pinned.UpdateMetadata(incoming);
            return pinned;
        }

        // Check weak cache
        if (_cache.TryGetValue(incoming.Id, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            cached.UpdateMetadata(incoming);
            UpdatePinStatus(cached);
            return cached;
        }

        // Register as new
        _cache[incoming.Id] = new WeakReference<TrackInfo>(incoming);
        UpdatePinStatus(incoming);
        return incoming;
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

        // Load InPlaylists
        if (_playlists != null)
        {
            fromDb.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(id, ct);
        }

        return RegisterOrUpdate(fromDb);
    }

    /// <summary>
    /// Batch preload tracks into cache.
    /// </summary>
    public async Task PreloadAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (_repository == null) return;

        var toLoad = ids.Where(id => TryGet(id) == null).ToList();
        if (toLoad.Count == 0) return;

        var loaded = await _repository.GetByIdsAsync(toLoad, ct);

        foreach (var track in loaded)
        {
            if (_playlists != null)
            {
                track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, ct);
            }
            RegisterOrUpdate(track);
        }
    }

    /// <summary>
    /// Updates pinning status based on track importance.
    /// </summary>
    public void UpdatePinStatus(TrackInfo track)
    {
        bool shouldPin = track.IsLiked ||
                        track.IsDownloaded ||
                        track.IsDisliked ||
                        track.InPlaylists.Count > 0;

        if (shouldPin)
        {
            _pinned.TryAdd(track.Id, track);
        }
        else
        {
            _pinned.TryRemove(track.Id, out _);
        }
    }

    /// <summary>
    /// Hydrates cache with important tracks from database.
    /// Called at startup.
    /// </summary>
    public async Task HydrateAsync(CancellationToken ct = default)
    {
        if (_repository == null) return;

        Log.Info("[TrackRegistry] Hydrating from database...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Load liked tracks
        var liked = await _repository.GetLikedAsync(500, 0, ct);
        foreach (var t in liked)
        {
            if (_playlists != null) t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
            RegisterOrUpdate(t);
        }

        // Load downloaded tracks
        var downloaded = await _repository.GetDownloadedAsync(500, 0, ct);
        foreach (var t in downloaded)
        {
            if (_playlists != null) t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
            RegisterOrUpdate(t);
        }

        // Load recent tracks
        var recent = await _repository.GetRecentlyPlayedAsync(100, ct);
        foreach (var t in recent)
        {
            if (_playlists != null) t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
            RegisterOrUpdate(t);
        }

        sw.Stop();
        Log.Info($"[TrackRegistry] Hydrated {_pinned.Count} tracks in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Persists all pinned tracks to database.
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
    /// Cleans dead weak references. Call periodically.
    /// </summary>
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
        _cache.Clear();
        _pinned.Clear();
    }
}