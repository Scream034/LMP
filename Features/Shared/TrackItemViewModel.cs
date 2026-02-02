using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Shared;

/// <summary>
/// ViewModel для отображения трека в списке.
/// Не дублирует состояние — биндится напрямую к TrackInfo.
/// </summary>
public sealed class TrackItemViewModel : ViewModelBase, IDisposable
{
    #region Fields

    private readonly CompositeDisposable _disposables = new();
    private readonly AudioEngine _audio;
    private readonly MusicLibraryManager _manager;
    private readonly DownloadService _downloads;
    private readonly StreamCacheManager _cacheManager;

    private Action<TrackInfo>? _onPlay;
    private bool _isDisposed;

    #endregion

    #region Properties - Core

    /// <summary>
    /// Канонический объект трека — единственный источник правды.
    /// </summary>
    public TrackInfo Track { get; }

    /// <summary>
    /// Флаг для проверки dispose.
    /// </summary>
    public bool IsDisposed => _isDisposed;

    // Прямые делегаты к Track
    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;

    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    #endregion

    #region Properties - State (реактивные, из Track)

    private readonly ObservableAsPropertyHelper<bool> _isLiked;
    private readonly ObservableAsPropertyHelper<bool> _isDownloaded;
    private readonly ObservableAsPropertyHelper<bool> _isCached;

    /// <summary>
    /// Трек в "Любимое".
    /// </summary>
    public bool IsLiked => _isLiked.Value;

    /// <summary>
    /// Трек скачан в Downloads.
    /// </summary>
    public bool IsDownloaded => _isDownloaded.Value;

    /// <summary>
    /// Трек полностью закэширован (доступен офлайн).
    /// </summary>
    public bool IsCached => _isCached.Value;

    #endregion

    #region Properties - Playback State (локальные)

    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }

    #endregion

    #region Properties - Download Progress (локальные)

    [Reactive] public bool IsDownloading { get; private set; }
    [Reactive] public float DownloadProgress { get; private set; }

    #endregion

    #region Properties - UI State

    [Reactive] public bool IsMenuOpen { get; set; }
    [Reactive] public bool IsSelected { get; set; }
    [Reactive] public bool IsPlaylistContext { get; set; }
    [Reactive] public bool IsQueueContext { get; set; }

    public bool ShowAddToQueue => !IsQueueContext;

    /// <summary>
    /// Текст для кнопки скачивания в меню.
    /// </summary>
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

        // Получаем StreamCacheManager через DI
        _cacheManager = Program.Services.GetRequiredService<StreamCacheManager>();

        // ═══════════════════════════════════════════════════════════════
        // Подписки на свойства Track (реактивные)
        // ═══════════════════════════════════════════════════════════════

        _isLiked = Track.WhenAnyValue(x => x.IsLiked)
            .ToProperty(this, x => x.IsLiked)
            .DisposeWith(_disposables);

        _isDownloaded = Track.WhenAnyValue(x => x.IsDownloaded)
            .ToProperty(this, x => x.IsDownloaded)
            .DisposeWith(_disposables);

        _isCached = Track.WhenAnyValue(x => x.IsCached)
            .ToProperty(this, x => x.IsCached)
            .DisposeWith(_disposables);

        // Уведомляем UI при изменении статусов
        Track.WhenAnyValue(x => x.IsDownloaded, x => x.IsCached)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(DownloadStatusText)))
            .DisposeWith(_disposables);

        // ═══════════════════════════════════════════════════════════════
        // Подписки на события плеера
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
        // Подписки на события скачивания
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
        // Команды
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

        // Сохранить в папку Downloads
        SaveToDownloadsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Track.IsDownloaded) return; // Уже скачан

            if (Track.IsCached)
            {
                // Экспортируем из кэша
                await _cacheManager.ExportTrackToDownloadsAsync(Track.Id);
            }
            else
            {
                // Скачиваем с нуля
                _downloads.StartDownload(Track);
            }
        });

        RemoveFromPlaylistCommand = ReactiveCommand.Create(
            () => RemoveFromPlaylistAction?.Invoke(Track),
            this.WhenAnyValue(x => x.IsPlaylistContext));

        RemoveFromQueueCommand = ReactiveCommand.Create(
            () => _audio.RemoveFromQueue(Track),
            this.WhenAnyValue(x => x.IsQueueContext));

        // Уведомление для ShowAddToQueue
        this.WhenAnyValue(x => x.IsQueueContext)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowAddToQueue)))
            .DisposeWith(_disposables);
    }

    #endregion

    #region Methods

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

    #endregion
}