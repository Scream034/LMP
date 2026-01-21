using System.Reactive;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;

namespace MyLiteMusicPlayer.ViewModels;

/// <summary>
/// ViewModel для краткого отображения плейлиста в сетке библиотеки.
/// </summary>
public class PlaylistCardViewModel : ViewModelBase
{
    public Playlist Playlist { get; }
    public string Name => Playlist.Name;
    public string? ThumbnailUrl => Playlist.ThumbnailUrl;
    public int TrackCount => Playlist.TrackCount;

    // Используем новые хелперы
    public bool IsLocal => Playlist.IsLocal;
    public bool IsSynced => Playlist.IsFromAccount; // Иконка облака
    public bool IsReadOnly => !Playlist.IsEditable; // Иконка замка

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public string FormattedTrackCount => LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public PlaylistCardViewModel(Playlist playlist, Action<string> onOpen)
    {
        Playlist = playlist;
        OpenCommand = ReactiveCommand.Create(() => onOpen(playlist.Id));

        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)));

        LocalizationService.Instance.LanguageChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }
}