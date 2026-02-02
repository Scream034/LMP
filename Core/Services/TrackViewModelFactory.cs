using System.Collections.Concurrent;
using LMP.Core.Models;
using LMP.Features.Shared;

namespace LMP.Core.Services;

/// <summary>
/// Фабрика для создания TrackItemViewModel.
/// Использует кэширование на основе WeakReference.
/// </summary>
public class TrackViewModelFactory(
    AudioEngine audio,
    LibraryService library,
    DownloadService downloads,
    MusicLibraryManager manager)
{
    private readonly AudioEngine _audio = audio;
    private readonly LibraryService _library = library;
    private readonly DownloadService _downloads = downloads;
    private readonly MusicLibraryManager _manager = manager;

    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    /// <summary>
    /// Возвращает существующую или создает новую ViewModel для трека.
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // 1. Проверяем кэш
        if (_cache.TryGetValue(track.Id, out var weakRef) && 
            weakRef.TryGetTarget(out var existing) && 
            !existing.IsDisposed)
        {
            // Обновляем контекст использования
            existing.UpdatePlayAction(playAction);
            existing.IsQueueContext = false;

            // НЕ нужно синхронизировать IsLiked/IsDownloaded — 
            // они автоматически берутся из Track через ObservableAsPropertyHelper

            return existing;
        }

        // 2. Создаем новую
        var vm = new TrackItemViewModel(
            track,
            _audio,
            _library,
            _downloads,
            _manager,
            playAction);

        // 3. Сохраняем в кэш
        _cache[track.Id] = new WeakReference<TrackItemViewModel>(vm);

        return vm;
    }

    /// <summary>
    /// Создает VM для использования в очереди воспроизведения.
    /// </summary>
    public TrackItemViewModel CreateForQueue(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        var vm = GetOrCreate(track, playAction);
        vm.IsQueueContext = true;
        return vm;
    }

    /// <summary>
    /// Удаляет "мертвые" ссылки из кэша.
    /// </summary>
    public void CleanupCache()
    {
        var deadKeys = _cache
            .Where(kvp => !kvp.Value.TryGetTarget(out var target) || target.IsDisposed)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in deadKeys)
            _cache.TryRemove(key, out _);

        if (deadKeys.Count > 0)
            Log.Info($"[TrackFactory] Cleaned {deadKeys.Count} dead items.");
    }

    /// <summary>
    /// Полностью очищает кэш.
    /// </summary>
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