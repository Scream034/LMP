// Features/Library/PlaylistCardViewModel.cs
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
    public string Name => Playlist.Name;
    public string? ThumbnailUrl => Playlist.ThumbnailUrl;
    [Reactive] public int TrackCount { get; set; }
    public bool IsLocal => Playlist.IsLocal;
    public bool IsSynced => Playlist.IsFromAccount;
    public bool IsReadOnly => !Playlist.IsEditable;
    public bool CanDelete => Playlist.Id != LibraryService.LikedPlaylistId;
    public bool IsLikedPlaylist => Playlist.Id == LibraryService.LikedPlaylistId;
    public string FormattedTrackCount => FormatTrackCount(TrackCount);
    #endregion

    #region Commands
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    #endregion

    #region Constructor
    public PlaylistCardViewModel(
        Core.Models.Playlist playlist,
        Action<string> onOpen,
        Func<Core.Models.Playlist, Task> addToQueueAction,
        Func<string, Task>? onDelete = null)
    {
        Playlist = playlist;
        _onOpen = onOpen;
        _addToQueueAction = addToQueueAction;
        _onDelete = onDelete;
        TrackCount = playlist.TrackCount;

        // Используем CreateCommand из ViewModelBase для предотвращения утечек
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

        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }
    #endregion
    
    private static string FormatTrackCount(int count)
    {
        if (count == 0) return LocalizationService.Instance["Playlist_Empty"];
        return LocalizationService.Instance.GetPlural("Playlist_TracksCount", count);
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