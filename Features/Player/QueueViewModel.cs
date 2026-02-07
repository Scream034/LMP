using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace LMP.Features.Player;

public class QueueViewModel : ViewModelBase, IDisposable, IFilterable
{
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly TrackViewModelFactory _vmFactory;

    private bool _isMovingInternally;
    private readonly Dictionary<string, TrackItemViewModel> _vmCache = [];

    // Master список (соответствует _audio.Queue)
    private List<TrackInfo> _masterQueue = [];

    private bool _isDisposed;

    // LIFECYCLE: Флаг для пропуска UI-обновлений когда окно свёрнуто
    private volatile bool _isSuspended;

    [Reactive] public bool IsEmpty { get; private set; } = true;
    [Reactive] public bool CanReorderItems { get; private set; } = true;
    [Reactive] public string FilterQuery { get; set; } = string.Empty;

    public ObservableCollection<TrackItemViewModel> QueueItems { get; } = [];

    public ReactiveCommand<Unit, Unit> ClearQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<TrackItemViewModel, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<(int, int), Unit> MoveItemCommand { get; }

    public QueueViewModel(
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory)
    {
        _audio = audio;
        _downloads = downloads;
        _vmFactory = vmFactory;

        ClearQueueCommand = CreateCommand(ReactiveCommand.Create(() => _audio.ClearQueue()));
        ShuffleQueueCommand = CreateCommand(ReactiveCommand.Create(() => _audio.ShuffleQueue()));

        DownloadAllCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            foreach (var item in QueueItems.Where(t => !t.IsDownloaded))
                _downloads.StartDownload(item.Track);
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

        // ИЗМЕНЕНО: Добавлена проверка _isSuspended для пропуска UI-обновлений
        Observable.FromEvent(
                h => _audio.OnQueueChanged += h,
                h => _audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                // LIFECYCLE: Пропускаем обновления когда окно свёрнуто
                if (_isSuspended) return;
                
                if (!_isMovingInternally)
                {
                    RefreshFromAudioEngine();
                }
            })
            .DisposeWith(Disposables);

        // ИЗМЕНЕНО: Добавлена проверка _isSuspended
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                // LIFECYCLE: Пропускаем обновления когда окно свёрнуто
                if (_isSuspended) return;
                
                UpdateActiveStates();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.FilterQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                // Фильтрация может сработать только при активном окне
                // (пользователь вводит текст), проверка не нужна
                CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery);
                RebuildVisibleItems();
            })
            .DisposeWith(Disposables);

        RefreshFromAudioEngine();
    }

    // LIFECYCLE IMPLEMENTATION

    /// <summary>
    /// Окно свёрнуто — пропускаем UI-обновления от событий AudioEngine.
    /// Очередь продолжает работать, но UI не обновляется.
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
        
        // Синхронизируем данные которые могли измениться пока окно было свёрнуто
        // (треки могли проигрываться, очередь могла измениться)
        RefreshFromAudioEngine();
        
        Log.Debug($"[{GetType().Name}] Resumed — UI synchronized");
    }


    private void RefreshFromAudioEngine()
    {
        _masterQueue = [.. _audio.Queue];
        RebuildVisibleItems();
    }

    private void RebuildVisibleItems()
    {
        var currentId = _audio.CurrentTrack?.Id;
        var query = FilterQuery;

        var filtered = _masterQueue
            .Where(t => MatchesFilter(t, query))
            .ToList();

        var newItems = new List<TrackItemViewModel>();
        var usedIds = new HashSet<string>();

        foreach (var track in filtered)
        {
            if (!_vmCache.TryGetValue(track.Id, out var vm))
            {
                vm = _vmFactory.CreateForQueue(track, PlayFromQueue);
                _vmCache[track.Id] = vm;
            }

            vm.SetActive(track.Id == currentId);
            newItems.Add(vm);
            usedIds.Add(track.Id);
        }

        var toRemove = _vmCache.Keys.Where(k => !usedIds.Contains(k)).ToList();
        foreach (var id in toRemove)
        {
            _vmCache[id].Dispose();
            _vmCache.Remove(id);
        }

        SyncCollection(newItems);
        IsEmpty = QueueItems.Count == 0;
    }

    private void SyncCollection(List<TrackItemViewModel> newItems)
    {
        while (QueueItems.Count > newItems.Count)
        {
            QueueItems.RemoveAt(QueueItems.Count - 1);
        }

        for (int i = 0; i < newItems.Count; i++)
        {
            if (i < QueueItems.Count)
            {
                if (!ReferenceEquals(QueueItems[i], newItems[i]))
                {
                    QueueItems[i] = newItems[i];
                }
            }
            else
            {
                QueueItems.Add(newItems[i]);
            }
        }
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

    private void PlayFromQueue(TrackInfo track)
    {
        Task.Run(async () => await _audio.PlayTrackAsync(track));
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