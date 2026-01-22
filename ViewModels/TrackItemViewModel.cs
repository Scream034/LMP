using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

public class TrackItemViewModel : ViewModelBase, IDisposable, IActivatableViewModel
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;

    // Allows updating the action if the VM is reused from cache
    private Action<TrackInfo>? _onPlay;

    public TrackInfo Track { get; }
    public ViewModelActivator Activator { get; } = new();

    // Reactive properties
    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDownloaded { get; set; }
    [Reactive] public bool IsDownloading { get; set; }
    [Reactive] public float DownloadProgress { get; set; }

    // Static properties (Non-reactive for memory efficiency)
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
        _onPlay = onPlay;

        // Initialize static data
        Title = track.Title;
        Author = track.Author;
        Duration = track.Duration;
        ThumbnailUrl = track.ThumbnailUrl;
        IsDownloaded = track.IsDownloaded;
        IsLiked = track.IsLiked;

        // ACTIVATION LOGIC: Subscriptions only active when View is attached to Visual Tree
        this.WhenActivated(disposables =>
        {
            // 1. Listen for Like updates
            Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
                    h => _library.OnTrackUpdated += h,
                    h => _library.OnTrackUpdated -= h)
                .Where(t => t.Id == Track.Id)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(updatedTrack =>
                {
                    IsLiked = updatedTrack.IsLiked;
                    // Don't update Track object properties here to avoid side effects, just the VM state
                })
                .DisposeWith(disposables);

            // 2. Player state (Active/Playing)
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
                .DisposeWith(disposables);

            // 3. Downloads
            Observable.FromEvent<Action<string, float>, (string, float)>(
                    h => _downloads.OnProgress += h,
                    h => _downloads.OnProgress -= h)
                .Where(x => x.Item1 == Track.Id)
                .Sample(TimeSpan.FromMilliseconds(200)) // Throttle UI updates
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
                .DisposeWith(disposables);
        });

        PlayCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_onPlay != null)
                _onPlay(Track);
            else
                await _audio.PlayTrackAsync(Track);
        });

        ToggleLikeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ToggleLikeAsync(Track);
            IsLiked = Track.IsLiked;
        });
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay)
    {
        _onPlay = onPlay;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}