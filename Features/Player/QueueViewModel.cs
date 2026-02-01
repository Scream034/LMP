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

    private readonly Dictionary<string, TrackItemViewModel> _vmCache = [];

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
        var allQueue = _audio.Queue;  // Теперь это кэшированный snapshot
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
                // Создаём новую VM только для новых треков
                vm = _vmFactory.CreateForQueue(track, PlayFromQueue);
                _vmCache[track.Id] = vm;
            }

            vm.SetActive(track.Id == currentId);

            // Синхронизируем позицию
            int currentIndex = QueueItems.IndexOf(vm);
            if (currentIndex == -1)
            {
                // Новый элемент
                if (i < QueueItems.Count)
                    QueueItems.Insert(i, vm);
                else
                    QueueItems.Add(vm);
            }
            else if (currentIndex != i)
            {
                // Переместить на правильную позицию
                QueueItems.Move(currentIndex, i);
            }
        }

        IsEmpty = QueueItems.Count == 0;
    }

    private bool FilterItem(TrackInfo item)
    {
        // 1. Text search
        if (!string.IsNullOrWhiteSpace(FilterQuery))
        {
            bool matchesText = item.Title.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                               item.Author.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase);
            if (!matchesText) return false;
        }

        // 2. Type filter
        // Логика:
        // - Music: всё, кроме явных видеоклипов от официальных каналов
        // - Video: только явные видеоклипы (официальный канал артиста + НЕ помечен как Song)
        return FilterType switch
        {
            ContentFilterType.All => true,
            ContentFilterType.Music => !item.IsExplicitVideoClip,
            ContentFilterType.Video => item.IsExplicitVideoClip,
            _ => true
        };
    }

    private void PlayFromQueue(TrackInfo track)
    {
        // Fire-and-forget через Task.Run, чтобы клик по кнопке не фризил UI
        // пока AudioEngine останавливает предыдущий трек
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