using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class PlaylistViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly IDialogService _dialog;

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public string FormattedDuration { get; set; } = "";

    [Reactive] public bool CanEdit { get; private set; }
    [Reactive] public bool IsCloud { get; private set; }
    [Reactive] public bool IsReadOnly { get; private set; }

    private string _currentPlaylistId = "";
    private IDisposable? _librarySubscription;

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DeletePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadToCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlinkFromCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MergePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPlaylistCommand { get; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public PlaylistViewModel(
      LibraryService library,
      AudioEngine audio,
      DownloadService downloads,
      MusicLibraryManager manager,
      IDialogService dialog)
    {
        _library = library;
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _dialog = dialog;

        // Подписываемся на обновление языка для перерисовки "X треков"
        LocalizationService.Instance.LanguageChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = ReactiveCommand.Create(PlayAll, hasTracks);

        DeletePlaylistCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var confirmTitle = L["Dialog_Confirm_Title"];
            var confirmMsg = string.Format(L["Playlist_DeleteConfirm"], PlaylistName);

            if (await _dialog.ConfirmAsync(confirmTitle, confirmMsg))
            {
                await _manager.DeletePlaylistAsync(_currentPlaylistId);
            }
        });

        UploadToCloudCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.UploadPlaylistToAccountAsync(_currentPlaylistId);
            LoadPlaylist(_currentPlaylistId);
        });

        UnlinkFromCloudCommand = ReactiveCommand.Create(() =>
        {
            _manager.ConvertToLocal(_currentPlaylistId);
            LoadPlaylist(_currentPlaylistId);
        });

        RefreshPlaylistCommand = ReactiveCommand.Create(() => LoadPlaylist(_currentPlaylistId));

        ShufflePlayCommand = ReactiveCommand.Create(PlayShuffle, hasTracks);
        DownloadAllCommand = ReactiveCommand.Create(DownloadAll, hasTracks);
        MergePlaylistCommand = ReactiveCommand.CreateFromTask(MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit));

        // Подписываемся на изменения в библиотеке
        _librarySubscription = Observable.FromEvent(
                h => _library.OnDataChanged += h,
                h => _library.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!string.IsNullOrEmpty(_currentPlaylistId))
                {
                    LoadPlaylist(_currentPlaylistId);
                }
            });
    }

    private void PlayAll()
    {
        if (AllItems.Count == 0) return;
        _audio.ClearQueue();
        _audio.ShuffleEnabled = false;
        _audio.EnqueueRange(AllItems);
        _ = _audio.PlayTrackAsync(AllItems[0]);
    }

    private void PlayShuffle()
    {
        if (AllItems.Count == 0) return;
        _audio.ClearQueue();
        _audio.ShuffleEnabled = true;
        _audio.EnqueueRange(AllItems);
        _ = _audio.PlayTrackAsync(AllItems[0]);
    }

    private void DownloadAll()
    {
        foreach (var track in AllItems.Where(t => !t.IsDownloaded))
        {
            _downloads.StartDownload(track);
        }
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        return new TrackItemViewModel(track, _audio, _library, _downloads, _manager, PlayFromPlaylist);
    }

    public async void LoadPlaylist(string playlistId)
    {
        _currentPlaylistId = playlistId;
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsEditable;
        IsCloud = playlist.IsFromAccount;
        IsReadOnly = !playlist.IsEditable;

        var tracks = _library.GetPlaylistTracks(playlistId);
        TrackCount = tracks.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        CalculateTotalDuration(tracks);

        await InitializeItemsAsync(tracks, canFetchMore: false);
    }

    private void CalculateTotalDuration(List<TrackInfo> tracks)
    {
        var totalSec = tracks.Sum(t => t.Duration.TotalSeconds);
        TotalDuration = TimeSpan.FromSeconds(totalSec);

        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;

        if (totalHours > 0)
            FormattedDuration = $"{totalHours}h {minutes}m";
        else
            FormattedDuration = $"{minutes}m {TotalDuration.Seconds}s";
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
        var otherPlaylists = _library.GetAllPlaylists()
            .Where(p => p.Id != _currentPlaylistId && p.IsLocal)
            .ToList();

        if (otherPlaylists.Count == 0)
        {
            await _dialog.ShowInfoAsync(
                L["Dialog_Merge_NoTarget_Title"],
                L["Dialog_Merge_NoTarget_Msg"]);
            return;
        }

        var targetId = otherPlaylists.First().Id;
        if (!string.IsNullOrEmpty(targetId))
        {
            if (_library.MergePlaylists(_currentPlaylistId, targetId))
                await _dialog.ShowInfoAsync(L["Dialog_Success"], L["Merge_Success_Msg"]);
            else
                await _dialog.ShowInfoAsync(L["Dialog_Error"], L["Merge_Error_Msg"]);
        }
    }

    public void Dispose()
    {
        _librarySubscription?.Dispose();
        CancelLoading();
    }
}