using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace LMP.Features.Player;

public class QueueViewModel : ViewModelBase, IDisposable, IFilterable
{
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly TrackViewModelFactory _vmFactory;
    private readonly DialogService _dialog;
    private readonly MusicLibraryManager _manager;
    private readonly LibraryService _library;

    private bool _isMovingInternally;
    private readonly Dictionary<string, TrackItemViewModel> _vmCache = [];

    // Master список (соответствует _audio.Queue)
    private List<TrackInfo> _masterQueue = [];

    private bool _isDisposed;

    // LIFECYCLE: Флаг для пропуска UI-обновлений когда окно свёрнуто
    private volatile bool _isSuspended;

    /// <summary>
    /// True когда очередь действительно пуста (нет треков вообще).
    /// Показывает "Очередь пуста" с иконкой.
    /// </summary>
    [Reactive] public bool IsEmpty { get; private set; } = true;

    /// <summary>
    /// True когда очередь НЕ пуста, но фильтр не нашёл совпадений.
    /// Показывает "Ничего не найдено".
    /// </summary>
    [Reactive] public bool IsFilterEmpty { get; private set; }

    [Reactive] public bool CanReorderItems { get; private set; } = true;
    [Reactive] public string FilterQuery { get; set; } = string.Empty;

    /// <summary>
    /// Используем BatchObservableCollection для атомарной замены содержимого.
    /// При фильтрации 500+ треков генерирует 1 Reset вместо 500 Add/Remove.
    /// </summary>
    public BatchObservableCollection<TrackItemViewModel> QueueItems { get; } = [];

    public ReactiveCommand<Unit, Unit> ClearQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<TrackItemViewModel, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<(int, int), Unit> MoveItemCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveQueueToPlaylistCommand { get; }

