using Avalonia.Controls;
using Avalonia.Media;
using LMP.Core.Audio;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shell;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LMP.UI.Dialogs;

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
    private readonly YoutubeProvider _youtube;
    private readonly MainWindowViewModel _mainWindow;

    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentPlaylistId = "";

    private readonly IDisposable? _librarySubscription;
    private readonly IDisposable? _audioStateSub;
    private readonly IDisposable? _trackChangeSub;

    private DateTime _lastMoveTime = DateTime.MinValue;
    private const int MoveDebounceMs = 1000;

    private List<TrackInfo>? _allTracksCache;
    private bool _allTracksCacheValid;

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
    public ReactiveCommand<Unit, Unit> EditPlaylistCommand { get; }

    #endregion

    #region Constructor

    public PlaylistViewModel(
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        IDialogService dialog,
        TrackViewModelFactory vmFactory,
        DominantColorService dominantColor,
        YoutubeProvider youtube,
        MainWindowViewModel mainWindow)
    {
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _dialog = dialog;
        _vmFactory = vmFactory;
        _dominantColor = dominantColor;
        _youtube = youtube;
        _mainWindow = mainWindow;

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

        RefreshPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => LoadPlaylistAsync(_currentPlaylistId)));

        ShufflePlayCommand = CreateCommand(ReactiveCommand.CreateFromTask(ShufflePlayAsync, hasTracks));
        DownloadAllCommand = CreateCommand(ReactiveCommand.CreateFromTask(DownloadAllAsync, hasTracks));
        MergePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit)));

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

        EditPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            EditPlaylistAsync,
            this.WhenAnyValue(x => x.CanEdit)));

        this.WhenAnyValue(x => x.CanEdit, x => x.FilterQuery)
            .Subscribe(_ =>
            {
                CanReorderItems = CanEdit && CanReorder;
            })
            .DisposeWith(Disposables);

        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
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

        _audioStateSub = Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if (_isSuspended) return;
                await CheckPlaybackStateAsync();
            });

        _trackChangeSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if (_isSuspended) return;
                await CheckPlaybackStateAsync();
            });
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
        _ = CheckPlaybackStateAsync();
        InvalidateAllTracksCache();
        this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }

    #endregion

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

    #region All Tracks Helper

    private async Task<List<TrackInfo>> GetAllTracksAsync()
    {
        if (_allTracksCacheValid && _allTracksCache != null)
            return _allTracksCache;

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

        _ = LoadHeaderGradientAsync();
        _ = HydrateCacheStatusInBackgroundAsync();
        await CheckPlaybackStateAsync();
    }

    #endregion

    #region Edit Playlist

    private async Task EditPlaylistAsync()
    {
        var playlist = await LibService.GetPlaylistAsync(_currentPlaylistId);
        if (playlist == null) return;

        var result = await _dialog.ShowEditPlaylistDialogAsync(playlist);
        if (result == null) return;

        // ═══ Определяем что изменилось ═══

        var newName = result.Name?.Trim();
        bool nameChanged = !string.IsNullOrWhiteSpace(newName)
                        && !string.Equals(newName, playlist.Name, StringComparison.Ordinal);
        bool thumbnailChanged = !string.Equals(
            result.ThumbnailUrl, playlist.ThumbnailUrl, StringComparison.Ordinal);
        bool colorChanged = !string.Equals(
            result.CustomColor, playlist.CustomColor, StringComparison.Ordinal);
        bool syncChanged = result.SyncToCloud.HasValue
                        && result.SyncToCloud.Value != playlist.IsFromAccount;

        if (!nameChanged && !thumbnailChanged && !colorChanged && !syncChanged)
            return;

        bool localChanged = false;

        // ═══ STEP 1: Обработка изменения синхронизации ═══

        if (syncChanged)
        {
            bool wantsSync = result.SyncToCloud!.Value;

            if (wantsSync && !playlist.IsFromAccount)
            {
                // Привязка к облаку
                localChanged |= await TryLinkToCloudAsync(playlist);
            }
            else if (!wantsSync && playlist.IsFromAccount)
            {
                // Отвязка от облака
                localChanged |= await TryUnlinkFromCloudAsync(playlist);
            }
        }

        // ═══ STEP 2: Переименование ═══

        if (nameChanged)
        {
            // Если плейлист привязан к облаку — синхронизируем название
            if (playlist.IsFromAccount && !string.IsNullOrEmpty(playlist.YoutubeId))
            {
                _mainWindow.LockNavigation(SL["Playlist_Renaming"] ?? "Renaming...");
                try
                {
                    await _youtube.RenamePlaylistAsync(playlist.YoutubeId, newName!);
                    playlist.Name = newName!;
                    localChanged = true;
                    Log.Info($"[Playlist] Renamed on YouTube: {playlist.YoutubeId} → '{newName}'");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Playlist] YouTube rename failed: {ex.Message}");
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"] ?? "Error",
                        string.Format(
                            SL["Playlist_RenameCloudFailed"]
                                ?? "Failed to rename on YouTube:\n{0}\n\nLocal name was not changed.",
                            ex.Message));
                    // НЕ применяем переименование
                }
                finally
                {
                    _mainWindow.UnlockNavigation();
                }
            }
            else
            {
                // Локальный плейлист — просто меняем
                playlist.Name = newName!;
                localChanged = true;
            }
        }

        // ═══ STEP 3: Thumbnail (только локально) ═══

        if (thumbnailChanged)
        {
            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                if (PlaylistEditorViewModel.IsValidUri(result.ThumbnailUrl))
                {
                    playlist.ThumbnailUrl = result.ThumbnailUrl;
                    localChanged = true;
                }
                else
                {
                    Log.Warn($"[Playlist] Invalid thumbnail URL: {result.ThumbnailUrl}");
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Warning_Title"] ?? "Warning",
                        SL["Error_InvalidThumbnailUrl"]
                            ?? "Invalid image URL. Thumbnail was not updated.");
                }
            }
            else
            {
                playlist.ThumbnailUrl = null;
                localChanged = true;
            }
        }

        // ═══ STEP 4: Color (только локально) ═══

        if (colorChanged)
        {
            playlist.CustomColor = result.CustomColor;
            localChanged = true;
        }

        // ═══ STEP 5: Сохранение ═══

        if (localChanged)
        {
            playlist.UpdatedAt = DateTime.Now;
            Log.Info($"[Playlist] Saving: SyncMode={playlist.SyncMode}, YoutubeId={playlist.YoutubeId ?? "null"}, Name={playlist.Name}");
            await LibService.AddOrUpdatePlaylistAsync(playlist);

            // Verify save
            var verify = await LibService.GetPlaylistAsync(_currentPlaylistId);
            Log.Info($"[Playlist] Verified: SyncMode={verify?.SyncMode}, YoutubeId={verify?.YoutubeId ?? "null"}");

            await LoadPlaylistAsync(_currentPlaylistId);
        }
    }

    /// <summary>
    /// Привязывает локальный плейлист к YouTube Music.
    /// </summary>
    private async Task<bool> TryLinkToCloudAsync(Core.Models.Playlist playlist)
    {
        _mainWindow.LockNavigation(
            SL["Playlist_LinkingToCloud"] ?? "Linking to YouTube Music...");
        try
        {
            var ytId = await _youtube.CreatePlaylistAsync(playlist.Name);

            if (string.IsNullOrEmpty(ytId))
            {
                await _dialog.ShowInfoAsync(
                    SL["Dialog_Error_Title"] ?? "Error",
                    SL["Playlist_CloudCreateFailed"]
                        ?? "Could not create playlist in YouTube Music.");
                return false;
            }

            playlist.YoutubeId = ytId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;

            Log.Info($"[Playlist] Linked to YouTube: {ytId}");

            // Load track IDs from DB (playlist.TrackIds may be empty — it's JsonIgnore)
            var trackIds = await LibService.GetPlaylistTrackIdsAsync(_currentPlaylistId);
            if (trackIds.Count > 0)
            {
                _ = UploadTracksInBackgroundAsync(ytId, trackIds);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Cloud link failed: {ex.Message}");
            await _dialog.ShowInfoAsync(
                SL["Dialog_Error_Title"] ?? "Error",
                string.Format(
                    SL["Playlist_CloudLinkFailed"]
                        ?? "Failed to link to YouTube Music:\n{0}",
                    ex.Message));
            return false;
        }
        finally
        {
            _mainWindow.UnlockNavigation();
        }
    }

    /// <summary>
    /// Отвязывает плейлист от YouTube Music (локальная копия становится независимой).
    /// </summary>
    private async Task<bool> TryUnlinkFromCloudAsync(Core.Models.Playlist playlist)
    {
        var confirm = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"] ?? "Confirm",
            SL["Playlist_UnlinkConfirm"]
                ?? "Unlink this playlist from YouTube Music?\n\n" +
                   "The playlist will remain in your YouTube account, " +
                   "but local changes will no longer sync.",
            SL["Playlist_Unlink"] ?? "Unlink",
            SL["Button_Cancel"] ?? "Cancel");

        if (!confirm) return false;

        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;

        Log.Info($"[Playlist] Unlinked from YouTube: {playlist.Id}");
        return true;
    }

    /// <summary>
    /// Фоновая загрузка треков в облачный плейлист.
    /// </summary>
    private async Task UploadTracksInBackgroundAsync(string youtubePlaylistId, List<string> trackIds)
    {
        if (trackIds.Count == 0) return;

        try
        {
            int uploaded = 0;
            for (int i = 0; i < trackIds.Count; i++)
            {
                if (!trackIds[i].StartsWith("yt_")) continue;

                await _youtube.AddToPlaylistAsync(youtubePlaylistId, trackIds[i]);
                uploaded++;

                // Rate limiting
                await Task.Delay(uploaded % 5 == 0 ? 1000 : 300);
            }

            Log.Info($"[Playlist] Uploaded {uploaded} tracks to YouTube playlist {youtubePlaylistId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Background upload failed: {ex.Message}");
        }
    }

    #endregion

    #region Private Methods

    private async Task LoadHeaderGradientAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(ThumbnailUrl))
            {
                await SetHeaderBackgroundAsync(null);
                return;
            }

            if (!PlaylistEditorViewModel.IsValidUri(ThumbnailUrl))
            {
                Log.Warn($"[Playlist] Invalid thumbnail URL skipped: {ThumbnailUrl}");
                await SetHeaderBackgroundAsync(null);
                return;
            }

            var dominantColor = await _dominantColor.GetDominantColorAsync(ThumbnailUrl);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                HeaderBackground = dominantColor != null
                    ? DominantColorService.CreateHeaderGradient(dominantColor.Value)
                    : null;
            });
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[Playlist] Thumbnail load failed (network): {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                string.Format(SL["Playlist_ThumbnailLoadFailed"]
                    ?? "Could not load cover image: {0}", ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Log.Warn($"[Playlist] Invalid image: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                SL["Playlist_ThumbnailInvalidFormat"]
                    ?? "The cover image format is invalid or unsupported.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Gradient error: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
        }
    }

    private async Task SetHeaderBackgroundAsync(IBrush? brush)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => HeaderBackground = brush);
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

        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;
        await _audio.StartQueueAsync(allTracks, allTracks[0]);
    }

    private async Task ShufflePlayAsync()
    {
        if (TrackCount == 0) return;

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        var shuffled = new List<TrackInfo>(allTracks);
        var rng = Random.Shared;
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        _audio.ShuffleEnabled = false;
        await _audio.StartQueueAsync(shuffled, shuffled[0]);

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

        foreach (var track in allTracks)
        {
            if (!track.IsDownloaded)
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
            FormattedDuration = $"{totalHours}{SL["Duration_Hours"]} {minutes}{SL["Duration_Minutes"]}";
        else if (minutes > 0)
            FormattedDuration = $"{minutes}{SL["Duration_Minutes"]} {seconds}{SL["Duration_Seconds"]}";
        else
            FormattedDuration = $"{seconds}{SL["Duration_Seconds"]}";
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