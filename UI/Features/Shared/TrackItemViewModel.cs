using System.ComponentModel;
using System.Windows.Input;
using ReactiveUI;


namespace LMP.UI.Features.Shared;

public sealed partial class TrackItemViewModel : ViewModelBase
{
    #region Weak Event Subscription

    /// <summary>
    /// Разрывает сильную ссылку Track → VM через делегат.
    ///
    /// Проблема: TrackInfo живёт в TrackRegistry._pinned (GC Root).
    /// Track.PropertyChanged += handler создаёт делегат с Target=VM.
    /// Пока Track жив (pinned) — жив делегат — жива VM — жива вся ReactiveUI-цепочка (~15 объектов).
    /// VM никогда не собирается GC, кэш фабрики растёт бесконечно.
    ///
    /// Решение: Handle() содержит WeakReference&lt;VM&gt;.
    /// Track.PropertyChanged держит сильную ссылку на этот маленький объект (~40 байт),
    /// но НЕ на VM. VM собирается GC когда страница навигации очищает коллекцию.
    /// При следующем Handle() — TryGetTarget возвращает false → автоматический unsub.
    /// </summary>
    private sealed class WeakPropertyChangedSubscription
    {
        private readonly WeakReference<TrackItemViewModel> _weak;
        private readonly INotifyPropertyChanged _source;

        internal WeakPropertyChangedSubscription(TrackItemViewModel vm, INotifyPropertyChanged source)
        {
            _weak = new WeakReference<TrackItemViewModel>(vm);
            _source = source;
            source.PropertyChanged += Handle;
        }

        private void Handle(object? sender, PropertyChangedEventArgs e)
        {
            if (_weak.TryGetTarget(out var vm))
                vm.OnTrackPropertyChanged(sender, e);
            else
                _source.PropertyChanged -= Handle;
        }

        internal void Unsubscribe() => _source.PropertyChanged -= Handle;
    }

    private readonly WeakPropertyChangedSubscription _trackSubscription;

    #endregion

    private readonly AudioEngine _audio;
    private readonly MusicLibraryManager _manager;
    private readonly DownloadService _downloads;
    private readonly DialogService _dialog;
    private readonly LibraryService _library;

    private Action<TrackInfo>? _onPlay;

    public TrackInfo Track { get; }
    public bool IsDisposed { get; private set; }

    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;

    /// <summary>
    /// Проброс Track.IsLiked для одноуровневого XAML-биндинга.
    /// Обновляется через OnTrackPropertyChanged → устраняет DataContextNode + 2× PropertyAccessorNode.
    /// </summary>
    public bool IsLiked => Track.IsLiked;

    /// <summary>
    /// Проброс Track.IsDownloaded для одноуровневого XAML-биндинга.
    /// </summary>
    public bool IsDownloaded => Track.IsDownloaded;

    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    [Reactive] public partial bool IsActive { get; private set; }
    [Reactive] public partial bool IsPlaying { get; private set; }
    [Reactive] public partial bool IsDownloading { get; private set; }
    [Reactive] public partial float DownloadProgress { get; private set; }
    [Reactive] public partial bool IsMenuOpen { get; set; }
    [Reactive] public partial bool IsSelected { get; set; }
    [Reactive] public partial bool IsPlaylistContext { get; set; }
    [Reactive] public partial bool IsQueueContext { get; set; }

    public bool ShowAddToQueue => !IsQueueContext;

    /// <summary>
    /// Флаг отображения иконки скачанного трека.
    /// Исключает наложение на прогресс-бар при активной загрузке.
    /// </summary>
    public bool ShowDownloadedIcon => Track.IsDownloaded && !IsDownloading;

    /// <summary>
    /// Замена MultiBinding в AXAML. Вычисляется на стороне VM без аллокаций.
    /// Исключает наложение на прогресс-бар при активной загрузке.
    /// </summary>
    public bool ShowCachedIcon => Track.IsCached && !Track.IsDownloaded && !IsDownloading;

    public string DownloadStatusText
    {
        get
        {
            if (Track.IsDownloaded) return L["Track_Downloaded"] ?? "Downloaded";
            if (Track.IsCached) return L["Track_SaveToFolder"] ?? "Save to folder";
            return L["Track_Download"] ?? "Download";
        }
    }

    public Action<TrackInfo>? StartRadioAction { get; set; }
    public Action<TrackInfo>? RemoveFromPlaylistAction { get; set; }
    public string? SourceContextId { get; set; }

