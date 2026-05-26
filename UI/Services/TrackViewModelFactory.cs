using System.Buffers;
using System.Collections.Concurrent;
using LMP.UI.Features.Shared;

namespace LMP.UI.Services;

/// <summary>
/// Фабрика для создания TrackItemViewModel.
/// Использует TrackRegistry для Identity Map и кэширует ViewModel-и.
/// </summary>
public class TrackViewModelFactory
{
    private readonly LibraryService _library;
    private readonly DialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly TrackRegistry _registry;

    // Кэш для "общих" VM (используются в Home, Search, Library, Playlist)
    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    public TrackViewModelFactory(
        LibraryService library,
        DialogService dialog,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        TrackRegistry registry)
    {
        _library = library;
        _dialog = dialog;
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
            _dialog,
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
        // ArrayPool устраняет аллокацию List<string> на каждый вызов очистки.
        // _cache.Count может измениться — берём с запасом через Count + 16.
        var deadKeys = ArrayPool<string>.Shared.Rent(_cache.Count + 16);
        int deadCount = 0;

        try
        {
            foreach (var kvp in _cache)
            {
                if (!kvp.Value.TryGetTarget(out var target) || target.IsDisposed)
                {
                    if (deadCount < deadKeys.Length)
                        deadKeys[deadCount++] = kvp.Key;
                }
            }

            for (int i = 0; i < deadCount; i++)
                _cache.TryRemove(deadKeys[i], out _);

            if (deadCount > 0)
            {
                Log.Debug($"[TrackFactory] Cleaned {deadCount} dead references.");
            }

            return deadCount;
        }
        finally
        {
            ArrayPool<string>.Shared.Return(deadKeys, clearArray: true);
        }
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
    }
}