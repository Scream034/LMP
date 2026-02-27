using Avalonia.Controls;
using Avalonia.Media;
using LMP.Core.Audio;
using LMP.Core.Helpers;
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
    private readonly DominantColorService _dominantColor;

    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentPlaylistId = "";

    private readonly IDisposable? _librarySubscription;
    private readonly IDisposable? _audioStateSub;
    private readonly IDisposable? _trackChangeSub;

    private DateTime _lastMoveTime = DateTime.MinValue;
    private const int MoveDebounceMs = 1000;

    // Кэш всех треков для избежания повторных загрузок
    private List<TrackInfo>? _allTracksCache;
    private bool _allTracksCacheValid;

    // LIFECYCLE: Флаг для пропуска UI-обновлений когда окно свёрнуто
    private volatile bool _isSuspended;

    #endregion

    #region Properties

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public string FormattedDuration { get; set; } = "";
    [Reactive] public IBrush? HeaderBackground { get; private set; }
    [Reactive] public bool IsLikedPlaylist { get; private set; }

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
        TrackViewModelFactory vmFactory,
        DominantColorService dominantColor)
    {
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _dialog = dialog;
        _vmFactory = vmFactory;
        _dominantColor = dominantColor;

        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        IsShuffleActive = _audio.ShuffleEnabled;
        _headerHeight = new GridLength(LibService.Settings.PlaylistHeaderHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = CreateCommand(ReactiveCommand.CreateFromTask(PlayAllAsync, hasTracks));

        DeletePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            var confirmTitle = SL["Dialog_Confirm_Title"];
            var confirmMsg = string.Format(SL["Playlist_DeleteConfirm"], PlaylistName);

            if (await _dialog.ConfirmAsync(confirmTitle, confirmMsg))
            {
                await _manager.DeletePlaylistAsync(_currentPlaylistId);
            }
        }));

        UploadToCloudCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.UploadPlaylistToAccountAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        }));

        UnlinkFromCloudCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ConvertToLocalAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        }));

        RefreshPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(() => LoadPlaylistAsync(_currentPlaylistId)));

        ShufflePlayCommand = CreateCommand(ReactiveCommand.CreateFromTask(ShufflePlayAsync, hasTracks));
        DownloadAllCommand = CreateCommand(ReactiveCommand.CreateFromTask(DownloadAllAsync, hasTracks));
        MergePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit)));

        AddToQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _audio.EnqueueRange(GetLoadedItemsSnapshot());
        }, hasTracks));

        MoveItemCommand = CreateCommand(ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(async tuple =>
        {
            if (!CanReorderItems) return;

            _lastMoveTime = DateTime.Now;
            InvalidateAllTracksCache();

            await MoveItemAsync(tuple.oldIndex, tuple.newIndex);
        }));

        this.WhenAnyValue(x => x.CanEdit, x => x.FilterQuery)
            .Subscribe(_ =>
            {
                CanReorderItems = CanEdit && CanReorder;
            })
            .DisposeWith(Disposables);

        // ИЗМЕНЕНО: Добавлена проверка _isSuspended для пропуска UI-обновлений
        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                // LIFECYCLE: Пропускаем обновления когда окно свёрнуто
                if (_isSuspended) return;

                if ((DateTime.Now - _lastMoveTime).TotalMilliseconds < MoveDebounceMs)
                {
                    Log.Debug("[Playlist] Ignoring OnDataChanged (recent move)");
                    return;
                }

                InvalidateAllTracksCache();

                if (!string.IsNullOrEmpty(_currentPlaylistId))
                {
                    await LoadPlaylistAsync(_currentPlaylistId);
                }
            });

        // ИЗМЕНЕНО: Добавлена проверка _isSuspended
        _audioStateSub = Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                // LIFECYCLE: Пропускаем обновления когда окно свёрнуто
                if (_isSuspended) return;

                await CheckPlaybackStateAsync();
            });

        // ИЗМЕНЕНО: Добавлена проверка _isSuspended
        _trackChangeSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                // LIFECYCLE: Пропускаем обновления когда окно свёрнуто
                if (_isSuspended) return;

                await CheckPlaybackStateAsync();
            });
    }

    #endregion

    // LIFECYCLE IMPLEMENTATION

    /// <summary>
    /// Окно свёрнуто — пропускаем UI-обновления от событий.
    /// Музыка продолжает играть, но UI не обновляется.
    /// </summary>
    protected override void OnSuspend()
    {
        _isSuspended = true;
    }

    /// <summary>
    /// Окно развёрнуто — синхронизируем UI с актуальным состоянием.
    /// </summary>
    protected override void OnResume()
    {
        _isSuspended = false;

        // Синхронизируем состояние воспроизведения (трек мог смениться)
        _ = CheckPlaybackStateAsync();

        // Инвалидируем кэш — данные могли измениться
        InvalidateAllTracksCache();

        // Обновляем UI-свойства
        this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }


    #region Abstract Implementation

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override TrackItemViewModel CreateViewModel(TrackInfo item)
    {
        var vm = _vmFactory.GetOrCreate(item, PlayFromPlaylistAsync);
        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async (t) =>
        {
            if (CanEdit)
            {
                InvalidateAllTracksCache();
                await _manager.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
            }
        };

        vm.StartRadioAction = (t) => Log.Info($"Start radio requested for {t.Title}");

        return vm;
    }

    protected override bool MatchesFilter(TrackInfo item, string query)
        => TrackFilters.MatchesTitleOrAuthor(item, query);

    protected override async Task<List<TrackInfo>> LoadItemsByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        var idsList = ids.ToList();
        return await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId,
            limit: idsList.Count,
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

    #region All Tracks Helper (DRY)

    private async Task<List<TrackInfo>> GetAllTracksAsync()
    {
        if (_allTracksCacheValid && _allTracksCache != null)
        {
            return _allTracksCache;
        }

        var allIds = GetAllIds();
        _allTracksCache = await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId, limit: allIds.Count, offset: 0);
        _allTracksCacheValid = true;

        return _allTracksCache;
    }

    private void InvalidateAllTracksCache()
    {
        _allTracksCacheValid = false;
        _allTracksCache = null;
    }

    #endregion

    #region Public Methods

    public async Task LoadPlaylistAsync(string playlistId)
    {
        _currentPlaylistId = playlistId;
        InvalidateAllTracksCache();

        var playlist = await LibService.GetPlaylistAsync(playlistId);
        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsEditable;
        IsCloud = playlist.IsFromAccount;
        IsReadOnly = !playlist.IsEditable;
        IsLikedPlaylist = playlistId == LibraryService.LikedPlaylistId;

        var allIds = await LibService.GetPlaylistTrackIdsAsync(playlistId);
        TrackCount = allIds.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        TotalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId);
        FormatDuration();

        await InitializeAsync(allIds);

        // Загружаем градиент в фоне
        _ = LoadHeaderGradientAsync();

        _ = HydrateCacheStatusInBackgroundAsync();
        await CheckPlaybackStateAsync();
    }

    #endregion

    #region Private Methods

    private async Task LoadHeaderGradientAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(ThumbnailUrl))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => HeaderBackground = null);
                return;
            }

            // Тяжёлая работа по извлечению цвета остаётся в фоне
            var dominantColor = await _dominantColor.GetDominantColorAsync(ThumbnailUrl);

            // Создание UI-объектов (Brush, Stops) и присвоение свойства СТРОГО на UI-потоке
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (dominantColor == null)
                {
                    HeaderBackground = null;
                }
                else
                {
                    HeaderBackground = DominantColorService.CreateHeaderGradient(dominantColor.Value);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Gradient failed: {ex.Message}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => HeaderBackground = null);
        }
    }

    private async Task HydrateCacheStatusInBackgroundAsync()
    {
        try
        {
            var tracks = GetLoadedItemsSnapshot();
            var audioCache = AudioSourceFactory.GlobalCache;
            if (audioCache == null) return;

            await Task.Run(() => audioCache.HydrateCacheStatus(tracks));
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Cache hydration error: {ex.Message}");
        }
    }

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
            await _audio.SetPlaybackStateAsync(false);
            return;
        }

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        _audio.ClearQueue();
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;
        _audio.EnqueueRange(allTracks);

        await _audio.PlayTrackAsync(allTracks[0]);
    }

    private async Task ShufflePlayAsync()
    {
        if (TrackCount == 0) return;

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        _audio.ClearQueue();
        _audio.EnqueueRange(allTracks);
        _audio.ShuffleQueue();

        var queue = _audio.Queue;
        if (queue.Count > 0)
        {
            await _audio.PlayTrackAsync(queue[0]);
        }

        IsShuffleActive = true;

        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false)
            .DisposeWith(Disposables);
    }

    private async Task DownloadAllAsync()
    {
        IsDownloadingActive = true;

        var allTracks = await GetAllTracksAsync();

        foreach (var track in allTracks.Where(t => !t.IsDownloaded))
        {
            _downloads.StartDownload(track);
        }

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false)
            .DisposeWith(Disposables);
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

    private async void PlayFromPlaylistAsync(TrackInfo track)
    {
        try
        {
            _audio.ShuffleEnabled = false;
            IsShuffleActive = false;

            var allTracks = await GetAllTracksAsync();
            await _audio.StartQueueAsync(allTracks, track);
            _ = LibService.AddToRecentlyPlayedAsync(track);
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] PlayFromPlaylist error: {ex.Message}");
        }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log.Debug($"[PlaylistVM] Disposing playlist {_currentPlaylistId}");

            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _librarySubscription?.Dispose();
            _audioStateSub?.Dispose();
            _trackChangeSub?.Dispose();

            InvalidateAllTracksCache();
        }

        base.Dispose(disposing);
    }

    #endregion
}