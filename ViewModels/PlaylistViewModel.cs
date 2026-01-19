using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;

namespace MyLiteMusicPlayer.ViewModels;

public class PlaylistViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>
{
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public bool CanEdit { get; private set; }

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public PlaylistViewModel(
        LibraryService library,
        AudioEngine audio,
        DownloadService downloads)
    {
        _library = library;
        _audio = audio;
        _downloads = downloads;

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = ReactiveCommand.Create(() =>
        {
            if (AllItems.Count == 0) return;
            _audio.ClearQueue();
            _audio.ShuffleEnabled = false;
            _audio.EnqueueRange(AllItems);
            _ = _audio.PlayTrackAsync(AllItems[0]);
        }, hasTracks);

        ShufflePlayCommand = ReactiveCommand.Create(() =>
        {
            if (AllItems.Count == 0) return;
            _audio.ClearQueue();
            _audio.ShuffleEnabled = true;
            _audio.EnqueueRange(AllItems);
            _ = _audio.PlayTrackAsync(AllItems[0]);
        }, hasTracks);

        DownloadAllCommand = ReactiveCommand.Create(() =>
        {
            foreach (var track in AllItems.Where(t => !t.IsDownloaded))
            {
                _downloads.StartDownload(track);
            }
        }, hasTracks);

        // Обновление локализации
        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)));

        LocalizationService.Instance.LanguageChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        return new TrackItemViewModel(track, _audio, _library, _downloads, PlayFromPlaylist);
    }

    public async void LoadPlaylist(string playlistId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsLocal && playlist.Id != "liked";

        var tracks = _library.GetPlaylistTracks(playlistId);
        TrackCount = tracks.Count;
        TotalDuration = TimeSpan.FromSeconds(tracks.Sum(t => t.Duration.TotalSeconds));

        await InitializeItemsAsync(tracks);
    }

    private void PlayFromPlaylist(TrackInfo track)
    {
        _audio.ClearQueue();
        _ = _audio.PlayTrackAsync(track);

        bool found = false;
        foreach (var item in AllItems)
        {
            if (found) _audio.Enqueue(item);
            if (item.Id == track.Id) found = true;
        }

        _library.AddToRecentlyPlayed(track);
    }
}