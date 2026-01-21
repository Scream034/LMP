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
    private readonly IDialogService _dialog;

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public bool CanEdit { get; private set; }

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MergePlaylistCommand { get; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    private string _playlistId = ""; // Храним ID текущего плейлиста

    public PlaylistViewModel(
        LibraryService library,
        AudioEngine audio,
        DownloadService downloads,
        IDialogService dialog)
    {
        _library = library;
        _audio = audio;
        _downloads = downloads;
        _dialog = dialog;

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

        MergePlaylistCommand = ReactiveCommand.CreateFromTask(MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit));

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

    private async Task MergePlaylistAsync()
    {
        var otherPlaylists = _library.GetAllPlaylists().Where(p => p.Id != _playlistId && p.IsLocal).ToList();
        if (otherPlaylists.Count == 0)
        {
            await _dialog.ShowInfoAsync("Некуда объединять", "Нет других локальных плейлистов для объединения.");
            return;
        }

        // Здесь должен быть вызов диалога для выбора целевого плейлиста
        // var targetId = await _dialog.ShowSelectTargetPlaylistDialog(otherPlaylists);

        // Для примера, объединим с первым попавшимся
        var targetId = otherPlaylists.First().Id;

        if (!string.IsNullOrEmpty(targetId))
        {
            if (_library.MergePlaylists(_playlistId, targetId))
            {
                await _dialog.ShowInfoAsync("Успешно", "Плейлист был успешно объединен.");
            }
            else
            {
                await _dialog.ShowInfoAsync("Ошибка", "Не удалось объединить плейлисты.");
            }
        }
    }
}