// Features/Player/QueueViewModel.cs
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

    private readonly IDisposable? _queueSub;
    private readonly IDisposable? _trackSub;
    private readonly IDisposable? _filterSub;

    private bool _isMovingInternally;
    private readonly Dictionary<string, TrackItemViewModel> _vmCache = [];

    // Master список (соответствует _audio.Queue)
    private List<TrackInfo> _masterQueue = [];

    private bool _isDisposed;

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

        // ThrownExceptions subscription to prevent memory leak
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

        _queueSub = Observable.FromEvent(
                h => _audio.OnQueueChanged += h,
                h => _audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!_isMovingInternally)
                {
                    RefreshFromAudioEngine();
                }
            });

        _trackSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateActiveStates());

        _filterSub = this.WhenAnyValue(x => x.FilterQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery);
                RebuildVisibleItems();
            });

        RefreshFromAudioEngine();
    }

    /// <summary>
    /// Синхронизирует _masterQueue с AudioEngine.
    /// </summary>
    private void RefreshFromAudioEngine()
    {
        _masterQueue = [.. _audio.Queue];
        RebuildVisibleItems();
    }

    /// <summary>
    /// Перестраивает QueueItems на основе _masterQueue и фильтра.
    /// </summary>
    private void RebuildVisibleItems()
    {
        var currentId = _audio.CurrentTrack?.Id;
        var query = FilterQuery;

        // Формируем отфильтрованный список
        var filtered = _masterQueue
            .Where(t => MatchesFilter(t, query))
            .ToList();

        // Собираем нужные VM
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

        // Удаляем неиспользуемые VM из кэша
        var toRemove = _vmCache.Keys.Where(k => !usedIds.Contains(k)).ToList();
        foreach (var id in toRemove)
        {
            _vmCache[id].Dispose();
            _vmCache.Remove(id);
        }

        // Синхронизируем ObservableCollection
        SyncCollection(newItems);

        IsEmpty = QueueItems.Count == 0;
    }

    private void SyncCollection(List<TrackItemViewModel> newItems)
    {
        // Удаляем лишние с конца
        while (QueueItems.Count > newItems.Count)
        {
            QueueItems.RemoveAt(QueueItems.Count - 1);
        }

        // Обновляем/добавляем
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

    /// <summary>
    /// Перемещает элемент в видимом списке с пересчётом master-индексов.
    /// </summary>
    private void MoveItem(int oldIdx, int newIdx)
    {
        if (oldIdx == newIdx) return;
        if (oldIdx < 0 || oldIdx >= QueueItems.Count) return;
        if (newIdx < 0 || newIdx >= QueueItems.Count) return;

        // При активном фильтре нужно пересчитывать master индексы
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

            // 1. Обновляем UI
            QueueItems.Move(oldIdx, newIdx);

            // 2. Обновляем локальный master
            _masterQueue.RemoveAt(oldIdx);
            _masterQueue.Insert(newIdx, movingTrack);

            // 3. Обновляем AudioEngine
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