// Features/Shared/TrackItemViewModel.cs
using System.Reactive;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Shared;

/// <summary>
/// ViewModel, представляющая один музыкальный трек в списке.
/// Отвечает за отображение состояния (играет, пауза, лайк) и команд.
/// </summary>
public sealed class TrackItemViewModel : ViewModelBase, IDisposable
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly StreamCacheManager _cacheManager;

    private Action<TrackInfo>? _onPlay;

    private bool _isDisposed;

    private readonly Action<TrackInfo?> _onTrackChangedHandler;
    private readonly Action<bool, bool> _onPlaybackStateHandler;
    private readonly Action<TrackInfo> _onTrackUpdatedHandler;
    private readonly Action<string, float> _onDownloadProgressHandler;
    private readonly Action<string, bool, string?> _onDownloadCompletedHandler;
    private readonly Action<string, string, int, bool> _onFormatCachedHandler;

    #endregion

    #region Properties

    /// <summary>
    /// Контекст источника (ID плейлиста или "search", "home", etc.)
    /// Используется для определения, нужно ли заменять очередь.
    /// </summary>
    public string? SourceContextId { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
    private bool _isSelected;

    public bool IsDisposed => _isDisposed;
    public TrackInfo Track { get; }
    public string Id => Track.Id;
    public string Title { get; }
    public string Author { get; }
    public TimeSpan Duration { get; }

    // Форматированная строка времени (mm:ss) для UI
    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string ThumbnailUrl { get; }

    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDownloaded { get; set; }
    [Reactive] public bool IsDownloading { get; set; }
    [Reactive] public float DownloadProgress { get; set; }
    [Reactive] public bool IsQueueContext { get; set; }
    [Reactive] public bool IsMenuOpen { get; set; }

    // Контекст плейлиста (для отображения кнопки удаления в меню)
    [Reactive] public bool IsPlaylistContext { get; set; }

    public bool ShowAddToQueue => !IsQueueContext;

    // Текст для кнопки скачивания в меню
    public string DownloadStatusText => IsDownloaded ? L["Track_Downloaded"] : L["Track_Download"];

    #endregion

    #region Actions Injection

    // Внешние действия, назначаемые родительской VM
    public Action<TrackInfo>? StartRadioAction { get; set; }
    public Action<TrackInfo>? RemoveFromPlaylistAction { get; set; }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    // Context Menu Commands
    public ReactiveCommand<Unit, Unit> StartRadioCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromQueueCommand { get; }

    // Menu State Commands
    public ReactiveCommand<Unit, Unit> MenuOpenedCommand { get; }
    public ReactiveCommand<Unit, Unit> MenuClosedCommand { get; }

    #endregion

    #region Constructors

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
        _cacheManager = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<StreamCacheManager>(Program.Services);
        _onPlay = onPlay;

        Title = track.Title;
        Author = track.Author;
        Duration = track.Duration;
        ThumbnailUrl = track.ThumbnailUrl;

        _onTrackChangedHandler = OnAudioTrackChanged;
        _onPlaybackStateHandler = OnAudioPlaybackStateChanged;
        _onTrackUpdatedHandler = OnLibraryTrackUpdated;
        _onDownloadProgressHandler = OnDownloadProgress;
        _onDownloadCompletedHandler = OnDownloadCompleted;
        _onFormatCachedHandler = OnFormatCached;

        _audio.OnTrackChanged += _onTrackChangedHandler;
        _audio.OnPlaybackStateChanged += _onPlaybackStateHandler;
        _library.OnTrackUpdated += _onTrackUpdatedHandler;
        _downloads.OnProgress += _onDownloadProgressHandler;
        _downloads.OnCompleted += _onDownloadCompletedHandler;
        _cacheManager.OnFormatCached += _onFormatCachedHandler;

        IsDownloading = _downloads.IsDownloading(track.Id);
        if (IsDownloading)
            DownloadProgress = _downloads.GetProgress(track.Id);

        IsDownloaded = track.IsDownloaded;
        IsLiked = track.IsLiked;

        UpdateActiveState(_audio.CurrentTrack, _audio.IsPlaying);

        // --- Commands ---

        PlayCommand = ReactiveCommand.CreateFromTask(ExecutePlayAsync);
        ToggleLikeCommand = ReactiveCommand.CreateFromTask(ExecuteToggleLikeAsync);
        AddToQueueCommand = ReactiveCommand.Create(ExecuteAddToQueue);

        // Menu State Commands
        MenuOpenedCommand = ReactiveCommand.Create(() => { IsMenuOpen = true; });
        MenuClosedCommand = ReactiveCommand.Create(() => { IsMenuOpen = false; });

        // 1. Start Radio
        StartRadioCommand = ReactiveCommand.Create(() =>
        {
            if (StartRadioAction != null)
                StartRadioAction(Track);
            else
                Log.Info($"[TrackItem] Start radio for {Title} (Action not bound)");
        });

        // 2. Download
        DownloadTrackCommand = ReactiveCommand.Create(() =>
        {
            if (!IsDownloaded && !IsDownloading)
            {
                _downloads.StartDownload(Track);
            }
        });

        // 3. Remove From Playlist
        RemoveFromPlaylistCommand = ReactiveCommand.Create(() =>
        {
            RemoveFromPlaylistAction?.Invoke(Track);
        }, this.WhenAnyValue(x => x.IsPlaylistContext));

        // 4. Remove From Queue
        RemoveFromQueueCommand = ReactiveCommand.Create(() =>
        {
            _audio.RemoveFromQueue(Track);
        }, this.WhenAnyValue(x => x.IsQueueContext));

        this.WhenAnyValue(x => x.IsQueueContext)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowAddToQueue)));

        this.WhenAnyValue(x => x.IsDownloaded)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(DownloadStatusText)));
    }

    #endregion

    #region Event Handlers

    private void OnAudioTrackChanged(TrackInfo? track)
    {
        UpdateActiveState(track, _audio.IsPlaying);
    }

    private void OnAudioPlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        UpdateActiveState(_audio.CurrentTrack, isPlaying);
    }

    private void OnLibraryTrackUpdated(TrackInfo updatedTrack)
    {
        if (updatedTrack.Id != Id) return;

        // Обновляем состояние из canonical объекта
        IsLiked = updatedTrack.IsLiked;
        IsDownloaded = updatedTrack.IsDownloaded;

        // Принудительно уведомляем UI
        this.RaisePropertyChanged(nameof(IsLiked));
        this.RaisePropertyChanged(nameof(IsDownloaded));
        this.RaisePropertyChanged(nameof(DownloadStatusText));
    }

    private void OnDownloadProgress(string trackId, float progress)
    {
        if (trackId == Id)
        {
            IsDownloading = true;
            DownloadProgress = progress;
        }
    }

    private void OnDownloadCompleted(string trackId, bool success, string? path)
    {
        if (trackId == Id)
        {
            IsDownloading = false;
            DownloadProgress = 0;
            if (success)
            {
                IsDownloaded = true;
                this.RaisePropertyChanged(nameof(IsDownloaded));
                this.RaisePropertyChanged(nameof(DownloadStatusText));
            }
        }
    }

    /// <summary>
    /// Вызывается когда формат полностью закэширован (стриминг завершён).
    /// </summary>
    private void OnFormatCached(string trackId, string container, int bitrate, bool isDownloaded)
    {
        if (trackId != Id) return;

        Log.Debug($"[TrackItemVM] OnFormatCached received for {Id}, isDownloaded={isDownloaded}, Track.IsDownloaded={Track.IsDownloaded}");

        // Обновляем из канонического объекта Track
        if (Track.IsDownloaded != IsDownloaded)
        {
            IsDownloaded = Track.IsDownloaded;
            this.RaisePropertyChanged(nameof(IsDownloaded));
            this.RaisePropertyChanged(nameof(DownloadStatusText));
        }
    }

    #endregion

    #region Methods

    private void UpdateActiveState(TrackInfo? currentTrack, bool isPlaying)
    {
        bool isMe = currentTrack?.Id == Id;
        IsActive = isMe;
        IsPlaying = isMe && isPlaying;
    }

    private async Task ExecutePlayAsync()
    {
        if (_audio.CurrentTrack?.Id == Id)
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }
        else
        {
            if (_onPlay != null)
                _onPlay(Track);
            else
                _ = Task.Run(async () => await _audio.PlayTrackAsync(Track));
        }
    }

    private async Task ExecuteToggleLikeAsync()
    {
        await _manager.ToggleLikeAsync(Track);
    }

    private void ExecuteAddToQueue()
    {
        _audio.Enqueue(Track);
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay)
    {
        _onPlay = onPlay;
    }

    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        IsPlaying = isActive && _audio.IsPlaying;
    }

    public void Cleanup()
    {
        Dispose();
    }

    #endregion

    #region IDisposable Implementation

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _audio.OnTrackChanged -= _onTrackChangedHandler;
        _audio.OnPlaybackStateChanged -= _onPlaybackStateHandler;
        _library.OnTrackUpdated -= _onTrackUpdatedHandler;
        _downloads.OnProgress -= _onDownloadProgressHandler;
        _downloads.OnCompleted -= _onDownloadCompletedHandler;
        _cacheManager.OnFormatCached -= _onFormatCachedHandler;

        GC.SuppressFinalize(this);
    }

    #endregion
}