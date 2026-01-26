using Avalonia.Controls;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;

namespace LMP.Features.Playlist;

/// <summary>
/// ViewModel для отображения содержимого плейлиста и управления им.
/// </summary>
public sealed class PlaylistViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly IDialogService _dialog;
    private readonly TrackViewModelFactory _vmFactory;

    private readonly EventHandler<string> _languageChangedHandler;
    
    private string _currentPlaylistId = "";
    private IDisposable? _librarySubscription;
    private IDisposable? _audioStateSub;
    private IDisposable? _trackChangeSub;
    
    private bool _isDisposed;

    #endregion

    #region Properties

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public string FormattedDuration { get; set; } = "";

    [Reactive] public bool CanEdit { get; private set; }
    [Reactive] public bool IsCloud { get; private set; }
    [Reactive] public bool IsReadOnly { get; private set; }

    [Reactive] public bool IsPlayingThisPlaylist { get; private set; }
    [Reactive] public bool IsShuffleActive { get; private set; }
    [Reactive] public bool IsDownloadingActive { get; private set; }

    private GridLength _headerHeight;
    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            this.RaiseAndSetIfChanged(ref _headerHeight, value);
            if (value.IsAbsolute && value.Value > 50)
            {
                if (Math.Abs(LibService.Data.PlaylistHeaderHeight - value.Value) > 1)
                {
                    LibService.Data.PlaylistHeaderHeight = value.Value;
                    LibService.Save();
                }
            }
        }
    }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DeletePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadToCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlinkFromCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MergePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    #endregion

    #region Constructor

    public PlaylistViewModel(
      AudioEngine audio,
      DownloadService downloads,
      MusicLibraryManager manager,
      IDialogService dialog,
      TrackViewModelFactory vmFactory)
    {
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _dialog = dialog;
        _vmFactory = vmFactory;

        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        IsShuffleActive = _audio.ShuffleEnabled;
        _headerHeight = new GridLength(LibService.Data.PlaylistHeaderHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = ReactiveCommand.Create(PlayAll, hasTracks);

        DeletePlaylistCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var confirmTitle = SL["Dialog_Confirm_Title"];
            var confirmMsg = string.Format(SL["Playlist_DeleteConfirm"], PlaylistName);

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

        ShufflePlayCommand = ReactiveCommand.Create(ToggleShuffle, hasTracks);
        DownloadAllCommand = ReactiveCommand.Create(DownloadAll, hasTracks);
        MergePlaylistCommand = ReactiveCommand.CreateFromTask(MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit));

        AddToQueueCommand = ReactiveCommand.Create(() =>
        {
            // Заменено небезопасное `AllItems` на `GetItemsSnapshot()`
            _audio.EnqueueRange(GetItemsSnapshot());
        }, hasTracks);

        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!string.IsNullOrEmpty(_currentPlaylistId))
                {
                    LoadPlaylist(_currentPlaylistId);
                }
            });

        _audioStateSub = Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => CheckPlaybackState());

        _trackChangeSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => CheckPlaybackState());
    }

    #endregion

    #region Methods

    private void CheckPlaybackState()
    {
        if (_audio.CurrentTrack != null && _audio.IsPlaying)
        {
            IsPlayingThisPlaylist = LibService.IsTrackInPlaylist(_audio.CurrentTrack.Id, _currentPlaylistId);
        }
        else
        {
            IsPlayingThisPlaylist = false;
        }
    }

    private void PlayAll()
    {
        // Заменено небезопасное `AllItems` на `GetItemsSnapshot()`
        var allTracks = GetItemsSnapshot();
        if (allTracks.Count == 0) return;

        if (IsPlayingThisPlaylist)
        {
            _ = _audio.SetPlaybackStateAsync(false);
            return;
        }

        if (_audio.CurrentTrack != null &&
            _audio.IsPaused &&
            LibService.IsTrackInPlaylist(_audio.CurrentTrack.Id, _currentPlaylistId))
        {
            _ = _audio.SetPlaybackStateAsync(true);
            return;
        }

        _audio.ClearQueue();
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;

        _audio.EnqueueRange(allTracks);
        _ = _audio.PlayTrackAsync(allTracks[0]);
    }

    private void ToggleShuffle()
    {
        // Заменено небезопасное `AllItems` на `GetItemsSnapshot()`
        var allTracks = GetItemsSnapshot();
        if (allTracks.Count == 0) return;

        _audio.ClearQueue();
        _audio.EnqueueRange(allTracks);
        _audio.ShuffleQueue();

        var queue = _audio.Queue;
        if (queue.Count > 0)
        {
            _ = _audio.PlayTrackAsync(queue[0]);
        }

        IsShuffleActive = true;
        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false);
    }

    private void DownloadAll()
    {
        IsDownloadingActive = true;
        // Заменено небезопасное `AllItems` на `GetItemsSnapshot()`
        foreach (var track in GetItemsSnapshot().Where(t => !t.IsDownloaded))
        {
            _downloads.StartDownload(track);
        }

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false);
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        return _vmFactory.GetOrCreate(track, PlayFromPlaylist);
    }

    public async void LoadPlaylist(string playlistId)
    {
        _currentPlaylistId = playlistId;
        var playlist = LibService.GetPlaylist(playlistId);
        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsEditable;
        IsCloud = playlist.IsFromAccount;
        IsReadOnly = !playlist.IsEditable;

        var tracks = LibService.GetPlaylistTracks(playlistId);
        TrackCount = tracks.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        CalculateTotalDuration(tracks);

        await InitializeItemsAsync(tracks, canFetchMore: false);
        CheckPlaybackState();
    }

    private void CalculateTotalDuration(List<TrackInfo> tracks)
    {
        var totalSec = tracks.Sum(static t => t.Duration.TotalSeconds);
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
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;
        var tracks = Items.Select(static vm => vm.Track).ToList();
        _ = _audio.StartQueueAsync(tracks, track);
        LibService.AddToRecentlyPlayed(track);
    }

    private async Task MergePlaylistAsync()
    {
        var otherPlaylists = LibService.GetAllPlaylists()
            .Where(p => p.Id != _currentPlaylistId && p.IsLocal)
            .ToList();

        if (otherPlaylists.Count == 0)
        {
            await _dialog.ShowInfoAsync(
                SL["Dialog_Merge_NoTarget_Title"],
                SL["Dialog_Merge_NoTarget_Msg"]);
            return;
        }

        var targetId = otherPlaylists.First().Id;
        if (!string.IsNullOrEmpty(targetId))
        {
            if (LibService.MergePlaylists(_currentPlaylistId, targetId))
                await _dialog.ShowInfoAsync(SL["Dialog_Success"], SL["Merge_Success_Msg"]);
            else
                await _dialog.ShowInfoAsync(SL["Dialog_Error"], SL["Merge_Error_Msg"]);
        }
    }

    #endregion

    #region Filter Implementation

    protected override bool FilterItem(TrackInfo item)
    {
        // 1. Фильтр по типу (Music / Video)
        if (FilterType == ContentFilterType.Music && !item.IsMusic) return false;
        if (FilterType == ContentFilterType.Video && item.IsMusic) return false;

        // 2. Текстовый поиск
        if (string.IsNullOrWhiteSpace(FilterQuery)) return true;

        return item.Title.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
               item.Author.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region IDisposable Implementation

    public new void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;

        _librarySubscription?.Dispose();
        _audioStateSub?.Dispose();
        _trackChangeSub?.Dispose();
        CancelLoading();

        base.Dispose();
    }

    #endregion
}