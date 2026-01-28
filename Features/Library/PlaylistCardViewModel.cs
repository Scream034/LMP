// Features/Library/PlaylistCardViewModel.cs
using System.Reactive;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Library;

/// <summary>
/// ViewModel для карточки плейлиста в сетке библиотеки.
/// </summary>
public sealed class PlaylistCardViewModel : ViewModelBase, IDisposable
{
    #region Fields

    private readonly Func<string, Task>? _onDelete;
    private readonly Func<Core.Models.Playlist, Task> _addToQueueAction;
    private readonly Action<string> _onOpen;
    private readonly EventHandler<string> _languageChangedHandler;
    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>Данные плейлиста.</summary>
    public Core.Models.Playlist Playlist { get; }

    /// <summary>ID плейлиста.</summary>
    public string Id => Playlist.Id;

    /// <summary>Название плейлиста.</summary>
    public string Name => Playlist.Name;

    /// <summary>URL обложки.</summary>
    public string? ThumbnailUrl => Playlist.ThumbnailUrl;

    /// <summary>Количество треков.</summary>
    [Reactive] public int TrackCount { get; set; }

    /// <summary>Плейлист сохранен локально.</summary>
    public bool IsLocal => Playlist.IsLocal;

    /// <summary>Плейлист синхронизирован.</summary>
    public bool IsSynced => Playlist.IsFromAccount;

    /// <summary>Плейлист только для чтения.</summary>
    public bool IsReadOnly => !Playlist.IsEditable;

    /// <summary>Можно ли удалить плейлист.</summary>
    public bool CanDelete => Playlist.Id != LibraryService.LikedPlaylistId;

    /// <summary>Это плейлист "Любимое".</summary>
    public bool IsLikedPlaylist => Playlist.Id == LibraryService.LikedPlaylistId;

    /// <summary>Форматированное количество треков (локализованное).</summary>
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
        
        // Initialize track count from playlist
        TrackCount = playlist.TrackCount;

        OpenCommand = ReactiveCommand.Create(() =>
        {
            if (!_isDisposed) _onOpen(Playlist.Id);
        });

        var canDelete = this.WhenAnyValue(x => x.CanDelete);
        DeleteCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed && _onDelete != null)
            {
                await _onDelete(Playlist.Id);
            }
        }, canDelete);

        AddToQueueCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed)
            {
                await _addToQueueAction(Playlist);
            }
        });

        // Update formatted text when count changes
        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)));

        // Update on language change
        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    #endregion

    #region Helpers

    private static string FormatTrackCount(int count)
    {
        if (count == 0)
            return LocalizationService.Instance["Playlist_Empty"];
        
        // Use plural helper if available
        return LocalizationService.Instance.GetPlural("Playlist_TracksCount", count);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        GC.SuppressFinalize(this);
    }

    #endregion
}