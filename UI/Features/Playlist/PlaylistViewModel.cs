using Avalonia.Controls;
using Avalonia.Media;
using LMP.UI.Features.Shell;
using LMP.UI.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;
using LMP.UI.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;

namespace LMP.UI.Features.Playlist;

/// <summary>
/// ViewModel экрана плейлиста.
/// </summary>
public sealed class PlaylistViewModel : TrackListReorderableViewModel, ISmoothTransitionViewModel
{
    #region Fields

    private readonly MusicLibraryManager _manager;
    private readonly DialogService _dialog;
    private readonly DominantColorService _dominantColor;
    private readonly MainWindowViewModel _mainWindow;
    private readonly PlaylistEditService _editService;
    private readonly PlaylistSyncService _syncService;
    private readonly PlayerControlService _playerControl;
    private readonly CookieAuthService _auth;

    private readonly EventHandler<string> _languageChangedHandler;
    private readonly IDisposable? _librarySubscription;

    private string _currentPlaylistId = "";
    private Core.Models.Playlist? _currentPlaylist;

    private DateTime _lastLocalMutationTime = DateTime.MinValue;
    private const int LocalMutationDebounceMs = 1500;

    private List<TrackInfo>? _allTracksCache;
    private bool _allTracksCacheValid;

    private volatile bool _isSuspended;
    private int _syncInProgressGate;

    private HashSet<string>? _playlistTrackIds;

    #endregion

