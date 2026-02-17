using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Shared;

public sealed class TrackItemViewModel : ViewModelBase
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly MusicLibraryManager _manager;
    private readonly DownloadService _downloads;
    private readonly StreamCacheManager _cacheManager;

    private readonly ObservableAsPropertyHelper<bool> _isLiked;
    private readonly ObservableAsPropertyHelper<bool> _isDownloaded;
    private readonly ObservableAsPropertyHelper<bool> _isCached;

    private Action<TrackInfo>? _onPlay;

    #endregion

    #region Properties - Core

    public TrackInfo Track { get; }
    public bool IsDisposed { get; private set; }

    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;

    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    #endregion

    #region Properties - State

    public bool IsLiked => _isLiked.Value;
    public bool IsDownloaded => _isDownloaded.Value;
    public bool IsCached => _isCached.Value;

    #endregion

    #region Properties - Playback State

    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }

    #endregion

    #region Properties - Download Progress

    [Reactive] public bool IsDownloading { get; private set; }
    [Reactive] public float DownloadProgress { get; private set; }

    #endregion

    #region Properties - UI State

    [Reactive] public bool IsMenuOpen { get; set; }
    [Reactive] public bool IsSelected { get; set; }
    [Reactive] public bool IsPlaylistContext { get; set; }
    [Reactive] public bool IsQueueContext { get; set; }

    public bool ShowAddToQueue => !IsQueueContext;

    public string DownloadStatusText
    {
        get
        {
            if (IsDownloaded) return L["Track_Downloaded"] ?? "Downloaded";
            if (IsCached) return L["Track_SaveToFolder"] ?? "Save to folder";
            return L["Track_Download"] ?? "Download";
        }
    }

    #endregion

    #region Properties - Actions

    public Action<TrackInfo>? StartRadioAction { get; set; }
    public Action<TrackInfo>? RemoveFromPlaylistAction { get; set; }
    public string? SourceContextId { get; set; }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> StartRadioCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveToDownloadsCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromQueueCommand { get; }

    #endregion

    #region Constructor

    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        StreamCacheManager cacheManager,
        Action<TrackInfo>? onPlay = null)
    {
        Track = track;
        _audio = audio;
        _manager = manager;
        _downloads = downloads;
        _cacheManager = cacheManager;
        _onPlay = onPlay;

        // ObservableAsPropertyHelper - используем Disposables из ViewModelBase
        _isLiked = Track.WhenAnyValue(x => x.IsLiked)
            .ToProperty(this, x => x.IsLiked)
            .DisposeWith(Disposables);

        _isDownloaded = Track.WhenAnyValue(x => x.IsDownloaded)
            .ToProperty(this, x => x.IsDownloaded)
            .DisposeWith(Disposables);

        _isCached = Track.WhenAnyValue(x => x.IsCached)
            .ToProperty(this, x => x.IsCached)
            .DisposeWith(Disposables);

        Track.WhenAnyValue(x => x.IsDownloaded, x => x.IsCached)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(DownloadStatusText)))
            .DisposeWith(Disposables);

        // Audio subscriptions
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => UpdatePlaybackState(t, _audio.IsPlaying))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
                h => (a, b) => h((a, b)),
                h => _audio.OnPlaybackStateChanged += h,
                h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => UpdatePlaybackState(_audio.CurrentTrack, x.Item1))
            .DisposeWith(Disposables);

        // Download subscriptions
        Observable.FromEvent<Action<string, float>, (string, float)>(
                h => (id, p) => h((id, p)),
                h => _downloads.OnProgress += h,
                h => _downloads.OnProgress -= h)
            .Where(x => x.Item1 == Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                IsDownloading = true;
                DownloadProgress = x.Item2;
            })
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<string, bool, string?>, (string, bool, string?)>(
                h => (id, ok, path) => h((id, ok, path)),
                h => _downloads.OnCompleted += h,
                h => _downloads.OnCompleted -= h)
            .Where(x => x.Item1 == Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                IsDownloading = false;
                DownloadProgress = 0;
            })
            .DisposeWith(Disposables);

        // Initialize state
        IsDownloading = _downloads.IsDownloading(track.Id);
        if (IsDownloading) DownloadProgress = _downloads.GetProgress(track.Id);
        UpdatePlaybackState(_audio.CurrentTrack, _audio.IsPlaying);

        // Commands - используем CreateCommand из ViewModelBase
        PlayCommand = CreateCommand(ReactiveCommand.CreateFromTask(PlayAsync));
        ToggleLikeCommand = CreateCommand(ReactiveCommand.CreateFromTask(() => _manager.ToggleLikeAsync(Track)));
        AddToQueueCommand = CreateCommand(ReactiveCommand.Create(() => _audio.Enqueue(Track)));
        StartRadioCommand = CreateCommand(ReactiveCommand.Create(() => StartRadioAction?.Invoke(Track)));
        SaveToDownloadsCommand = CreateCommand(ReactiveCommand.CreateFromTask(SaveToDownloadsAsync));

        RemoveFromPlaylistCommand = CreateCommand(
            ReactiveCommand.Create(
                () => RemoveFromPlaylistAction?.Invoke(Track),
                this.WhenAnyValue(x => x.IsPlaylistContext)));

        RemoveFromQueueCommand = CreateCommand(
            ReactiveCommand.Create(
                () => _audio.RemoveFromQueue(Track),
                this.WhenAnyValue(x => x.IsQueueContext)));

        // UI state subscriptions
        this.WhenAnyValue(x => x.IsQueueContext)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowAddToQueue)))
            .DisposeWith(Disposables);

        MemoryDiagnostics.TrackInstance("TrackVM.Created");
    }

    #endregion

    #region Command Handlers

    private async Task PlayAsync()
    {
        if (_audio.CurrentTrack?.Id == Id)
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        else
            _onPlay?.Invoke(Track);
    }

    private async Task SaveToDownloadsAsync()
    {
        if (Track.IsDownloaded) return;

        if (Track.IsCached)
        {
            await _cacheManager.ExportTrackToDownloadsAsync(Track.Id);
        }
        else
        {
            _downloads.StartDownload(Track);
        }
    }

    #endregion

    #region Methods

    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        bool isMe = currentTrack?.Id == Id;
        IsActive = isMe;
        IsPlaying = isMe && isPlaying;
    }

    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        IsPlaying = isActive && _audio.IsPlaying;
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay) => _onPlay = onPlay;

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        
        if (disposing)
        {
            // Log.Debug($"[TrackVM] Disposing {Id} ({Title})");

            // Очищаем делегаты
            _onPlay = null;
            StartRadioAction = null;
            RemoveFromPlaylistAction = null;

            MemoryDiagnostics.UntrackInstance("TrackVM.Created");
        }
        
        base.Dispose(disposing);  // Это вызовет Disposables.Dispose()
        IsDisposed = true;
    }

    #endregion
}