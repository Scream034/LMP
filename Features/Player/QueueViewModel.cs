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

public class QueueViewModel : ViewModelBase, IDisposable, IFilterable
{
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly TrackViewModelFactory _vmFactory;

    private readonly IDisposable? _queueSub;
    private readonly IDisposable? _trackSub;
    private bool _isMovingInternally; // Флаг для подавления обновления

    [Reactive] public bool IsEmpty { get; private set; } = true;
    [Reactive] public bool CanReorderItems { get; private set; }

    [Reactive] public string FilterQuery { get; set; } = string.Empty;
    [Reactive] public ContentFilterType FilterType { get; set; } = ContentFilterType.All;

    public ObservableCollection<TrackItemViewModel> QueueItems { get; } = [];

    public ReactiveCommand<string, Unit> SetFilterTypeCommand { get; }
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

        // Init Filter Command
        SetFilterTypeCommand = ReactiveCommand.Create<string>(typeStr =>
        {
            if (Enum.TryParse<ContentFilterType>(typeStr, true, out var result))
                FilterType = result;
        });

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

        // Оптимизированная команда перемещения
        MoveItemCommand = ReactiveCommand.Create<(int oldIndex, int newIndex)>(tuple =>
        {
            // Запрещаем перемещение, если включен фильтр (индексы не совпадают с реальными)
            if (!string.IsNullOrEmpty(FilterQuery) || FilterType != ContentFilterType.All) return;

            var (oldIdx, newIdx) = tuple;
            if (oldIdx == newIdx) return;

            try
            {
                _isMovingInternally = true;

                // 1. Мгновенно обновляем UI (High Performance)
                // ObservableCollection.Move не пересоздает список, а уведомляет только о перемещении
                if (oldIdx >= 0 && oldIdx < QueueItems.Count &&
                    newIdx >= 0 && newIdx < QueueItems.Count)
                {
                    QueueItems.Move(oldIdx, newIdx);
                }

                // 2. Обновляем AudioEngine
                _audio.MoveQueueItem(oldIdx, newIdx);
            }
            finally
            {
                _isMovingInternally = false;
            }
        });

        // Подписки
        _queueSub = Observable.FromEvent(
                h => _audio.OnQueueChanged += h,
                h => _audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                // Если изменение вызвано нашим перемещением, не перестраиваем список заново
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

        // Реакция на изменение фильтров
        this.WhenAnyValue(x => x.FilterQuery, x => x.FilterType)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshQueue());

        this.WhenAnyValue(x => x.FilterQuery, x => x.FilterType)
             .Throttle(TimeSpan.FromMilliseconds(200))
             .ObserveOn(RxApp.MainThreadScheduler)
             .Subscribe(_ =>
             {
                 CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery) && FilterType == ContentFilterType.All;
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

        // Полная очистка только если структура изменилась
        // Для производительности при больших списках лучше было бы использовать DynamicData,
        // но для очереди плеера (обычно < 1000 элементов) полная замена приемлема,
        // если это происходит не часто (не при перемещении).

        foreach (var item in QueueItems) item.Cleanup();
        QueueItems.Clear();

        foreach (var track in filteredTracks)
        {
            var vm = _vmFactory.CreateForQueue(track, PlayFromQueue);
            vm.SetActive(track.Id == currentId);
            QueueItems.Add(vm);
        }

        IsEmpty = QueueItems.Count == 0;
    }

    private bool FilterItem(TrackInfo item)
    {
        if (FilterType == ContentFilterType.Music && !item.IsMusic) return false;
        if (FilterType == ContentFilterType.Video && item.IsMusic) return false;

        if (!string.IsNullOrWhiteSpace(FilterQuery))
        {
            return item.Title.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                   item.Author.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

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

    public void Dispose()
    {
        _queueSub?.Dispose();
        _trackSub?.Dispose();

        foreach (var item in QueueItems)
        {
            item.Dispose(); // Важно вызвать Dispose, а не просто Cleanup (если они отличаются)
        }

        QueueItems.Clear();
        GC.SuppressFinalize(this);
    }
}