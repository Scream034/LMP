using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using MyLiteMusicPlayer.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.Features.Player;

public class QueueViewModel : ViewModelBase, IDisposable, IFilterable
{
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly TrackViewModelFactory _vmFactory;

    private IDisposable? _queueSub;
    private IDisposable? _trackSub;

    [Reactive] public bool IsEmpty { get; private set; } = true;

    [Reactive] public string FilterQuery { get; set; } = string.Empty;
    [Reactive] public ContentFilterType FilterType { get; set; } = ContentFilterType.All;
    public ReactiveCommand<string, Unit> SetFilterTypeCommand { get; }

    public ObservableCollection<TrackItemViewModel> QueueItems { get; } = [];

    public ReactiveCommand<Unit, Unit> ClearQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<TrackItemViewModel, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<(int, int), Unit> MoveTrackCommand { get; }

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
            // Скачиваем только те, что сейчас ВИДИМЫ в списке (с учетом фильтра)
            foreach (var item in QueueItems.Where(t => !t.IsDownloaded))
                _downloads.StartDownload(item.Track);
        });

        RemoveTrackCommand = ReactiveCommand.Create<TrackItemViewModel>(item =>
        {
            _audio.RemoveFromQueue(item.Track);
        });

        MoveTrackCommand = ReactiveCommand.Create<(int oldIndex, int newIndex)>(tuple =>
        {
            // Запрещаем перемещение, если включен фильтр, чтобы не сломать индексы
            if (!string.IsNullOrEmpty(FilterQuery) || FilterType != ContentFilterType.All) return;
            _audio.MoveQueueItem(tuple.oldIndex, tuple.newIndex);
        });

        // Подписки
        _queueSub = Observable.FromEvent(
                h => _audio.OnQueueChanged += h,
                h => _audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshQueue());

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

        RefreshQueue();
    }

    private void RefreshQueue()
    {
        var allQueue = _audio.Queue; // Полный список из движка
        var currentId = _audio.CurrentTrack?.Id;

        // Применяем фильтр
        var filteredTracks = allQueue.Where(FilterItem).ToList();

        // Полная перерисовка (для простоты и надежности при фильтрации)
        // При большом кол-ве элементов тут стоило бы использовать DynamicData, как в PaginatedViewModel,
        // но очередь обычно небольшая.
        
        // Очищаем старые VM
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
        // 1. Фильтр по типу
        if (FilterType == ContentFilterType.Music && !item.IsMusic) return false;
        if (FilterType == ContentFilterType.Video && item.IsMusic) return false;

        // 2. Текстовый поиск
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
            item.Cleanup();
        
        QueueItems.Clear();
        GC.SuppressFinalize(this);
    }
}