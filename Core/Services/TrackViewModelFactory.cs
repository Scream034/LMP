// ============================================================================
// Файл: Core/Services/TrackViewModelFactory.cs
// Описание: Фабрика для создания и кэширования TrackItemViewModel.
// Исправления:
//   - [FIX] Добавлена принудительная очистка кэша с вызовом Dispose для VM.
//   - [FIX] Очистка мертвых WeakReference.
// ============================================================================

using System.Collections.Concurrent;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.ViewModels;
using MyLiteMusicPlayer.Features.Shared;

namespace MyLiteMusicPlayer.Core.Services;

/// <summary>
/// Фабрика для создания <see cref="TrackItemViewModel"/>.
/// Использует кэширование на основе WeakReference для предотвращения дублирования VM.
/// </summary>
public class TrackViewModelFactory
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;

    // Кэш: ID -> Слабая ссылка на VM
    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    #endregion

    #region Constructor

    public TrackViewModelFactory(
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        MusicLibraryManager manager)
    {
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _manager = manager;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Возвращает существующую или создает новую ViewModel для трека.
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // 1. Проверяем кэш
        if (_cache.TryGetValue(track.Id, out var weakRef) && weakRef.TryGetTarget(out var existing))
        {
            // Обновляем контекст использования
            existing.UpdatePlayAction(playAction);
            
            // Синхронизируем состояние
            existing.IsLiked = track.IsLiked;
            existing.IsDownloaded = track.IsDownloaded;
            existing.IsQueueContext = false;
            
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
    /// Удаляет "мертвые" ссылки из кэша (объекты, собранные GC).
    /// </summary>
    public void CleanupCache()
    {
        var deadKeys = _cache
            .Where(kvp => !kvp.Value.TryGetTarget(out _))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in deadKeys)
        {
            _cache.TryRemove(key, out _);
        }
        
        if (deadKeys.Count > 0)
        {
            Log.Info($"[TrackFactory] Cleaned {deadKeys.Count} dead items.");
        }
    }

    /// <summary>
    /// Полностью очищает кэш. Вызывает Dispose у всех еще живых VM.
    /// Рекомендуется вызывать при смене профиля или глобальной очистке.
    /// </summary>
    public void Clear()
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.TryGetTarget(out var vm))
            {
                // [FIX] Принудительно убиваем VM, чтобы она отписалась от событий
                vm.Dispose();
            }
        }
        _cache.Clear();
    }

    #endregion
}