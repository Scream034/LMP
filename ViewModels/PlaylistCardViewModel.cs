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
    public bool IsLocal => Playlist.IsLocal;
    public bool IsLiked => Playlist.Id == "liked";

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    // Локализованная строка количества треков
    public string FormattedTrackCount => LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public PlaylistCardViewModel(Playlist playlist, Action<string> onOpen)
    {
        Playlist = playlist;

        // Команда открытия вызывает экшн навигации, переданный из родителя
        OpenCommand = ReactiveCommand.Create(() => onOpen(playlist.Id));

        // Обновляем текст при изменении количества треков или смене языка
        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)));

        LocalizationService.Instance.LanguageChanged += (s, e) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }
}