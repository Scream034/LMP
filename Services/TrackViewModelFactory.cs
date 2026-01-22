using System.Collections.Concurrent;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Фабрика для переиспользования TrackItemViewModel.
/// Экономит RAM при большом количестве треков.
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

    // Кэш VM по ID трека
    private readonly ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> _cache = new();

    /// <summary>
    /// Получает или создаёт ViewModel для трека
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        // Пробуем получить из кэша
        if (_cache.TryGetValue(track.Id, out var weakRef) &&
            weakRef.TryGetTarget(out var existing))
        {
            // Обновляем playAction для нового контекста
            existing.UpdatePlayAction(playAction);

            // Синхронизируем данные трека
            existing.IsLiked = track.IsLiked;
            existing.IsDownloaded = track.IsDownloaded;
            existing.IsQueueContext = false; // Сбрасываем контекст

            return existing;
        }

        // Создаём новый VM с правильной сигнатурой
        var vm = new TrackItemViewModel(
            track,
            _audio,
            _library,
            _downloads,
            _manager,      // ← ИСПРАВЛЕНО: передаём manager
            playAction);

        // Кэшируем через WeakReference (GC может освободить если не используется)
        _cache[track.Id] = new WeakReference<TrackItemViewModel>(vm);

        return vm;
    }

    /// <summary>
    /// Создаёт VM специально для контекста очереди
    /// </summary>
    public TrackItemViewModel CreateForQueue(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        var vm = GetOrCreate(track, playAction);
        vm.IsQueueContext = true;
        return vm;
    }

    /// <summary>
    /// Очистка неиспользуемых VM
    /// </summary>
    public void CleanupCache()  // ← Переименовано для ясности
    {
        var deadKeys = _cache
            .Where(kvp => !kvp.Value.TryGetTarget(out _))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in deadKeys)
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Полная очистка кэша
    /// </summary>
    public void Clear()
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.TryGetTarget(out var vm))
            {
                vm.Cleanup();
            }
        }
        _cache.Clear();
    }
}