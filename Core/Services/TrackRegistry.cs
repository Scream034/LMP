using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LMP.Core.Audio;
using LMP.Core.Audio.Cache;
using LMP.Core.Data.Repositories;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Identity Map / L1 Cache for TrackInfo objects.
/// минимизированы аллокации, batch-операции, ValueTask для hot paths.
/// </summary>
public sealed class TrackRegistry
{
    private readonly ConcurrentDictionary<string, WeakReference<TrackInfo>> _cache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TrackInfo> _pinned =
        new(StringComparer.Ordinal);

    private readonly ITrackRepository? _repository;
    private readonly IPlaylistRepository? _playlists;

    public TrackRegistry(ITrackRepository? repository = null, IPlaylistRepository? playlists = null)
    {
        _repository = repository;
        _playlists = playlists;
    }

    /// <summary>
    /// Получает AudioCacheManager из AudioSourceFactory (lazy, singleton).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AudioCacheManager? GetAudioCache() => AudioSourceFactory.GlobalCache;

    /// <summary>
    /// Registers or updates a track. Returns the canonical instance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo RegisterOrUpdate(TrackInfo incoming)
    {
        if (string.IsNullOrEmpty(incoming.Id)) return incoming;

        if (_pinned.TryGetValue(incoming.Id, out var pinned))
        {
            pinned.UpdateMetadata(incoming);
            return pinned;
        }

        if (_cache.TryGetValue(incoming.Id, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            cached.UpdateMetadata(incoming);
            return cached;
        }

        _cache[incoming.Id] = new WeakReference<TrackInfo>(incoming);

        // Обновляем статус кэширования из AudioCacheManager
        if (!incoming.IsDownloaded && !incoming.IsCached)
        {
            var audioCache = GetAudioCache();
            if (audioCache != null && audioCache.IsTrackFullyCached(incoming.Id))
            {
                var bestEntry = audioCache.FindBestCacheByTrackId(incoming.Id);
                if (bestEntry != null)
                    incoming.MarkAsCached(bestEntry.Format.ToString(), bestEntry.Bitrate);
                else
                    incoming.IsCached = true;
            }
        }

        return incoming;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo? TryGet(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_pinned.TryGetValue(id, out var pinned)) return pinned;
        if (_cache.TryGetValue(id, out var weak) && weak.TryGetTarget(out var cached)) return cached;

        return null;
    }

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

    public async Task PreloadAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (_repository == null) return;

        var toLoadSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            if (TryGet(id) == null)
                toLoadSet.Add(id);
        }

        if (toLoadSet.Count == 0) return;

        var loaded = await _repository.GetByIdsAsync(toLoadSet, ct);
        if (loaded.Count == 0) return;

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

    public async Task HydrateAsync(CancellationToken ct = default)
    {
        if (_repository == null) return;

        Log.Info("[TrackRegistry] Hydrating from database...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var allLoaded = new List<TrackInfo>(2200);

        var liked = await _repository.GetLikedAsync(1000, 0, ct);
        allLoaded.AddRange(liked);

        var downloaded = await _repository.GetDownloadedAsync(1000, 0, ct);
        allLoaded.AddRange(downloaded);

        var recent = await _repository.GetRecentlyPlayedAsync(100, ct);
        allLoaded.AddRange(recent);

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

        // Массовая гидрация кэш-статуса через AudioCacheManager
        var audioCache = GetAudioCache();
        audioCache?.HydrateCacheStatus(_pinned.Values);

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

    /// <summary>
    /// Подписывается на события AudioCacheManager.
    /// Вызывать после инициализации AudioSourceFactory.GlobalCache.
    /// </summary>
    public void SubscribeToCacheEvents()
    {
        var audioCache = GetAudioCache();
        if (audioCache == null)
        {
            Log.Warn("[TrackRegistry] AudioCache not available for event subscription");
            return;
        }

        audioCache.OnCacheCleared += HandleCacheCleared;
        audioCache.OnFormatCached += HandleFormatCached;

        Log.Info("[TrackRegistry] Subscribed to AudioCache events");
    }

    /// <summary>
    /// Вызывается когда весь кэш очищен — сбрасываем IsCached у всех треков.
    /// </summary>
    private void HandleCacheCleared()
    {
        int cleared = 0;

        // Сбрасываем у pinned (сильные ссылки — гарантированно живые)
        foreach (var track in _pinned.Values)
        {
            if (track.IsCached && !track.IsDownloaded)
            {
                track.ClearCacheStatus();
                cleared++;
            }
        }

        // Сбрасываем у обычного кэша (слабые ссылки)
        foreach (var weakRef in _cache.Values)
        {
            if (weakRef.TryGetTarget(out var track) && track.IsCached && !track.IsDownloaded)
            {
                // Не дублируем если уже обработали в pinned
                if (!_pinned.ContainsKey(track.Id))
                {
                    track.ClearCacheStatus();
                    cleared++;
                }
            }
        }

        Log.Info($"[TrackRegistry] Cache cleared: reset IsCached on {cleared} tracks");
    }

    /// <summary>
    /// Вызывается когда формат трека полностью закэширован — помечаем трек.
    /// </summary>
    private void HandleFormatCached(string trackId, string container, int bitrate, bool isDownloaded)
    {
        if (string.IsNullOrEmpty(trackId)) return;

        // Ищем трек в pinned
        if (_pinned.TryGetValue(trackId, out var pinned))
        {
            if (isDownloaded)
                pinned.MarkAsDownloaded(pinned.LocalPath ?? "", container, bitrate);
            else
                pinned.MarkAsCached(container, bitrate);

            Log.Debug($"[TrackRegistry] Marked pinned track {trackId} as cached ({container}/{bitrate}kbps)");
            return;
        }

        // Ищем в слабом кэше
        if (_cache.TryGetValue(trackId, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            if (isDownloaded)
                cached.MarkAsDownloaded(cached.LocalPath ?? "", container, bitrate);
            else
                cached.MarkAsCached(container, bitrate);

            Log.Debug($"[TrackRegistry] Marked cached track {trackId} as cached ({container}/{bitrate}kbps)");
        }
    }
}