    public QueueViewModel(
        AudioEngine audio,
        DownloadService downloads,
        DialogService dialog,
        MusicLibraryManager manager,
        LibraryService library,
        TrackViewModelFactory vmFactory)
    {
        _audio = audio;
        _downloads = downloads;
        _dialog = dialog;
        _manager = manager;
        _library = library;
        _vmFactory = vmFactory;

        ClearQueueCommand = CreateCommand(ReactiveCommand.Create(() => _audio.ClearQueue()));
        ShuffleQueueCommand = CreateCommand(ReactiveCommand.Create(() => _audio.ShuffleQueue()));

        DownloadAllCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            foreach (var item in QueueItems)
            {
                if (!item.IsDownloaded)
                    _downloads.StartDownload(item.Track);
            }
        }));

        RemoveTrackCommand = CreateCommand(ReactiveCommand.Create<TrackItemViewModel>(item =>
        {
            _audio.RemoveFromQueue(item.Track);
        }));

        MoveItemCommand = CreateCommand(ReactiveCommand.Create<(int oldIndex, int newIndex)>(tuple =>
        {
            if (!CanReorderItems) return;
            MoveItem(tuple.oldIndex, tuple.newIndex);
        }));

        SaveQueueToPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            SaveQueueToPlaylistAsync,
            this.WhenAnyValue(x => x.IsEmpty, empty => !empty)));

        // Подписка на изменения очереди из AudioEngine
        Observable.FromEvent(
                h => _audio.OnQueueChanged += h,
                h => _audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (_isSuspended) return;

                if (!_isMovingInternally)
                {
                    RefreshFromAudioEngine();
                }
            })
            .DisposeWith(Disposables);

        // Подписка на смену текущего трека
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (_isSuspended) return;
                UpdateActiveStates();
            })
            .DisposeWith(Disposables);

        // Фильтрация с дебаунсом
        this.WhenAnyValue(x => x.FilterQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery);
                RebuildVisibleItems();
            })
            .DisposeWith(Disposables);

        RefreshFromAudioEngine();
    }

    private async Task SaveQueueToPlaylistAsync()
    {
        if (_masterQueue.Count == 0) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var playlist = await _library.CreatePlaylistAsync(result.Name.Trim());

        foreach (var track in _masterQueue)
        {
            await _manager.AddTrackToPlaylistAsync(playlist.Id, track);
        }

        Log.Info($"[Queue] Saved {_masterQueue.Count} tracks to playlist '{result.Name}'");
    }

    // LIFECYCLE IMPLEMENTATION

    /// <summary>
    /// Окно свёрнуто — пропускаем UI-обновления от событий AudioEngine.
    /// </summary>
    protected override void OnSuspend()
    {
        _isSuspended = true;
        Log.Debug($"[{GetType().Name}] Suspended — UI updates paused");
    }

    /// <summary>
    /// Окно развёрнуто — синхронизируем UI с актуальным состоянием очереди.
    /// </summary>
    protected override void OnResume()
    {
        _isSuspended = false;
        RefreshFromAudioEngine();
        Log.Debug($"[{GetType().Name}] Resumed — UI synchronized");
    }

    private void RefreshFromAudioEngine()
    {
        _masterQueue = [.. _audio.Queue];
        RebuildVisibleItems();
    }

    /// <summary>
    /// Перестраивает видимый список с учётом фильтра.
    /// Использует BatchObservableCollection.ReplaceAll для атомарного обновления UI.
    /// 
    /// <para><b>Оптимизации:</b></para>
    /// <list type="bullet">
    ///   <item>In-place очистка _vmCache вместо LINQ .Where().ToList()</item>
    ///   <item>Capacity hint для List и HashSet</item>
    /// </list>
    /// </summary>
    private void RebuildVisibleItems()
    {
        var currentId = _audio.CurrentTrack?.Id;
        var query = FilterQuery;
        bool hasFilter = !string.IsNullOrWhiteSpace(query);

        // Фильтруем master list
        var newItems = new List<TrackItemViewModel>(_masterQueue.Count);
        var usedIds = new HashSet<string>(_masterQueue.Count);

        foreach (var track in _masterQueue)
        {
            if (hasFilter && !MatchesFilter(track, query))
                continue;

            if (!_vmCache.TryGetValue(track.Id, out var vm))
            {
                vm = _vmFactory.CreateForQueue(track, PlayFromQueue);
                _vmCache[track.Id] = vm;
            }

            vm.SetActive(track.Id == currentId);
            newItems.Add(vm);
            usedIds.Add(track.Id);
        }

        // Очищаем неиспользуемые VM из кэша in-place (без промежуточного List)
        if (usedIds.Count < _vmCache.Count)
        {
            // Собираем ключи для удаления — нельзя модифицировать словарь во время итерации
            List<string>? keysToRemove = null;
            foreach (var kvp in _vmCache)
            {
                if (!usedIds.Contains(kvp.Key))
                {
                    keysToRemove ??= new List<string>(_vmCache.Count - usedIds.Count);
                    keysToRemove.Add(kvp.Key);
                }
            }

            if (keysToRemove != null)
            {
                foreach (var key in keysToRemove)
                {
                    _vmCache[key].Dispose();
                    _vmCache.Remove(key);
                }
            }
        }

        // Атомарная замена: 1 Reset-эвент вместо N Add/Remove
        QueueItems.ReplaceAll(newItems);

        // Обновляем состояния empty/filter
        IsEmpty = _masterQueue.Count == 0;
        IsFilterEmpty = !IsEmpty && hasFilter && QueueItems.Count == 0;
    }

    private static bool MatchesFilter(TrackInfo item, string query)
        => TrackFilters.MatchesTitleOrAuthor(item, query);

    private void MoveItem(int oldIdx, int newIdx)
    {
        if (oldIdx == newIdx) return;
        if (oldIdx < 0 || oldIdx >= QueueItems.Count) return;
        if (newIdx < 0 || newIdx >= QueueItems.Count) return;

        if (!string.IsNullOrWhiteSpace(FilterQuery))
        {
            Log.Warn("[Queue] Cannot reorder with active filter");
            return;
        }

        var movingVm = QueueItems[oldIdx];
        var movingTrack = movingVm.Track;

        Log.Info($"[Queue] Moving {oldIdx} → {newIdx}");

        try
        {
            _isMovingInternally = true;
            QueueItems.Move(oldIdx, newIdx);
            _masterQueue.RemoveAt(oldIdx);
            _masterQueue.Insert(newIdx, movingTrack);
            _audio.MoveQueueItem(oldIdx, newIdx);
        }
        finally
        {
            _isMovingInternally = false;
        }
    }

    /// <summary>
    /// Запускает воспроизведение трека из очереди.
    /// Fire-and-forget без лишнего Task.Run — PlayTrackAsync уже async.
    /// </summary>
    private void PlayFromQueue(TrackInfo track)
    {
        _ = _audio.PlayTrackAsync(track);
    }

    private void UpdateActiveStates()
    {
        var currentId = _audio.CurrentTrack?.Id;
        foreach (var item in QueueItems)
        {
            item.SetActive(item.Id == currentId);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[QueueVM] Disposing");

            foreach (var vm in _vmCache.Values)
                vm.Dispose();

            _vmCache.Clear();
            QueueItems.Clear();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }
}