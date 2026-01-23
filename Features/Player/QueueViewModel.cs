using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.Features.Player;

public class QueueViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly TrackViewModelFactory _vmFactory;

    private IDisposable? _queueSub;
    private IDisposable? _trackSub;

    [Reactive] public bool IsEmpty { get; private set; } = true;

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

        MoveTrackCommand = ReactiveCommand.Create<(int oldIndex, int newIndex)>(tuple =>
        {
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

        RefreshQueue();
    }

    private void RefreshQueue()
    {
        var queue = _audio.Queue;
        var currentId = _audio.CurrentTrack?.Id;

        // Оптимизация: обновляем только при реальных изменениях
        if (QueueItems.Count == queue.Count)
        {
            bool same = true;
            for (int i = 0; i < queue.Count && same; i++)
            {
                if (QueueItems[i].Id != queue[i].Id)
                    same = false;
            }
            if (same)
            {
                UpdateActiveStates();
                return;
            }
        }

        // Cleanup старых VM
        foreach (var item in QueueItems)
            item.Cleanup();

        QueueItems.Clear();

        foreach (var track in queue)
        {
            // Используем фабрику с контекстом очереди
            var vm = _vmFactory.CreateForQueue(track, PlayFromQueue);
            
            // Устанавливаем активное состояние
            vm.SetActive(track.Id == currentId);
            
            QueueItems.Add(vm);
        }

        IsEmpty = QueueItems.Count == 0;
    }

    private void PlayFromQueue(TrackInfo track)
    {
        // В очереди: просто переключаемся на выбранный трек
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


