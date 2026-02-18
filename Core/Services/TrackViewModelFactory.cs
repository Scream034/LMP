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
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly TrackRegistry _registry;

    // Кэш для "общих" VM (используются в Home, Search, Library, Playlist)
    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    public TrackViewModelFactory(
        LibraryService library,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        TrackRegistry registry)
    {
        _library = library;
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _registry = registry;
    }

    /// <summary>
    /// Возвращает существующую (из кэша) или создаёт новую "общую" ViewModel для трека.
    /// Эти VM переиспользуются между страницами (Home, Search, Library).
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // Identity Map — получаем канонический экземпляр данных
        var canonical = _registry.RegisterOrUpdate(track);

        // Проверяем кэш
        if (_cache.TryGetValue(canonical.Id, out var weakRef) &&
            weakRef.TryGetTarget(out var existing) &&
            !existing.IsDisposed)
        {
            existing.UpdatePlayAction(playAction);

            // Сбрасываем контекст, так как эта VM используется в общих списках
            existing.IsQueueContext = false;
            // existing.IsPlaylistContext - это свойство управляется извне (PlaylistViewModel), не трогаем его тут жестко

            if (!ReferenceEquals(existing.Track, canonical))
            {
                Log.Warn($"[TrackFactory] VM track mismatch for {canonical.Id}, recreating");
                // Если данные рассинхронизировались (редкий кейс), создаем заново
                existing.Dispose();
            }
            else
            {
                return existing;
            }
        }

        // Создаём новую VM
        var vm = CreateVmInstance(canonical, playAction);

        _cache[canonical.Id] = new WeakReference<TrackItemViewModel>(vm);

        MemoryDiagnostics.TrackInstance("TrackVM.Created(Shared)");

        return vm;
    }

    /// <summary>
    /// Создаёт СПЕЦИАЛЬНУЮ ViewModel для списка очереди.
    /// ВАЖНО: Эта VM НЕ кэшируется в общем словаре, чтобы QueueViewModel могла 
    /// безопасно вызывать Dispose() при закрытии, не ломая остальные экраны.
    /// </summary>
    public TrackItemViewModel CreateForQueue(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // Используем те же канонические данные
        var canonical = _registry.RegisterOrUpdate(track);

        // Создаем ИЗОЛИРОВАННЫЙ экземпляр
        var vm = CreateVmInstance(canonical, playAction);

        vm.IsQueueContext = true;

        MemoryDiagnostics.TrackInstance("TrackVM.Created(Queue)");

        // НЕ добавляем в _cache!
        return vm;
    }

    private TrackItemViewModel CreateVmInstance(TrackInfo track, Action<TrackInfo>? playAction)
    {
        return new TrackItemViewModel(
            track,
            _audio,
            _downloads,
            _manager,
            _library,
            playAction);
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
        var deadKeys = new List<string>();

        foreach (var kvp in _cache)
        {
            if (!kvp.Value.TryGetTarget(out var target) || target.IsDisposed)
            {
                deadKeys.Add(kvp.Key);
            }
        }

        foreach (var key in deadKeys)
        {
            _cache.TryRemove(key, out _);
        }

        if (deadKeys.Count > 0)
        {
            MemoryDiagnostics.SetBytes("TrackVM.CacheSize", _cache.Count);
            Log.Debug($"[TrackFactory] Cleaned {deadKeys.Count} dead references.");
        }

        return deadKeys.Count;
    }


    /// <summary>
    /// Полная очистка с dispose всех VM.
    /// Вызывается только при закрытии приложения или полном сбросе.
    /// </summary>
    public void ClearWithDispose()
    {
        // Собираем все живые VM
        var toDispose = new List<TrackItemViewModel>();

        foreach (var kvp in _cache)
        {
            if (kvp.Value.TryGetTarget(out var vm) && !vm.IsDisposed)
            {
                toDispose.Add(vm);
            }
        }

        _cache.Clear();

        // Диспозим отложенно
        Task.Run(async () =>
        {
            await Task.Delay(100);
            foreach (var vm in toDispose)
            {
                try { vm.Dispose(); } catch { }
            }
            Log.Info($"[TrackFactory] Disposed {toDispose.Count} VMs on clear.");
        });
    }

    /// <summary>
    /// Мягкая очистка — только удаляем ссылки, не диспозим.
    /// Используется при навигации.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        MemoryDiagnostics.SetBytes("TrackVM.CacheSize", 0);
    }
}