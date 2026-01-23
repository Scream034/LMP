using System.Reactive;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Features.Shared;

public class TrackItemViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private Action<TrackInfo>? _onPlay;
    
    // Список подписок для очистки
    private readonly List<IDisposable> _subscriptions = [];

    public TrackInfo Track { get; }
    
    /// <summary>
    /// Уникальный ID трека (для сравнения в очереди)
    /// </summary>
    public string Id => Track.Id;

    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDownloaded { get; set; }
    [Reactive] public bool IsDownloading { get; set; }
    [Reactive] public float DownloadProgress { get; set; }
    
    /// <summary>
    /// Флаг: отображается в контексте очереди (скрывает кнопку добавления в очередь)
    /// </summary>
    [Reactive] public bool IsQueueContext { get; set; }
    
    /// <summary>
    /// Показывать ли кнопку "Добавить в очередь"
    /// </summary>
    public bool ShowAddToQueue => !IsQueueContext;

    public string Title { get; }
    public string Author { get; }
    public TimeSpan Duration { get; }
    public string ThumbnailUrl { get; }

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

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

        // Initial state from Services
        IsDownloading = _downloads.IsDownloading(track.Id);
        if (IsDownloading) DownloadProgress = _downloads.GetProgress(track.Id);

        IsDownloaded = track.IsDownloaded;
        IsLiked = track.IsLiked;

        UpdateActiveState(_audio.CurrentTrack, _audio.IsPlaying);

        // Подписки через WeakReference для избежания утечек памяти
        SetupSubscriptions();

        PlayCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_audio.CurrentTrack?.Id == Track.Id)
                await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
            else
            {
                if (_onPlay != null) _onPlay(Track);
                else await _audio.PlayTrackAsync(Track);
            }
        });

        ToggleLikeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ToggleLikeAsync(Track);
        });

        AddToQueueCommand = ReactiveCommand.Create(() =>
        {
            _audio.Enqueue(Track);
        });
        
        // Реактивное обновление ShowAddToQueue при изменении IsQueueContext
        this.WhenAnyValue(x => x.IsQueueContext)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowAddToQueue)));
    }

    private void SetupSubscriptions()
    {
        var weakSelf = new WeakReference<TrackItemViewModel>(this);

        // 1. Audio Engine Events
        Action<TrackInfo?> onTrackChanged = null!;
        onTrackChanged = t =>
        {
            if (weakSelf.TryGetTarget(out var vm)) 
                vm.UpdateActiveState(t, vm._audio.IsPlaying);
            else 
                _audio.OnTrackChanged -= onTrackChanged;
        };
        _audio.OnTrackChanged += onTrackChanged;

        Action<bool, bool> onPlaybackState = null!;
        onPlaybackState = (playing, paused) =>
        {
            if (weakSelf.TryGetTarget(out var vm)) 
                vm.UpdateActiveState(vm._audio.CurrentTrack, playing);
            else 
                _audio.OnPlaybackStateChanged -= onPlaybackState;
        };
        _audio.OnPlaybackStateChanged += onPlaybackState;

        // 2. Library Updates
        Action<TrackInfo> onTrackUpdated = null!;
        onTrackUpdated = t =>
        {
            if (weakSelf.TryGetTarget(out var vm))
            {
                if (t.Id == vm.Track.Id)
                {
                    vm.IsLiked = t.IsLiked;
                    vm.IsDownloaded = t.IsDownloaded;
                    vm.RaisePropertyChanged(nameof(IsDownloaded));
                }
            }
            else _library.OnTrackUpdated -= onTrackUpdated;
        };
        _library.OnTrackUpdated += onTrackUpdated;

        // 3. Download Progress
        Action<string, float> onProgress = null!;
        onProgress = (id, progress) =>
        {
            if (weakSelf.TryGetTarget(out var vm))
            {
                if (id == vm.Track.Id)
                {
                    vm.IsDownloading = true;
                    vm.DownloadProgress = progress;
                }
            }
            else _downloads.OnProgress -= onProgress;
        };
        _downloads.OnProgress += onProgress;

        // 4. Download Completion
        Action<string, bool, string?> onDownloadCompleted = null!;
        onDownloadCompleted = (id, success, path) =>
        {
            if (weakSelf.TryGetTarget(out var vm))
            {
                if (id == vm.Track.Id)
                {
                    vm.IsDownloading = false;
                    vm.DownloadProgress = 0;
                    if (success)
                    {
                        vm.IsDownloaded = true;
                        vm.RaisePropertyChanged(nameof(IsDownloaded));
                    }
                }
            }
            else _downloads.OnCompleted -= onDownloadCompleted;
        };
        _downloads.OnCompleted += onDownloadCompleted;
    }

    private void UpdateActiveState(TrackInfo? currentTrack, bool isPlaying)
    {
        bool isMe = currentTrack?.Id == Track.Id;
        IsActive = isMe;
        IsPlaying = isMe && isPlaying;
    }

    /// <summary>
    /// Обновляет действие воспроизведения (для переиспользования VM в разных контекстах)
    /// </summary>
    public void UpdatePlayAction(Action<TrackInfo>? onPlay)
    {
        _onPlay = onPlay;
    }
    
    /// <summary>
    /// Устанавливает активное состояние (вызывается из QueueViewModel)
    /// </summary>
    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        IsPlaying = isActive && _audio.IsPlaying;
    }
    
    /// <summary>
    /// Очистка ресурсов (отписка от событий)
    /// </summary>
    public void Cleanup()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
    }
}


