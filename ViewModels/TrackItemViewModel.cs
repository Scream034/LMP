using System.Reactive;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

public class TrackItemViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private Action<TrackInfo>? _onPlay;

    public TrackInfo Track { get; }
    
    // Removed Activator: We use constructor-based weak subscriptions now.

    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDownloaded { get; set; }
    [Reactive] public bool IsDownloading { get; set; }
    [Reactive] public float DownloadProgress { get; set; }

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

        Title = track.Title;
        Author = track.Author;
        Duration = track.Duration;
        ThumbnailUrl = track.ThumbnailUrl;
        IsDownloaded = track.IsDownloaded;
        IsLiked = track.IsLiked;

        // Initialize state immediately
        UpdateActiveState(_audio.CurrentTrack, _audio.IsPlaying);

        // --- WEAK SUBSCRIPTIONS PATTERN ---
        // Using WeakReference allows this ViewModel to be Garbage Collected
        // even though it subscribes to Singleton services (AudioEngine, LibraryService).
        
        var weakSelf = new WeakReference<TrackItemViewModel>(this);

        // 1. Audio Engine: Track Changed
        Action<TrackInfo?> onTrackChanged = null!;
        onTrackChanged = t =>
        {
            if (weakSelf.TryGetTarget(out var vm)) 
                vm.UpdateActiveState(t, vm._audio.IsPlaying);
            else 
                audio.OnTrackChanged -= onTrackChanged; // Auto-unsubscribe if dead
        };
        _audio.OnTrackChanged += onTrackChanged;

        // 2. Audio Engine: Playback State
        Action<bool, bool> onPlaybackState = null!;
        onPlaybackState = (playing, paused) =>
        {
            if (weakSelf.TryGetTarget(out var vm)) 
                vm.UpdateActiveState(vm._audio.CurrentTrack, playing);
            else 
                audio.OnPlaybackStateChanged -= onPlaybackState;
        };
        _audio.OnPlaybackStateChanged += onPlaybackState;

        // 3. Library: Likes update
        Action<TrackInfo> onTrackUpdated = null!;
        onTrackUpdated = t =>
        {
            if (weakSelf.TryGetTarget(out var vm))
            {
                if (t.Id == vm.Track.Id) vm.IsLiked = t.IsLiked;
            }
            else 
                library.OnTrackUpdated -= onTrackUpdated;
        };
        _library.OnTrackUpdated += onTrackUpdated;

        // 4. Downloads: Progress
        Action<string, float> onProgress = null!;
        onProgress = (id, progress) =>
        {
            if (weakSelf.TryGetTarget(out var vm))
            {
                if (id == vm.Track.Id)
                {
                    vm.IsDownloading = progress < 1.0f;
                    vm.DownloadProgress = progress;
                    if (progress >= 1.0f) vm.IsDownloaded = true;
                }
            }
            else 
                downloads.OnProgress -= onProgress;
        };
        _downloads.OnProgress += onProgress;

        // Commands
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

    private void UpdateActiveState(TrackInfo? currentTrack, bool isPlaying)
    {
        bool isMe = currentTrack?.Id == Track.Id;
        // Direct property assignment triggers [Reactive] notification for the View
        IsActive = isMe;
        IsPlaying = isMe && isPlaying;
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay)
    {
        _onPlay = onPlay;
    }
}