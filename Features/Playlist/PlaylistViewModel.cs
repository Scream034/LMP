// Features/Playlist/PlaylistViewModel.cs
using Avalonia.Controls;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace LMP.Features.Playlist;

public sealed class PlaylistViewModel : ReorderableViewModel<TrackInfo, TrackItemViewModel>
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly IDialogService _dialog;
    private readonly TrackViewModelFactory _vmFactory;

    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentPlaylistId = "";

    private readonly IDisposable? _librarySubscription;
    private readonly IDisposable? _audioStateSub;
    private readonly IDisposable? _trackChangeSub;

    private DateTime _lastMoveTime = DateTime.MinValue;
    private const int MoveDebounceMs = 1000;

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

    [Reactive] public bool CanReorderItems { get; private set; }

    private GridLength _headerHeight;
    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            this.RaiseAndSetIfChanged(ref _headerHeight, value);
            if (value.IsAbsolute && value.Value > 50)
            {
                if (Math.Abs(LibService.Settings.PlaylistHeaderHeight - value.Value) > 1)
                {
                    LibService.UpdateSettings(s => s.PlaylistHeaderHeight = value.Value);
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
    public ReactiveCommand<(int oldIndex, int newIndex), Unit> MoveItemCommand { get; }

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
        _headerHeight = new GridLength(LibService.Settings.PlaylistHeaderHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = ReactiveCommand.CreateFromTask(PlayAllAsync, hasTracks);

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
            await LoadPlaylistAsync(_currentPlaylistId);
        });

        UnlinkFromCloudCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ConvertToLocalAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        });

        RefreshPlaylistCommand = ReactiveCommand.CreateFromTask(() => LoadPlaylistAsync(_currentPlaylistId));

        ShufflePlayCommand = ReactiveCommand.Create(ToggleShuffle, hasTracks);
        DownloadAllCommand = ReactiveCommand.Create(DownloadAll, hasTracks);
        MergePlaylistCommand = ReactiveCommand.CreateFromTask(MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit));

        AddToQueueCommand = ReactiveCommand.Create(() =>
        {
            _audio.EnqueueRange(GetLoadedItemsSnapshot());
        }, hasTracks);

        // Команда перемещения
        MoveItemCommand = ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(async tuple =>
        {
            if (!CanReorderItems) return;

            _lastMoveTime = DateTime.Now;

            // Вызываем метод базового класса
            await MoveItemAsync(tuple.oldIndex, tuple.newIndex);
        });

        // Обновление CanReorderItems
        this.WhenAnyValue(x => x.CanEdit, x => x.FilterQuery)
            .Subscribe(_ =>
            {
                CanReorderItems = CanEdit && CanReorder;
            })
            .DisposeWith(Disposables);

        // Подписка на изменения библиотеки
        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if ((DateTime.Now - _lastMoveTime).TotalMilliseconds < MoveDebounceMs)
                {
                    Log.Debug("[Playlist] Ignoring OnDataChanged (recent move)");
                    return;
                }

                if (!string.IsNullOrEmpty(_currentPlaylistId))
                {
                    await LoadPlaylistAsync(_currentPlaylistId);
                }
            });

        _audioStateSub = Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await CheckPlaybackStateAsync());

        _trackChangeSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await CheckPlaybackStateAsync());
    }

    #endregion

    #region Abstract Implementation

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override TrackItemViewModel CreateViewModel(TrackInfo item)
    {
        var vm = _vmFactory.GetOrCreate(item, PlayFromPlaylist);
        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async (t) =>
        {
            if (CanEdit)
                await _manager.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
        };

        vm.StartRadioAction = (t) => Log.Info($"Start radio requested for {t.Title}");

        return vm;
    }

    protected override bool MatchesFilter(TrackInfo item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        return item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Author.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<List<TrackInfo>> LoadItemsByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        return await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId,
            limit: ids.Count(),
            offset: LoadedCount,
            ct);
    }

    protected override async Task SaveMoveAsync(int fromMasterIndex, int toMasterIndex, CancellationToken ct)
    {
        Log.Info($"[Playlist] Saving move {fromMasterIndex}→{toMasterIndex}");
        await _manager.MovePlaylistTrackAsync(_currentPlaylistId, fromMasterIndex, toMasterIndex);
        Log.Info("[Playlist] Move saved");
    }

    #endregion

    #region Public Methods

    public async Task LoadPlaylistAsync(string playlistId)
    {
        _currentPlaylistId = playlistId;

        var playlist = await LibService.GetPlaylistAsync(playlistId);
        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsEditable;
        IsCloud = playlist.IsFromAccount;
        IsReadOnly = !playlist.IsEditable;

        var allIds = await LibService.GetPlaylistTrackIdsAsync(playlistId);
        TrackCount = allIds.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        TotalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId);
        FormatDuration();

        await InitializeAsync(allIds);
        await CheckPlaybackStateAsync();
    }

    #endregion

    #region Private Methods

    private async Task CheckPlaybackStateAsync()
    {
        if (_audio.CurrentTrack != null && _audio.IsPlaying)
        {
            IsPlayingThisPlaylist = await LibService.IsTrackInPlaylistAsync(
                _audio.CurrentTrack.Id, _currentPlaylistId);
        }
        else
        {
            IsPlayingThisPlaylist = false;
        }
    }

    private async Task PlayAllAsync()
    {
        if (TrackCount == 0) return;

        if (IsPlayingThisPlaylist)
        {
            _ = _audio.SetPlaybackStateAsync(false);
            return;
        }

        // Загружаем все треки для воспроизведения
        var allIds = GetAllIds();
        var allTracks = await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId, limit: allIds.Count, offset: 0);

        if (allTracks.Count == 0) return;

        _audio.ClearQueue();
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;
        _audio.EnqueueRange(allTracks);

        _ = Task.Run(async () => await _audio.PlayTrackAsync(allTracks[0]));
    }

    private async void ToggleShuffle()
    {
        if (TrackCount == 0) return;

        var allIds = GetAllIds();
        var allTracks = await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId, limit: allIds.Count, offset: 0);

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

    private async void DownloadAll()
    {
        IsDownloadingActive = true;

        var allIds = GetAllIds();
        var allTracks = await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId, limit: allIds.Count, offset: 0);

        foreach (var track in allTracks.Where(t => !t.IsDownloaded))
        {
            _downloads.StartDownload(track);
        }

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false);
    }

    private void FormatDuration()
    {
        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;
        int seconds = TotalDuration.Seconds;

        if (totalHours > 0)
            FormattedDuration = $"{totalHours}h {minutes}m";
        else if (minutes > 0)
            FormattedDuration = $"{minutes}m {seconds}s";
        else
            FormattedDuration = $"{seconds}s";
    }

    private async void PlayFromPlaylist(TrackInfo track)
    {
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;

        var allIds = GetAllIds();
        var allTracks = await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId, limit: allIds.Count, offset: 0);

        await _audio.StartQueueAsync(allTracks, track);
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    private async Task MergePlaylistAsync()
    {
        var otherPlaylists = (await LibService.GetAllPlaylistsAsync())
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
            if (await _manager.MergePlaylistsAsync(_currentPlaylistId, targetId))
                await _dialog.ShowInfoAsync(SL["Dialog_Success"], SL["Merge_Success_Msg"]);
            else
                await _dialog.ShowInfoAsync(SL["Dialog_Error"], SL["Merge_Error_Msg"]);
        }
    }

    #endregion

    #region Dispose

    public override void Dispose()
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        _librarySubscription?.Dispose();
        _audioStateSub?.Dispose();
        _trackChangeSub?.Dispose();

        base.Dispose();
    }

    #endregion
}