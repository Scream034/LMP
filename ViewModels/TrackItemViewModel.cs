using System.Reactive.Disposables;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class TrackItemViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;

    private readonly CompositeDisposable _disposables = new();

    public TrackInfo Track { get; }

    // Реактивные свойства состояния
    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDownloaded { get; set; }
    [Reactive] public bool IsDownloading { get; set; }
    [Reactive] public float DownloadProgress { get; set; }

    // Статические свойства (не меняются) - без [Reactive] для экономии памяти
    public string Title { get; }
    public string Author { get; }
    public TimeSpan Duration { get; }
    public string ThumbnailUrl { get; }

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }

    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        MusicLibraryManager manager,
        Action<TrackInfo>? onPlay = null)
    {
        Track = track;
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _manager = manager;

        // Инициализируем статические свойства один раз
        Title = track.Title;
        Author = track.Author;
        Duration = track.Duration;
        ThumbnailUrl = track.ThumbnailUrl;

        IsDownloaded = track.IsDownloaded;
        IsLiked = track.IsLiked;

        // 1. Подписка на обновление (Лайки)
        Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
                h => _library.OnTrackUpdated += h,
                h => _library.OnTrackUpdated -= h)
            .Where(t => t.Id == Track.Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(updatedTrack =>
            {
                IsLiked = updatedTrack.IsLiked;
                Track.IsLiked = updatedTrack.IsLiked;
            })
            .DisposeWith(_disposables);

        // 2. Подписка на состояние плеера
        _audio.WhenAnyValue(x => x.CurrentTrack, x => x.IsPlaying)
            .Select(t =>
            {
                var (current, playing) = t;
                bool isMe = current?.Id == Track.Id;
                return (IsActive: isMe, IsPlaying: isMe && playing);
            })
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                IsActive = state.IsActive;
                IsPlaying = state.IsPlaying;
            })
            .DisposeWith(_disposables);

        // 3. Загрузки
        Observable.FromEvent<Action<string, float>, (string, float)>(
                h => (id, p) => h((id, p)),
                h => _downloads.OnProgress += h,
                h => _downloads.OnProgress -= h)
            .Where(x => x.Item1 == Track.Id)
            .Sample(TimeSpan.FromMilliseconds(100)) // Ограничиваем частоту обновления прогресса
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                IsDownloading = x.Item2 < 1.0f;
                DownloadProgress = x.Item2;
                if (x.Item2 >= 1.0f)
                {
                    IsDownloaded = true;
                }
            })
            .DisposeWith(_disposables);

        PlayCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (onPlay != null)
                onPlay(Track);
            else
                await _audio.PlayTrackAsync(Track);
        });

        ToggleLikeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ToggleLikeAsync(Track);
            IsLiked = Track.IsLiked;
        });
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}