using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Shared;

/// <summary>
/// Легковесная обёртка над TrackInfo для UI.
/// НЕ дублирует состояние — всё берётся напрямую из Track.
/// </summary>
public sealed class TrackItemViewModel : ViewModelBase, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly AudioEngine _audio;
    private readonly MusicLibraryManager _manager;
    private readonly DownloadService _downloads;
    
    private Action<TrackInfo>? _onPlay;
    private bool _isDisposed;

    // === Главный объект — единственный источник правды ===
    public TrackInfo Track { get; }

    // === Прямые делегаты к Track (для удобства биндинга) ===
    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;
    
    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    // === Флаг для фабрики ===
    public bool IsDisposed => _isDisposed;

    // === Состояние воспроизведения (локальное для VM) ===
    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    
    // === Состояние скачивания (прогресс — локальный) ===
    [Reactive] public bool IsDownloading { get; private set; }
    [Reactive] public float DownloadProgress { get; private set; }

    // === UI State ===
    [Reactive] public bool IsMenuOpen { get; set; }
    [Reactive] public bool IsSelected { get; set; }
    [Reactive] public bool IsPlaylistContext { get; set; }
    [Reactive] public bool IsQueueContext { get; set; }

    public bool ShowAddToQueue => !IsQueueContext;

    // === Прокси-свойства для XAML ===
    private readonly ObservableAsPropertyHelper<bool> _isLiked;
    private readonly ObservableAsPropertyHelper<bool> _isDownloaded;
    
    public bool IsLiked => _isLiked.Value;
    public bool IsDownloaded => _isDownloaded.Value;

    public string DownloadStatusText => IsDownloaded 
        ? (L["Track_Downloaded"] ?? "Downloaded") 
        : (L["Track_Download"] ?? "Download");

    // === Actions ===
    public Action<TrackInfo>? StartRadioAction { get; set; }
    public Action<TrackInfo>? RemoveFromPlaylistAction { get; set; }
    public string? SourceContextId { get; set; }

    // === Commands ===
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> StartRadioCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromQueueCommand { get; }

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
        _manager = manager;
        _downloads = downloads;
        _onPlay = onPlay;

        // ═══════════════════════════════════════════════════════════════
        // Подписываемся на изменения САМОГО Track объекта
        // ═══════════════════════════════════════════════════════════════
        
        _isLiked = Track.WhenAnyValue(x => x.IsLiked)
            .ToProperty(this, x => x.IsLiked)
            .DisposeWith(_disposables);

        _isDownloaded = Track.WhenAnyValue(x => x.IsDownloaded)
            .ToProperty(this, x => x.IsDownloaded)
            .DisposeWith(_disposables);

        // Уведомляем UI при изменении IsDownloaded
        Track.WhenAnyValue(x => x.IsDownloaded)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(DownloadStatusText)))
            .DisposeWith(_disposables);

        // ═══════════════════════════════════════════════════════════════
        // Подписка на события плеера
        // ═══════════════════════════════════════════════════════════════
        
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => UpdatePlaybackState(t, _audio.IsPlaying))
            .DisposeWith(_disposables);

        Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
                h => (a, b) => h((a, b)),
                h => _audio.OnPlaybackStateChanged += h,
                h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => UpdatePlaybackState(_audio.CurrentTrack, x.Item1))
            .DisposeWith(_disposables);

        // ═══════════════════════════════════════════════════════════════
        // Подписка на события скачивания
        // ═══════════════════════════════════════════════════════════════
        
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
            .DisposeWith(_disposables);

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
            .DisposeWith(_disposables);

        // Инициализация состояния
        IsDownloading = _downloads.IsDownloading(track.Id);
        if (IsDownloading) DownloadProgress = _downloads.GetProgress(track.Id);
        UpdatePlaybackState(_audio.CurrentTrack, _audio.IsPlaying);

        // ═══════════════════════════════════════════════════════════════
        // Commands
        // ═══════════════════════════════════════════════════════════════

        PlayCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_audio.CurrentTrack?.Id == Id)
                await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
            else
                _onPlay?.Invoke(Track);
        });

        ToggleLikeCommand = ReactiveCommand.CreateFromTask(() => _manager.ToggleLikeAsync(Track));

        AddToQueueCommand = ReactiveCommand.Create(() => _audio.Enqueue(Track));

        StartRadioCommand = ReactiveCommand.Create(() => StartRadioAction?.Invoke(Track));

        DownloadTrackCommand = ReactiveCommand.Create(() =>
        {
            if (!Track.IsDownloaded && !IsDownloading)
                _downloads.StartDownload(Track);
        });

        RemoveFromPlaylistCommand = ReactiveCommand.Create(
            () => RemoveFromPlaylistAction?.Invoke(Track),
            this.WhenAnyValue(x => x.IsPlaylistContext));

        RemoveFromQueueCommand = ReactiveCommand.Create(
            () => _audio.RemoveFromQueue(Track),
            this.WhenAnyValue(x => x.IsQueueContext));

        this.WhenAnyValue(x => x.IsQueueContext)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowAddToQueue)))
            .DisposeWith(_disposables);
    }

    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        bool isMe = currentTrack?.Id == Id;
        IsActive = isMe;
        IsPlaying = isMe && isPlaying;
    }

    /// <summary>
    /// Устанавливает активное состояние (используется QueueViewModel).
    /// </summary>
    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        IsPlaying = isActive && _audio.IsPlaying;
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay) => _onPlay = onPlay;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _disposables.Dispose();
    }
}