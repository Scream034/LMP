using System.Collections.Concurrent;
using LMP.Core.Models;
using LMP.Features.Shared;

namespace LMP.Core.Services;

/// <summary>
/// Фабрика для создания TrackItemViewModel.
/// Использует TrackRegistry для Identity Map и кэширует ViewModel-и.
/// </summary>
public class TrackViewModelFactory
{
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly TrackRegistry _registry;
    private readonly StreamCacheManager _cacheManager;

    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    public TrackViewModelFactory(
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        TrackRegistry registry,
        StreamCacheManager cacheManager)
    {
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _registry = registry;
        _cacheManager = cacheManager;
    }

    /// <summary>
    /// Возвращает существующую или создаёт новую ViewModel для трека.
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // Identity Map — получаем канонический экземпляр
        var canonical = _registry.RegisterOrUpdate(track);

        // Проверяем кэш
        if (_cache.TryGetValue(canonical.Id, out var weakRef) &&
            weakRef.TryGetTarget(out var existing) &&
            !existing.IsDisposed)
        {
            existing.UpdatePlayAction(playAction);
            existing.IsQueueContext = false;

            if (!ReferenceEquals(existing.Track, canonical))
            {
                Log.Warn($"[TrackFactory] VM track mismatch for {canonical.Id}, recreating");
                existing.Dispose();
            }
            else
            {
                return existing;
            }
        }

        // Создаём новую VM
        var vm = new TrackItemViewModel(
            canonical,
            _audio,
            _downloads,
            _manager,
            _cacheManager,  // ← Передаём через DI
            playAction);

        _cache[canonical.Id] = new WeakReference<TrackItemViewModel>(vm);

        MemoryDiagnostics.TrackInstance("TrackVM.Created");

        return vm;
    }

    public TrackItemViewModel CreateForQueue(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        var vm = GetOrCreate(track, playAction);
        vm.IsQueueContext = true;
        return vm;
    }

    public TrackItemViewModel? TryGet(string trackId)
    {
        if (_cache.TryGetValue(trackId, out var weakRef) &&
            weakRef.TryGetTarget(out var vm) &&
            !vm.IsDisposed)
        {
            return vm;
        }
        return null;
    }

    public void TryRemove(string trackId)
    {
        _cache.TryRemove(trackId, out _);
    }

    public int CleanupCache()
    {
        var deadKeys = _cache
            .Where(kvp => !kvp.Value.TryGetTarget(out var target) || target.IsDisposed)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in deadKeys)
            _cache.TryRemove(key, out _);

        if (deadKeys.Count > 0)
        {
            // ТРЕКИНГ
            MemoryDiagnostics.SetBytes("TrackVM.CacheSize", _cache.Count);
            Log.Info($"[TrackFactory] Cleaned {deadKeys.Count} dead items.");
        }

        return deadKeys.Count;
    }

    public void Clear()
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.TryGetTarget(out var vm))
                vm.Dispose();
        }
        _cache.Clear();
    }
}