    #region Properties

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }

    [Reactive] public string? Description { get; private set; }

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
    [Reactive] public bool IsSyncing { get; private set; }
    [Reactive] public bool CanReorderItems { get; private set; }

    public string? PlaylistYoutubeUrl =>
        IsLikedPlaylist && _auth.IsAuthenticated
            ? "https://www.youtube.com/playlist?list=LL"
            : _currentPlaylist?.YoutubeId is { Length: > 0 } id
                ? $"https://www.youtube.com/playlist?list={id}"
                : null;

    [Reactive] public bool HasYoutubeLink { get; private set; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            if (!value.IsAbsolute) return;
            var clamped = new GridLength(Math.Clamp(value.Value, HeaderHeightMin, HeaderHeightMax));
            this.RaiseAndSetIfChanged(ref _headerHeight, clamped);
            if (Math.Abs(LibService.Settings.PlaylistHeaderHeight - clamped.Value) > 1)
                LibService.UpdateSettings(s => s.PlaylistHeaderHeight = clamped.Value);
        }
    }

    private GridLength _headerHeight;
    private const double HeaderHeightMin = 280;
    private const double HeaderHeightMax = 400;

    /// <summary>Очередь "чистая" — запущена из этого плейлиста и не изменялась сторонними треками.</summary>
    [Reactive] public bool IsQueuePure { get; private set; }

    /// <summary>Очередь "чистая" и прямо сейчас активно проигрывается (для анимации эквалайзера).</summary>
    [Reactive] public bool IsPlayingPure { get; private set; }

    /// <summary>Динамическая подсказка для кнопки-трансформера.</summary>
    public string PlayButtonTooltip
    {
        get
        {
            if (IsQueuePure)
            {
                return IsPlayingPure ? (SL["Player_Pause"] ?? "Pause") : (SL["Player_Play"] ?? "Play");
            }
            return SL["Playlist_PlayAll"] ?? "Play (replace queue)";
        }
    }

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
    public ReactiveCommand<Unit, Unit> EditPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPlaylistLinkCommand { get; }

    #endregion

    #region Constructor

    public PlaylistViewModel(
      AudioEngine audio,
      DownloadService downloads,
      MusicLibraryManager manager,
      DialogService dialog,
      TrackViewModelFactory vmFactory,
      DominantColorService dominantColor,
      MainWindowViewModel mainWindow,
      PlaylistSyncService syncService,
      PlaylistEditService editService,
      PlayerControlService playerControl,
      CookieAuthService auth)
      : base(audio, downloads, vmFactory)
    {
        _manager = manager;
        _dialog = dialog;
        _dominantColor = dominantColor;
        _mainWindow = mainWindow;
        _syncService = syncService;
        _editService = editService;
        _playerControl = playerControl;
        _auth = auth;

        _languageChangedHandler = (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        var savedHeight = Math.Clamp(
            LibService.Settings.PlaylistHeaderHeight,
            HeaderHeightMin,
            HeaderHeightMax);
        _headerHeight = new GridLength(savedHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, static c => c > 0)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var canEdit = this.WhenAnyValue(x => x.CanEdit)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var canRefresh = this.WhenAnyValue(
                x => x.IsCloud, x => x.IsSyncing,
                static (isCloud, isSyncing) => isCloud && !isSyncing)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        PlayAllCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(PlayAllAsync, hasTracks));

        DeletePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (await _dialog.ConfirmAsync(
                SL["Dialog_Confirm_Title"],
                string.Format(SL["Playlist_DeleteConfirm"], PlaylistName)))
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

        RefreshPlaylistCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(RefreshPlaylistAsync, canRefresh));

        ShufflePlayCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(ShufflePlayAsync, hasTracks));

        DownloadAllCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(DownloadAllAsync, hasTracks));

        MergePlaylistCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(MergePlaylistAsync, canEdit));

        AddToQueueCommand = CreateCommand(
            ReactiveCommand.Create(EnqueueUniquePlaylistTracks, hasTracks));

        MoveItemCommand = CreateCommand(
            ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(async tuple =>
            {
                if (!CanReorderItems) return;
                _lastLocalMutationTime = DateTime.Now;
                InvalidateAllTracksCache();
                await MoveItemAsync(tuple.oldIndex, tuple.newIndex);
            }));

        EditPlaylistCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(EditPlaylistAsync, canEdit));

        CopyPlaylistLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            CopyPlaylistLinkAsync,
            this.WhenAnyValue(x => x.HasYoutubeLink)
                .ObserveOn(RxSchedulers.MainThreadScheduler)));

        this.WhenAnyValue(x => x.CanEdit, x => x.FilterQuery)
            .Subscribe(_ => CanReorderItems = CanEdit && CanReorder)
            .DisposeWith(Disposables);

        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (_isSuspended) return;

                if ((DateTime.Now - _lastLocalMutationTime).TotalMilliseconds < LocalMutationDebounceMs)
                {
                    Log.Debug("[Playlist] Ignoring OnDataChanged (recent local mutation)");
                    return;
                }

                InvalidateAllTracksCache();

                if (string.IsNullOrEmpty(_currentPlaylistId)) return;

                Dispatcher.UIThread.InvokeAsync(
                    () => LoadPlaylistAsync(_currentPlaylistId),
                    DispatcherPriority.Background);
            });

        SubscribeToPlaybackSource();
    }

    #endregion

    #region ISmoothTransitionViewModel

    public override void PrepareForTransition()
    {
        base.PrepareForTransition();
        IsLoading = true;
    }

    #endregion

    #region Lifecycle

    protected override void OnSuspend()
    {
        _isSuspended = true;
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        InvalidateAllTracksCache();
        UpdateIsPlayingThisPlaylist();
        this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }

    #endregion

    #region TrackListReorderableViewModel Implementation

    protected override TrackItemViewModel CreateViewModel(TrackInfo item)
    {
        var vm = base.CreateViewModel(item);

        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async t =>
        {
            if (!CanEdit) return;

            _lastLocalMutationTime = DateTime.Now;
            InvalidateAllTracksCache();
            _playlistTrackIds?.Remove(t.Id); // Синхронно поддерживаем чистоту кэша

            RemoveItemLocally(t.Id);

            TrackCount = Math.Max(0, TrackCount - 1);
            this.RaisePropertyChanged(nameof(FormattedTrackCount));

            if (t.Duration > TimeSpan.Zero)
            {
                TotalDuration = TotalDuration > t.Duration
                    ? TotalDuration - t.Duration
                    : TimeSpan.Zero;
                FormatDuration();
            }

            await _manager.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
        };

        vm.StartRadioAction = t => Log.Info($"[Playlist] Start radio requested for {t.Title}");

        return vm;
    }

    protected override async Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        return await LibService.GetPlaylistTracksAsync(_currentPlaylistId, ct);
    }

    protected override async Task SaveMoveAsync(
        int fromMasterIndex, int toMasterIndex, CancellationToken ct)
    {
        Log.Info($"[Playlist] Saving move {fromMasterIndex}→{toMasterIndex}");
        await _manager.MovePlaylistTrackAsync(_currentPlaylistId, fromMasterIndex, toMasterIndex, ct);
        Log.Info("[Playlist] Move saved");
    }

    protected override void OnPlay(TrackInfo track)
    {
        _ = PlayFromPlaylistAsync(track);
    }

    #endregion

    #region Public API

    public async Task LoadPlaylistAsync(string playlistId)
    {
        _currentPlaylistId = playlistId;
        InvalidateAllTracksCache();

        IsLoading = true;

        try
        {
            var playlist = await LibService.GetPlaylistAsync(playlistId);
            if (playlist is null)
            {
                return;
            }

            _currentPlaylist = playlist;

            PlaylistName = playlist.Name;
            ThumbnailUrl = playlist.ThumbnailUrl;
            Description = playlist.Description;
            CanEdit = playlist.IsEditable;
            IsLikedPlaylist = playlistId == LibraryService.LikedPlaylistId;
            IsCloud = playlist.IsFromAccount || (IsLikedPlaylist && _auth.IsAuthenticated);
            this.RaisePropertyChanged(nameof(PlaylistYoutubeUrl));
            HasYoutubeLink = PlaylistYoutubeUrl is not null;
            IsReadOnly = !playlist.IsEditable;

            var allIds = await LibService.GetPlaylistTrackIdsAsync(playlistId);
            _playlistTrackIds = new HashSet<string>(allIds, StringComparer.Ordinal);
            TrackCount = allIds.Count;
            this.RaisePropertyChanged(nameof(FormattedTrackCount));

            TotalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId);
            FormatDuration();
            this.RaisePropertyChanged(nameof(PlaylistYoutubeUrl));

            await InitializeAsync(allIds);

            _ = LoadHeaderGradientAsync();
            _ = HydrateCacheStatusInBackgroundAsync();

            UpdateIsPlayingThisPlaylist();
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Error loading playlist '{playlistId}': {ex.Message}");
        }
        finally
        {
            // Блок гарантирует сокрытие скелетона и появление разметки даже при сбое
            IsLoading = false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Настраивает реактивную подписку на состояние аудио-движка для умного морфинга кнопки.
    /// </summary>
    private void SubscribeToPlaybackSource()
    {
        Observable.CombineLatest(
            _playerControl.ActivePlaylistIdObservable,
            _playerControl.PlaybackStateObservable,
            _playerControl.QueueCountObservable,
            (activeId, state, qCount) => new { activeId, state.IsPlaying, qCount })
        .ObserveOn(RxSchedulers.MainThreadScheduler)
        .Subscribe(data =>
        {
            bool isActivePlaylist = data.activeId == _currentPlaylistId;
            IsPlayingThisPlaylist = isActivePlaylist;

            if (isActivePlaylist)
            {
                IsQueuePure = CheckQueuePurity();
            }
            else
            {
                IsQueuePure = false;
            }

            IsPlayingPure = IsQueuePure && data.IsPlaying;
            this.RaisePropertyChanged(nameof(PlayButtonTooltip));
        })
        .DisposeWith(Disposables);
    }

    /// <summary>
    /// Сверяет текущую очередь плеера с эталоном плейлиста за O(N).
    /// </summary>
    private bool CheckQueuePurity()
    {
        if (_playlistTrackIds == null) return false;

        var queue = Audio.Queue;

        // Быстрый отсев: если кто-то удалил/добавил трек, количество не совпадет
        if (queue.Count != TrackCount) return false;

        // Если количества совпадают, проверяем, нет ли "чужих" треков
        for (int i = 0; i < queue.Count; i++)
        {
            if (!_playlistTrackIds.Contains(queue[i].Id))
                return false;
        }

        return true;
    }

    private void UpdateIsPlayingThisPlaylist()
    {
        IsPlayingThisPlaylist = _playerControl.ActivePlaylistId == _currentPlaylistId;

        if (IsPlayingThisPlaylist)
        {
            IsQueuePure = CheckQueuePurity();
            IsPlayingPure = IsQueuePure && _playerControl.IsPlaying;
        }
        else
        {
            IsQueuePure = false;
            IsPlayingPure = false;
        }
        this.RaisePropertyChanged(nameof(PlayButtonTooltip));
    }

    private async Task<List<TrackInfo>> GetAllTracksAsync()
    {
        if (_allTracksCacheValid && _allTracksCache is not null)
            return _allTracksCache;

        _allTracksCache = await LibService.GetPlaylistTracksAsync(_currentPlaylistId);
        _allTracksCacheValid = true;
        return _allTracksCache;
    }

    private void InvalidateAllTracksCache()
    {
        _allTracksCacheValid = false;
        _allTracksCache = null;
    }

    private void FormatDuration()
    {
        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;
        int seconds = TotalDuration.Seconds;

        FormattedDuration = totalHours > 0
            ? $"{totalHours}{SL["Duration_Hours"]} {minutes}{SL["Duration_Minutes"]}"
            : minutes > 0
                ? $"{minutes}{SL["Duration_Minutes"]} {seconds}{SL["Duration_Seconds"]}"
                : $"{seconds}{SL["Duration_Seconds"]}";
    }

    #endregion

    #region Command Implementations

    /// <summary>
    /// Обрабатывает нажатие умной кнопки (Трансформера).
    /// <para>Чистая очередь -> мягкий Play/Pause.</para>
    /// <para>Измененная очередь / Другой источник -> Полный сброс и перезапуск плейлиста.</para>
    /// </summary>
    private async Task PlayAllAsync()
    {
        if (TrackCount == 0) return;

        if (IsQueuePure)
        {
            // Очередь чистая: работаем как стандартная кнопка Play/Pause
            await _playerControl.PlayPauseAsync().ConfigureAwait(false);
            return;
        }

        // Очередь грязная (или активен другой плейлист): полностью стираем очередь и начинаем заново
        var allTracks = await GetAllTracksAsync().ConfigureAwait(false);
        if (allTracks.Count == 0) return;

        _playerControl.SetShuffleEnabled(false);
        _playerControl.SetActivePlaylistId(_currentPlaylistId);
        IsShuffleActive = false;
        await Audio.StartQueueAsync(allTracks, allTracks[0]).ConfigureAwait(false);
    }

    private async Task ShufflePlayAsync()
    {
        if (TrackCount == 0) return;

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        var shuffled = new List<TrackInfo>(allTracks);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        _playerControl.SetShuffleEnabled(false);
        _playerControl.SetActivePlaylistId(_currentPlaylistId);
        await Audio.StartQueueAsync(shuffled, shuffled[0]);

        IsShuffleActive = true;
        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false)
            .DisposeWith(Disposables);
    }

    private async Task DownloadAllAsync()
    {
        IsDownloadingActive = true;

        var allTracks = await GetAllTracksAsync();
        foreach (var track in allTracks.Where(static t => !t.IsDownloaded))
            Downloads.StartDownload(track);

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false)
            .DisposeWith(Disposables);
    }

    private async Task PlayFromPlaylistAsync(TrackInfo track)
    {
        try
        {
            _playerControl.SetShuffleEnabled(false);
            _playerControl.SetActivePlaylistId(_currentPlaylistId);
            IsShuffleActive = false;
            var allTracks = await GetAllTracksAsync();
            await Audio.StartQueueAsync(allTracks, track);
            _ = LibService.AddToRecentlyPlayedAsync(track);
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] PlayFromPlaylist error: {ex.Message}");
        }
    }

    private async Task EditPlaylistAsync()
    {
        var result = await _editService.EditPlaylistAsync(
            _currentPlaylistId,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);

        if (result is { Changed: true })
            await LoadPlaylistAsync(_currentPlaylistId);
    }

    private async Task RefreshPlaylistAsync()
    {
        if (!IsCloud) return;

        if (Interlocked.Exchange(ref _syncInProgressGate, 1) != 0)
        {
            Log.Debug("[Playlist] Sync ignored: already in progress");
            return;
        }

        IsSyncing = true;
        _mainWindow.LockNavigation(SL["Playlist_SyncInProgress"] ?? "Syncing...");

        try
        {
            var notifications = AppEntry.Services
                .GetRequiredService<NotificationService>();

            if (IsLikedPlaylist)
            {
                await _manager.SyncLikedTracksAsync();
                InvalidateAllTracksCache();
                await LoadPlaylistAsync(_currentPlaylistId);

                await notifications.ShowToastAsync(
                    titleKey: "Playlist_SyncComplete_Toast_Title",
                    messageKey: "Sync_Success_Msg_LikedOnly",
                    severity: NotificationSeverity.Success,
                    durationMs: 4000);

                NotificationService.PlaySuccessSound();
            }
            else
            {
                var result = await _syncService.SyncWithDialogAsync(_currentPlaylistId);
                if (result is null) return;

                if (result.Success)
                {
                    InvalidateAllTracksCache();
                    await LoadPlaylistAsync(_currentPlaylistId);

                    if (result.TracksAddedLocally > 0 || result.TracksAddedToCloud > 0 ||
                        result.TracksRemovedLocally > 0 || result.TracksRemovedFromCloud > 0 ||
                        result.MetadataChanged)
                    {
                        await notifications.ShowToastAsync(
                            titleKey: "Playlist_SyncComplete_Toast_Title",
                            messageKey: "Playlist_SyncSuccess_Details",
                            messageArgs:
                            [
                                result.TracksAddedLocally,
                                result.TracksAddedToCloud,
                                result.TracksRemovedLocally,
                                result.TracksRemovedFromCloud
                            ],
                            severity: NotificationSeverity.Success,
                            durationMs: 4000);

                        NotificationService.PlaySuccessSound();
                    }
                }
                else
                {
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"] ?? "Error",
                        result.ErrorMessage ?? SL["Playlist_SyncFailed"] ?? "Sync failed");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Sync error: {ex.Message}");
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            _mainWindow.UnlockNavigation();
            Interlocked.Exchange(ref _syncInProgressGate, 0);
        }
    }

    private async Task CopyPlaylistLinkAsync()
    {
        if (_currentPlaylist?.YoutubeId is not { Length: > 0 } ytId)
        {
            CopyHintService.Instance.Show(
                SL["Playlist_CopyLink_NotLinked"] ?? "Not linked to YouTube",
                CopyHintKind.Warning);
            return;
        }

        await Clipboard.SetTextAsync($"https://www.youtube.com/playlist?list={ytId}");
        CopyHintService.Instance.Show(
            SL["Playlist_LinkCopied"] ?? "Copied!",
            CopyHintKind.Success);
    }

    private async Task LoadHeaderGradientAsync()
    {
        try
        {
            if (_currentPlaylist?.EffectiveColor is { } effectiveColorStr)
            {
                try
                {
                    var effectiveColor = Color.Parse(effectiveColorStr);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        HeaderBackground = DominantColorService.CreateHeaderGradient(effectiveColor));
                    return;
                }
                catch (FormatException)
                {
                    Log.Warn($"[Playlist] Invalid EffectiveColor: {effectiveColorStr}");
                }
            }

            if (string.IsNullOrEmpty(ThumbnailUrl)
                || !PlaylistEditorViewModel.IsValidUri(ThumbnailUrl))
            {
                await SetHeaderBackgroundAsync(null);
                return;
            }

            var dominantColor = await _dominantColor.GetDominantColorAsync(ThumbnailUrl);

            if (dominantColor is not null)
            {
                _ = SaveComputedColorAsync(dominantColor.Value);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    HeaderBackground = DominantColorService.CreateHeaderGradient(dominantColor.Value));
            }
            else
            {
                await SetHeaderBackgroundAsync(null);
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[Playlist] Thumbnail load failed (network): {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                string.Format(SL["Playlist_ThumbnailLoadFailed"] ?? "Could not load cover: {0}",
                    ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Log.Warn($"[Playlist] Invalid image: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                SL["Playlist_ThumbnailInvalidFormat"] ?? "Invalid cover format.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Gradient error: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
        }
    }

    private async Task SaveComputedColorAsync(Color color)
    {
        if (_currentPlaylist is null) return;

        var colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        if (string.Equals(_currentPlaylist.ComputedColor, colorHex, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _currentPlaylist.ComputedColor = colorHex;
            await LibService.AddOrUpdatePlaylistAsync(_currentPlaylist);
            Log.Debug($"[Playlist] ComputedColor saved: {colorHex}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Failed to save ComputedColor: {ex.Message}");
        }
    }

    private async Task SetHeaderBackgroundAsync(IBrush? brush) =>
        await Dispatcher.UIThread.InvokeAsync(
            () => HeaderBackground = brush);

    private async Task HydrateCacheStatusInBackgroundAsync()
    {
        try
        {
            var tracks = GetLoadedItemsSnapshot();
            var audioCache = AudioSourceFactory.GlobalCache;
            if (audioCache is null) return;
            await Task.Run(() => audioCache.HydrateCacheStatus(tracks));
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Cache hydration error: {ex.Message}");
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
            bool ok = await _manager.MergePlaylistsAsync(_currentPlaylistId, targetId);
            await _dialog.ShowInfoAsync(
                ok ? SL["Dialog_Success"] : SL["Dialog_Error_Title"],
                ok ? SL["Merge_Success_Msg"] : SL["Merge_Error_Msg"]);
        }
    }

    /// <summary>
    /// Добавляет в очередь воспроизведения только уникальные треки из текущего плейлиста.
    /// Делегирует выполнение общему расширению AudioEngine.
    /// </summary>
    private void EnqueueUniquePlaylistTracks()
    {
        var tracks = GetLoadedItemsSnapshot();
        if (tracks.Count == 0) return;

        Audio.EnqueuePlaylistWithNotification(tracks, PlaylistName);
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log.Debug($"[PlaylistVM] Disposing {_currentPlaylistId}");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _librarySubscription?.Dispose();
            InvalidateAllTracksCache();
            _currentPlaylist = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}