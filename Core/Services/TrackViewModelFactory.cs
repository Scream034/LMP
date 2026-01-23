using System.Collections.Concurrent;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.ViewModels;
using MyLiteMusicPlayer.Features.Shared;

namespace MyLiteMusicPlayer.Core.Services;

/// <summary>
/// Фабрика для создания <see cref="TrackItemViewModel"/>.
/// Использует кэширование на основе WeakReference для предотвращения дублирования VM.
/// </summary>
public class TrackViewModelFactory(
    AudioEngine audio,
    LibraryService library,
    DownloadService downloads,
    MusicLibraryManager manager)
{
    #region Fields

    private readonly AudioEngine _audio = audio;
    private readonly LibraryService _library = library;
    private readonly DownloadService _downloads = downloads;
    private readonly MusicLibraryManager _manager = manager;

    // Кэш: ID -> Слабая ссылка на VM
    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    #endregion

    #region Public Methods

    /// <summary>
    /// Возвращает существующую или создает новую ViewModel для трека.
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // 1. Проверяем кэш
        // ВАЖНО: Проверяем !existing.IsDisposed. PaginatedViewModel может вызывать Dispose
        // при очистке списка, но ссылка в кэше еще может быть жива (до сборки мусора).
        if (_cache.TryGetValue(track.Id, out var weakRef) && 
            weakRef.TryGetTarget(out var existing) && 
            !existing.IsDisposed)
        {
            // Обновляем контекст использования (например, сменилась страница Search -> Home)
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
        // Для очереди используем GetOrCreate, чтобы переиспользовать кэшированные картинки и состояние
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
            .Where(kvp => !kvp.Value.TryGetTarget(out var target) || target.IsDisposed) // Удаляем и Dispose-нутые
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
    /// </summary>
    public void Clear()
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.TryGetTarget(out var vm))
            {
                vm.Dispose();
            }
        }
        _cache.Clear();
    }

    #endregion
}