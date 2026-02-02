using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace LMP.Features.Player;

/// <summary>
/// ViewModel для очереди воспроизведения.
/// Не использует SearchSource, так как очередь содержит уже загруженные треки.
/// </summary>
public class QueueViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly TrackViewModelFactory _vmFactory;

    private readonly IDisposable? _queueSub;
    private readonly IDisposable? _trackSub;
    private bool _isMovingInternally;

    private readonly Dictionary<string, TrackItemViewModel> _vmCache = [];

    [Reactive] public bool IsEmpty { get; private set; } = true;
    [Reactive] public bool CanReorderItems { get; private set; } = true;

    /// <summary>
    /// Текстовый фильтр для поиска в очереди.
    /// </summary>
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

        ClearQueueCommand = ReactiveCommand.Create(() => _audio.ClearQueue());
        ShuffleQueueCommand = ReactiveCommand.Create(() => _audio.ShuffleQueue());

        DownloadAllCommand = ReactiveCommand.Create(() =>
        {
            foreach (var item in QueueItems.Where(t => !t.IsDownloaded))
                _downloads.StartDownload(item.Track);
        });

        RemoveTrackCommand = ReactiveCommand.Create<TrackItemViewModel>(item =>
        {
            _audio.RemoveFromQueue(item.Track);
        });

        MoveItemCommand = ReactiveCommand.Create<(int oldIndex, int newIndex)>(tuple =>
        {
            if (!CanReorderItems) return;

            var (oldIdx, newIdx) = tuple;
            if (oldIdx == newIdx) return;

            try
            {
                _isMovingInternally = true;

                if (oldIdx >= 0 && oldIdx < QueueItems.Count &&
                    newIdx >= 0 && newIdx < QueueItems.Count)
                {
                    QueueItems.Move(oldIdx, newIdx);
                }

                _audio.MoveQueueItem(oldIdx, newIdx);
            }
            finally
            {
                _isMovingInternally = false;
            }
        });

        _queueSub = Observable.FromEvent(
                h => _audio.OnQueueChanged += h,
                h => _audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!_isMovingInternally)
                {
                    RefreshQueue();
                }
            });

        _trackSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateActiveStates());

        // Реакция на изменение фильтра
        this.WhenAnyValue(x => x.FilterQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery);
                RefreshQueue();
            });

        RefreshQueue();
    }

    private void RefreshQueue()
    {
        var allQueue = _audio.Queue;
        var currentId = _audio.CurrentTrack?.Id;

        // Применяем фильтр
        var filteredTracks = allQueue.Where(FilterItem).ToList();
        var filteredIds = filteredTracks.Select(t => t.Id).ToHashSet();

        // 1. Удаляем VM для треков, которых больше нет
        var toRemove = QueueItems
            .Where(vm => !filteredIds.Contains(vm.Id))
            .ToList();

        foreach (var vm in toRemove)
        {
            QueueItems.Remove(vm);
            _vmCache.Remove(vm.Id);
            vm.Dispose();
        }

        // 2. Добавляем новые VM / обновляем порядок
        for (int i = 0; i < filteredTracks.Count; i++)
        {
            var track = filteredTracks[i];

            if (!_vmCache.TryGetValue(track.Id, out var vm))
            {
                vm = _vmFactory.CreateForQueue(track, PlayFromQueue);
                _vmCache[track.Id] = vm;
            }

            vm.SetActive(track.Id == currentId);

            int currentIndex = QueueItems.IndexOf(vm);
            if (currentIndex == -1)
            {
                if (i < QueueItems.Count)
                    QueueItems.Insert(i, vm);
                else
                    QueueItems.Add(vm);
            }
            else if (currentIndex != i)
            {
                QueueItems.Move(currentIndex, i);
            }
        }

        IsEmpty = QueueItems.Count == 0;
    }

    private bool FilterItem(TrackInfo item)
    {
        if (string.IsNullOrWhiteSpace(FilterQuery)) return true;

        return item.Title.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
               item.Author.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase);
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

    public void Dispose()
    {
        _queueSub?.Dispose();
        _trackSub?.Dispose();

        foreach (var vm in _vmCache.Values)
        {
            vm.Dispose();
        }
        _vmCache.Clear();
        QueueItems.Clear();

        GC.SuppressFinalize(this);
    }
}