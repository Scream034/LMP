using System.Reactive;
using System.Reactive.Disposables;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Library;

public sealed class PlaylistCardViewModel : ViewModelBase
{
    #region Fields
    private readonly Func<string, Task>? _onDelete;
    private readonly Func<Core.Models.Playlist, Task> _addToQueueAction;
    private readonly Action<string> _onOpen;
    private readonly EventHandler<string> _languageChangedHandler;
    private bool _isDisposed;
    #endregion

    #region Properties
    public Core.Models.Playlist Playlist { get; }
    public string Id => Playlist.Id;

    [Reactive] public string Name { get; private set; }
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; set; }

    public bool IsLocal => Playlist.IsLocal;
    public bool IsSynced => Playlist.IsFromAccount;
    public bool IsReadOnly => !Playlist.IsEditable;
    public bool CanDelete => Playlist.Id != LibraryService.LikedPlaylistId;
    public bool IsLikedPlaylist => Playlist.Id == LibraryService.LikedPlaylistId;
    public string FormattedTrackCount => FormatTrackCount(TrackCount);

    /// <summary>
    /// Показывать обложку? true если есть URL И это НЕ liked playlist.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>
    /// Показывать placeholder иконку? Только если нет обложки и не liked.
    /// </summary>
    public bool ShowPlaceholder => string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>
    /// Флаг видимости для staggered animation.
    /// </summary>
    [Reactive] public bool IsVisible { get; set; }
    #endregion

    #region Commands
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    #endregion

    #region Constructor
    /// <summary>
    /// Конструктор с явным trackCount из БД (GetAllPlaylistsWithCountsAsync).
    /// Не полагается на playlist.TrackCount, который может быть 0.
    /// </summary>
    public PlaylistCardViewModel(
        Core.Models.Playlist playlist,
        int trackCount,
        Action<string> onOpen,
        Func<Core.Models.Playlist, Task> addToQueueAction,
        Func<string, Task>? onDelete = null)
    {
        Playlist = playlist;
        _onOpen = onOpen;
        _addToQueueAction = addToQueueAction;
        _onDelete = onDelete;

        Name = playlist.Name;
        TrackCount = trackCount;
        ThumbnailUrl = UpscaleThumbnailUrl(playlist.ThumbnailUrl);

        OpenCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            if (!_isDisposed) _onOpen(Playlist.Id);
        }));

        var canDelete = this.WhenAnyValue(x => x.CanDelete);
        DeleteCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed && _onDelete != null) await _onDelete(Playlist.Id);
        }, canDelete));

        AddToQueueCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed) await _addToQueueAction(Playlist);
        }));

        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(HasThumbnail));
                this.RaisePropertyChanged(nameof(ShowPlaceholder));
            })
            .DisposeWith(Disposables);

        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }
    #endregion

    /// <summary>
    /// Обновляет свойства карточки из данных БД без пересоздания VM.
    /// Используется Diff-алгоритмом в LibraryViewModel.
    /// </summary>
    public void UpdateFrom(Core.Models.Playlist playlist, int trackCount)
    {
        if (_isDisposed) return;

        if (Name != playlist.Name)
        {
            Playlist.StoredName = playlist.StoredName;
            Name = playlist.Name;
        }

        if (TrackCount != trackCount)
        {
            TrackCount = trackCount;
        }

        var newUrl = UpscaleThumbnailUrl(playlist.ThumbnailUrl);
        if (ThumbnailUrl != newUrl)
        {
            Playlist.ThumbnailUrl = playlist.ThumbnailUrl;
            ThumbnailUrl = newUrl;
        }
    }

    /// <summary>
    /// Заменяет low-res YouTube thumbnails на HD версию.
    /// </summary>
    private static string? UpscaleThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.Contains("hqdefault.jpg"))
            return url.Replace("hqdefault.jpg", "maxresdefault.jpg");

        if (url.Contains("mqdefault.jpg"))
            return url.Replace("mqdefault.jpg", "maxresdefault.jpg");

        return url;
    }

    private static string FormatTrackCount(int count)
    {
        if (count == 0) return LocalizationService.Instance["Playlist_Empty"];
        return LocalizationService.Instance.GetPlural("Playlist_TracksCount", count);
    }

    public void Show()
    {
        if (!_isDisposed)
            IsVisible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        }
        base.Dispose(disposing);
    }
}