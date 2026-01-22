using System.Reactive;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;

namespace MyLiteMusicPlayer.ViewModels;

public class PlaylistCardViewModel : ViewModelBase
{
    private readonly Action<Playlist> _addToQueueAction;

    public Playlist Playlist { get; }
    public string Id => Playlist.Id;
    public string Name => Playlist.Name;
    public string? ThumbnailUrl => Playlist.ThumbnailUrl;
    public int TrackCount => Playlist.TrackCount;

    public bool IsLocal => Playlist.IsLocal;
    public bool IsSynced => Playlist.IsFromAccount;
    public bool IsReadOnly => !Playlist.IsEditable;

    // Можно ли удалить плейлист (нельзя удалить "Любимое")
    public bool CanDelete => Playlist.Id != "liked";

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    public string FormattedTrackCount => LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    private readonly Func<string, Task>? _onDelete;

    public PlaylistCardViewModel(
        Playlist playlist,
        Action<string> onOpen,
        Action<Playlist> addToQueueAction,
        Func<string, Task>? onDelete = null)
    {
        Playlist = playlist;
        _onDelete = onDelete;
        _addToQueueAction = addToQueueAction;

        OpenCommand = ReactiveCommand.Create(() => onOpen(playlist.Id));

        // Команда удаления доступна только если можно удалить
        var canDelete = this.WhenAnyValue(x => x.CanDelete);
        DeleteCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_onDelete != null)
            {
                await _onDelete(Playlist.Id);
            }
        }, canDelete);

        AddToQueueCommand = ReactiveCommand.Create(() =>
            _addToQueueAction(Playlist));

        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)));

        LocalizationService.Instance.LanguageChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }
}