    public ICommand PlayCommand { get; }
    public ICommand ToggleLikeCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand StartRadioCommand { get; }
    public ICommand SaveToDownloadsCommand { get; }
    public ICommand RemoveFromPlaylistCommand { get; }
    public ICommand RemoveFromQueueCommand { get; }
    public ICommand AddToPlaylistCommand { get; }
    public ICommand CopyLinkCommand { get; }

    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        DialogService dialog,
        LibraryService library,
        Action<TrackInfo>? onPlay = null)
    {
        Track = track;
        _audio = audio;
        _manager = manager;
        _downloads = downloads;
        _dialog = dialog;
        _library = library;
        _onPlay = onPlay;

        PlayCommand = new TrackAsyncCommand(PlayAsync);
        ToggleLikeCommand = new TrackAsyncCommand(() => _manager.ToggleLikeAsync(Track));
        AddToQueueCommand = new TrackSyncCommand(() => _audio.Enqueue(Track));
        StartRadioCommand = new TrackSyncCommand(() => StartRadioAction?.Invoke(Track));
        SaveToDownloadsCommand = new TrackAsyncCommand(SaveToDownloadsAsync);
        AddToPlaylistCommand = new TrackAsyncCommand(AddToPlaylistAsync);
        CopyLinkCommand = new TrackAsyncCommand(CopyLinkAsync);

        RemoveFromPlaylistCommand = new TrackSyncCommand(() =>
        {
            if (IsPlaylistContext) RemoveFromPlaylistAction?.Invoke(Track);
        });

        RemoveFromQueueCommand = new TrackSyncCommand(() =>
        {
            if (IsQueueContext) _audio.RemoveFromQueue(Track);
        });

        _trackSubscription = new WeakPropertyChangedSubscription(this, track);
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Track.IsLiked):
                this.RaisePropertyChanged(nameof(IsLiked));
                break;

            case nameof(Track.IsDownloaded):
                if (Track.IsDownloaded && IsDownloading)
                {
                    IsDownloading = false;
                    DownloadProgress = 0f;
                }
                this.RaisePropertyChanged(nameof(IsDownloaded));
                this.RaisePropertyChanged(nameof(DownloadStatusText));
                this.RaisePropertyChanged(nameof(ShowDownloadedIcon));
                this.RaisePropertyChanged(nameof(ShowCachedIcon));
                break;

            case nameof(Track.IsCached):
                this.RaisePropertyChanged(nameof(DownloadStatusText));
                this.RaisePropertyChanged(nameof(ShowCachedIcon));
                break;
        }
    }

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
            var cache = AudioSourceFactory.GlobalCache;
            if (cache == null) return;

            bool success = await cache.ExportTrackToDownloadsAsync(
                Track.Id,
                async id => await _library.GetTrackAsync(id),
                async t => await _library.AddOrUpdateTrackAsync(t));

            if (success) Track.IsDownloaded = true;
        }
        else
        {
            _downloads.StartDownload(Track);
        }
    }

    public void SetActive(bool isActive, bool isPlaying)
    {
        IsActive = isActive;
        IsPlaying = isActive && isPlaying;
    }

    public void SetDownloadState(bool isDownloading, float progress)
    {
        if (Track.IsDownloaded)
            isDownloading = false;

        IsDownloading = isDownloading;
        DownloadProgress = isDownloading ? progress : 0f;

        // Явно обновляем триггеры видимости элементов без участия конвертеров
        this.RaisePropertyChanged(nameof(ShowDownloadedIcon));
        this.RaisePropertyChanged(nameof(ShowCachedIcon));

        if (!isDownloading)
        {
            this.RaisePropertyChanged(nameof(DownloadStatusText));
        }
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay) => _onPlay = onPlay;

    private async Task AddToPlaylistAsync()
    {
        var selectedIds = await _dialog.ShowAddToPlaylistDialogAsync(Track);
        if (selectedIds.Count == 0) return;

        foreach (var playlistId in selectedIds)
            await _manager.AddTrackToPlaylistAsync(playlistId, Track);
    }

    private async Task CopyLinkAsync()
    {
        if (IsDisposed) return;

        var url = Track.Url;
        if (string.IsNullOrEmpty(url))
            url = $"https://www.youtube.com/watch?v={Track.GetRawId()}";

        if (string.IsNullOrEmpty(url))
        {
            CopyHintService.Instance.Show(
                L["Track_CopyLink_NoUrl"] ?? "No link available",
                CopyHintKind.Warning,
                null);
            return;
        }

        await Clipboard.SetTextAsync(url);

        CopyHintService.Instance.Show(
            L["Track_Copied"] ?? "Copied!",
            CopyHintKind.Success,
            null);
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            _trackSubscription.Unsubscribe();
            _onPlay = null;
            StartRadioAction = null;
            RemoveFromPlaylistAction = null;
        }
        base.Dispose(disposing);
        IsDisposed = true;
    }
}
