using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shell;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using LMP.Core.Youtube.Search;
using System.Reactive.Disposables;
using LMP.UI.Dialogs;

namespace LMP.Features.Library;

public sealed class LibraryViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly MainWindowViewModel _mainWindow;
    private readonly MusicLibraryManager _manager;

    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _staggerCts;
    private CancellationTokenSource? _statsAnimCts;
    private bool _isDisposed;
    private bool _dataChangeSubscribed;

    private const int StaggerDelayMs = 40;

    [Reactive] public bool IsContentReady { get; private set; }

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsSyncing { get; private set; }
    [Reactive] public double SyncProgress { get; private set; }
    [Reactive] public string SyncStatus { get; private set; } = "";
    [Reactive] public bool IsAuthenticated { get; private set; }

    // ═══ STATISTICS ═══
    [Reactive] public bool IsStatsVisible { get; private set; }
    [Reactive] public string PlaylistCountText { get; private set; } = "";
    [Reactive] public string TotalTracksText { get; private set; } = "";
    [Reactive] public string TotalDurationText { get; private set; } = "";
    [Reactive] public string AvgTrackDurationText { get; private set; } = "";
    [Reactive] public string AvgPlaylistDurationText { get; private set; } = "";

    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCreateCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncAccountPlaylistsCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelSyncCommand { get; }

    public LibraryViewModel(
        LibraryService library,
        YoutubeProvider youtube,
        CookieAuthService auth,
        MainWindowViewModel mainWindow,
        IDialogService dialog,
        MusicLibraryManager manager,
        AudioEngine audio)
    {
        _audio = audio;
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _dialog = dialog;
        _mainWindow = mainWindow;
        _manager = manager;

        IsAuthenticated = _auth.IsAuthenticated;
        _auth.OnAuthStateChanged += OnAuthChanged;

        OpenCreateCommand = CreateCommand(ReactiveCommand.CreateFromTask(OpenCreateDialogAsync));

        var canSync = this.WhenAnyValue(x => x.IsSyncing, syncing => !syncing);
        SyncAccountPlaylistsCommand = CreateCommand(ReactiveCommand.CreateFromTask(SyncAccountPlaylistsAsync, canSync));

        CancelSyncCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _syncCts?.Cancel();
            SyncStatus = SL["Sync_Cancelling"];
        }));

        RefreshCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadPlaylistsAsync));
    }

    private async Task OpenCreateDialogAsync()
    {
        if (_isDisposed) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var trimmedName = result.Name.Trim();
        bool wantsCloud = result.SyncToCloud && _auth.IsAuthenticated;

        string? youtubeId = null;

        // ═══ STEP 1: Если хочет создать в облаке — запрос к YouTube ═══

        if (wantsCloud)
        {
            _mainWindow.LockNavigation(SL["Playlist_CreatingCloud"] ?? "Creating in cloud...");
            try
            {
                youtubeId = await _youtube.CreatePlaylistAsync(trimmedName);

                if (string.IsNullOrEmpty(youtubeId))
                {
                    wantsCloud = false;
                    var createLocal = await OfferLocalFallbackAsync(
                        SL["Playlist_CloudCreateFailed_AskLocal"]
                            ?? "Could not create playlist in YouTube Music. Create locally?");
                    if (!createLocal) return;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Library] Cloud playlist creation failed: {ex.Message}");
                wantsCloud = false;

                var createLocal = await OfferLocalFallbackAsync(
                    string.Format(
                        SL["Playlist_CloudError_AskLocal"]
                            ?? "YouTube API error: {0}\n\nCreate locally instead?",
                        ex.Message));
                if (!createLocal) return;
            }
            finally
            {
                _mainWindow.UnlockNavigation();
            }
        }

        // ═══ STEP 2: Создаём локальный Playlist ═══

        var playlist = await _library.CreatePlaylistAsync(trimmedName);

        if (wantsCloud && !string.IsNullOrEmpty(youtubeId))
        {
            playlist.YoutubeId = youtubeId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
        }
        else
        {
            playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        }

        // ═══ STEP 3: Сохраняем и обновляем UI ═══

        await _library.AddOrUpdatePlaylistAsync(playlist);
        await LoadPlaylistsAsync();

        Log.Info($"[Library] Created playlist '{trimmedName}' " +
                 $"(Sync={playlist.SyncMode}, YtId={playlist.YoutubeId ?? "none"})");
    }

    /// <summary>
    /// Предлагает пользователю создать плейлист локально при ошибке облака.
    /// </summary>
    private async Task<bool> OfferLocalFallbackAsync(string message)
    {
        return await _dialog.ConfirmAsync(
            SL["Dialog_Warning_Title"] ?? "Warning",
            message,
            SL["Playlist_CreateLocal"] ?? "Create locally",
            SL["Button_Cancel"] ?? "Cancel");
    }

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        if (!_dataChangeSubscribed)
        {
            _dataChangeSubscribed = true;
            Observable.FromEvent(h => _library.OnDataChanged += h, h => _library.OnDataChanged -= h)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Where(_ => !_isDisposed && !IsSyncing)
                .Subscribe(async _ => await LoadPlaylistsAsync())
                .DisposeWith(Disposables);
        }

        await LoadPlaylistsAsync();
        IsContentReady = true;
    }

    private void OnAuthChanged()
    {
        if (_isDisposed) return;
        IsAuthenticated = _auth.IsAuthenticated;
    }

    private async Task SyncAccountPlaylistsAsync()
    {
        if (_isDisposed) return;

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        IsSyncing = true;
        IsStatsVisible = false;
        SyncProgress = 0;
        SyncStatus = SL["Sync_FetchingPlaylists"];

        _mainWindow.LockNavigation(SL["Sync_InProgress"]);

        try
        {
            List<PlaylistSearchResult> playlistsToImport = [];

            if (_auth.IsAuthenticated)
            {
                try
                {
                    var ytPlaylists = await YoutubeProvider.GetUserPlaylistsByAuthAsync();
                    ct.ThrowIfCancellationRequested();
                    SyncProgress = 0.1;

                    playlistsToImport = [.. ytPlaylists
                        .Where(p =>
                            !string.IsNullOrEmpty(p.YoutubeId) &&
                            p.YoutubeId != "LM" &&
                            p.YoutubeId != "VLLM" &&
                            !p.YoutubeId.StartsWith("RD"))
                        .Select(p =>
                        {
                            var pid = new Core.Youtube.Playlists.PlaylistId(p.YoutubeId!);
                            return new PlaylistSearchResult(pid, p.Name, null, []);
                        })];
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (!_isDisposed)
                        await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"],
                            SL["Sync_Error_API"] + ": " + ex.Message);
                    return;
                }
            }
            else if (_library.HasFakeAccount &&
                     !string.IsNullOrEmpty(_library.Settings.FakeAccountChannelUrl))
            {
                var result = await _youtube.GetChannelPlaylistsForSyncAsync(
                    _library.Settings.FakeAccountChannelUrl, ct);
                ct.ThrowIfCancellationRequested();
                SyncProgress = 0.1;
                if (result != null) playlistsToImport = result.Value.Playlists;
            }
            else
            {
                if (!_isDisposed)
                    await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Sync_ConfigureUrlFirst"]);
                return;
            }

            if (playlistsToImport.Count == 0)
            {
                if (_auth.IsAuthenticated)
                {
                    var confirmSyncLikes = await _dialog.ConfirmAsync(
                        SL["Sync_ConfirmLikedOnly"] ?? "Playlists Not Found",
                        SL["Sync_NoPlaylistsFound_AskLiked"] ?? "Sync Liked Songs?",
                        SL["Common_Yes"] ?? "Yes",
                        SL["Common_No"] ?? "No");

                    if (confirmSyncLikes)
                    {
                        SyncStatus = SL["Sync_LikedSongs"];
                        await _manager.SyncLikedTracksAsync();
                        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"],
                            SL["Sync_Success_Msg_LikedOnly"] ?? "Liked songs synced.");
                    }
                }
                else
                {
                    if (!_isDisposed)
                        await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Sync_NoPlaylists"]);
                }
                return;
            }

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.15;
            SyncStatus = SL["Sync_SelectPlaylists"];

            var allPlaylists = await _library.GetAllPlaylistsAsync(ct);
            var existingLocal = allPlaylists
                .Where(p => p.IsLocal)
                .ToDictionary(p => p.Name, p => p);
            var localNames = new HashSet<string>(existingLocal.Keys, StringComparer.Ordinal);

            var decisions = await _dialog.ShowSyncSelectionAsync(playlistsToImport, localNames);
            if (decisions.Count == 0 || _isDisposed) return;

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.2;

            Task? likedSongsSyncTask = null;
            if (_auth.IsAuthenticated)
            {
                likedSongsSyncTask = Task.Run(async () =>
                {
                    try { await _manager.SyncLikedTracksAsync(); }
                    catch (Exception ex) { Log.Error($"[Sync] Liked sync failed: {ex.Message}"); }
                }, ct);
            }

            SyncProgress = 0.25;
            SyncStatus = SL["Sync_ImportingPlaylists"];

            int importedCount = 0, mergedCount = 0, processed = 0;
            int totalToProcess = decisions.Count;

            foreach (var decision in decisions)
            {
                ct.ThrowIfCancellationRequested();
                if (_isDisposed) return;

                SyncStatus = string.Format(SL["Sync_ImportingPlaylist"], decision.Playlist.Title);

                var fullPlaylist = await _youtube.ImportPlaylistAsync(
                    decision.Playlist.Id.Value, _auth.IsAuthenticated, ct);
                if (fullPlaylist == null) { processed++; continue; }

                existingLocal.TryGetValue(decision.Playlist.Title, out var existing);

                if (decision.Action == MergeAction.Merge && existing != null)
                {
                    var existingTrackSet = new HashSet<string>(existing.TrackIds);
                    bool changed = false;
                    foreach (var trackId in fullPlaylist.TrackIds)
                    {
                        if (existingTrackSet.Add(trackId))
                        {
                            existing.TrackIds.Add(trackId);
                            changed = true;
                        }

                        var t = await _library.GetTrackAsync(trackId, ct);
                        if (t != null && !t.InPlaylists.Contains(existing.Id))
                        {
                            t.InPlaylists.Add(existing.Id);
                            await _library.AddOrUpdateTrackAsync(t, ct);
                        }
                    }

                    if (changed)
                    {
                        existing.UpdatedAt = DateTime.Now;
                        await _library.AddOrUpdatePlaylistAsync(existing, ct);
                    }

                    mergedCount++;
                }
                else
                {
                    if (decision.Action == MergeAction.Duplicate && existing != null)
                        fullPlaylist.Name = $"{decision.Playlist.Title} ({SL["Sync_DuplicateName"]})";

                    if (_auth.IsAuthenticated)
                    {
                        fullPlaylist.SyncMode = PlaylistSyncMode.TwoWaySync;
                        fullPlaylist.YoutubeId = decision.Playlist.Id.Value;
                    }

                    await _library.AddOrUpdatePlaylistAsync(fullPlaylist, ct);
                    importedCount++;
                }

                processed++;
                SyncProgress = 0.25 + (0.75 * processed / totalToProcess);
            }

            if (likedSongsSyncTask != null)
            {
                if (!likedSongsSyncTask.IsCompleted)
                    SyncStatus = SL["Sync_FinalizingLikedSongs"];
                await likedSongsSyncTask;
            }

            SyncProgress = 1.0;
            SyncStatus = SL["Sync_Complete"];

            if (!_isDisposed)
                await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"],
                    string.Format(SL["Sync_Success_Msg"], importedCount, mergedCount));
        }
        catch (OperationCanceledException)
        {
            SyncStatus = SL["Sync_Cancelled"];
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Sync error: {ex.Message}");
            if (!_isDisposed)
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], ex.Message);
        }
        finally
        {
            await Task.Delay(300);
            _mainWindow.UnlockNavigation();
            if (!_isDisposed)
            {
                IsSyncing = false;
                SyncProgress = 0;
                SyncStatus = "";
                await LoadPlaylistsAsync();
            }
        }
    }

    private async Task UpdateStatsAnimatedAsync()
    {
        if (_isDisposed) return;

        _statsAnimCts?.Cancel();
        _statsAnimCts?.Dispose();
        _statsAnimCts = new CancellationTokenSource();
        var ct = _statsAnimCts.Token;

        var targetPlaylists = Playlists.Count;
        var targetTracks = Playlists.Sum(p => p.TrackCount);

        var totalDuration = TimeSpan.Zero;
        int trackCountForAvg = 0;

        foreach (var vm in Playlists)
        {
            try
            {
                var tracks = await _library.GetPlaylistTracksAsync(vm.Id);
                foreach (var track in tracks)
                {
                    if (track.Duration > TimeSpan.Zero)
                    {
                        totalDuration += track.Duration;
                        trackCountForAvg++;
                    }
                }
            }
            catch { }
        }

        if (ct.IsCancellationRequested || _isDisposed) return;

        var avgTrack = trackCountForAvg > 0
            ? TimeSpan.FromTicks(totalDuration.Ticks / trackCountForAvg)
            : TimeSpan.Zero;
        var avgPlaylist = targetPlaylists > 0
            ? TimeSpan.FromTicks(totalDuration.Ticks / targetPlaylists)
            : TimeSpan.Zero;

        IsStatsVisible = true;

        int steps = 30;
        var rnd = new Random();

        for (int i = 1; i <= steps; i++)
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            double t = (double)i / steps;
            double ease = 1 - Math.Pow(1 - t, 3);

            int currentPlaylists = (int)(targetPlaylists * ease);
            int currentTracks = (int)(targetTracks * ease);

            if (i < steps)
            {
                currentPlaylists += rnd.Next(-2, 3);
                currentTracks += rnd.Next(-10, 11);
                if (currentPlaylists < 0) currentPlaylists = 0;
                if (currentTracks < 0) currentTracks = 0;
            }

            PlaylistCountText = SL.GetPlural("Library_PlaylistWord", currentPlaylists);
            TotalTracksText = SL.GetPlural("Library_TrackWord", currentTracks);

            if (i == steps)
            {
                TotalDurationText = FormatDurationLocalized(totalDuration);
                AvgTrackDurationText = $"⌀ {SL["Library_AvgTrack"]}: {FormatDurationShort(avgTrack)}";
                AvgPlaylistDurationText = $"⌀ {SL["Library_AvgPlaylist"]}: {FormatDurationLocalized(avgPlaylist)}";
            }
            else
            {
                TotalDurationText = "...";
                AvgTrackDurationText = "";
                AvgPlaylistDurationText = "";
            }

            await Task.Delay(20, ct);
        }
    }

    private static string FormatDurationLocalized(TimeSpan ts)
    {
        var h = (int)ts.TotalHours;
        var m = ts.Minutes;
        var s = ts.Seconds;

        if (h > 0) return $"{h} {SL["Time_Hours_Short"]} {m} {SL["Time_Minutes_Short"]}";
        if (m > 0) return $"{m} {SL["Time_Minutes_Short"]}";
        return $"{s} {SL["Time_Seconds_Short"]}";
    }

    private static string FormatDurationShort(TimeSpan ts)
    {
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed) return;

        _staggerCts?.Cancel();
        _staggerCts?.Dispose();
        _staggerCts = new CancellationTokenSource();
        var ct = _staggerCts.Token;

        IsStatsVisible = false;

        var allPlaylistsWithCounts = await _library.GetAllPlaylistsWithCountsAsync();
        if (_isDisposed || ct.IsCancellationRequested) return;

        var sorted = allPlaylistsWithCounts
            .OrderByDescending(x => x.Playlist.Id == LibraryService.LikedPlaylistId)
            .ThenByDescending(x => x.Playlist.IsLocal)
            .ThenBy(x => x.Playlist.Name)
            .ToList();

        var existingDict = Playlists.ToDictionary(vm => vm.Id);
        var newIdSet = new HashSet<string>(sorted.Select(x => x.Playlist.Id));

        var toRemove = Playlists.Where(vm => !newIdSet.Contains(vm.Id)).ToList();
        foreach (var vm in toRemove)
        {
            vm.Dispose();
            Playlists.Remove(vm);
        }

        var newItems = new List<(PlaylistCardViewModel vm, int targetIndex)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (playlist, trackCount) = sorted[i];

            if (existingDict.TryGetValue(playlist.Id, out var existingVm))
            {
                existingVm.UpdateFrom(playlist, trackCount);

                int currentIndex = Playlists.IndexOf(existingVm);
                if (currentIndex != i && currentIndex >= 0 && i < Playlists.Count)
                {
                    Playlists.Move(currentIndex, Math.Min(i, Playlists.Count - 1));
                }
            }
            else
            {
                var vm = CreatePlaylistCardVm(playlist, trackCount);
                newItems.Add((vm, i));
            }
        }

        foreach (var (vm, targetIndex) in newItems)
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            if (targetIndex >= Playlists.Count)
                Playlists.Add(vm);
            else
                Playlists.Insert(targetIndex, vm);
        }

        if (newItems.Count > 0)
        {
            foreach (var (vm, _) in newItems)
            {
                if (ct.IsCancellationRequested || _isDisposed) return;
                vm.Show();
                try { await Task.Delay(StaggerDelayMs, ct); }
                catch (OperationCanceledException)
                {
                    foreach (var (remaining, _) in newItems.Where(x => !x.vm.IsVisible))
                        remaining.Show();
                    break;
                }
            }
        }

        if (!_isDisposed && !ct.IsCancellationRequested)
        {
            _ = UpdateStatsAnimatedAsync();
        }
    }

    private PlaylistCardViewModel CreatePlaylistCardVm(Core.Models.Playlist playlist, int trackCount)
    {
        return new PlaylistCardViewModel(
            playlist,
            trackCount,
            _mainWindow.NavigateToPlaylist,
            async (p) =>
            {
                var tracks = await _library.GetPlaylistTracksAsync(p.Id);
                _audio.EnqueueRange(tracks);
            },
            async (p) =>
            {
                var tracks = await _library.GetPlaylistTracksAsync(p.Id);
                if (tracks.Count > 0)
                    await _audio.StartQueueAsync(tracks, tracks[0]);
            },
            DeletePlaylistAsync,
            EditPlaylistFromCardAsync);
    }

    private async Task EditPlaylistFromCardAsync(Core.Models.Playlist playlist)
    {
        if (_isDisposed) return;

        // Re-fetch from DB to get latest state
        var fresh = await _library.GetPlaylistAsync(playlist.Id);
        if (fresh == null) return;

        var result = await _dialog.ShowEditPlaylistDialogAsync(fresh);
        if (result == null) return;

        bool changed = false;

        // Name
        var newName = result.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(newName) &&
            !string.Equals(newName, fresh.Name, StringComparison.Ordinal))
        {
            if (fresh.IsFromAccount && !string.IsNullOrEmpty(fresh.YoutubeId))
            {
                _mainWindow.LockNavigation(SL["Playlist_Renaming"] ?? "Renaming...");
                try
                {
                    await _youtube.RenamePlaylistAsync(fresh.YoutubeId, newName);
                    fresh.Name = newName;
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[Library] YouTube rename failed: {ex.Message}");
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"] ?? "Error",
                        string.Format(SL["Playlist_RenameCloudFailed"] ?? "Rename failed: {0}", ex.Message));
                }
                finally
                {
                    _mainWindow.UnlockNavigation();
                }
            }
            else
            {
                fresh.Name = newName;
                changed = true;
            }
        }

        // Thumbnail
        if (!string.Equals(result.ThumbnailUrl, fresh.ThumbnailUrl, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                if (PlaylistEditorViewModel.IsValidUri(result.ThumbnailUrl))
                {
                    fresh.ThumbnailUrl = result.ThumbnailUrl;
                    changed = true;
                }
            }
            else
            {
                fresh.ThumbnailUrl = null;
                changed = true;
            }
        }

        // Color
        if (!string.Equals(result.CustomColor, fresh.CustomColor, StringComparison.Ordinal))
        {
            fresh.CustomColor = result.CustomColor;
            changed = true;
        }

        // Sync toggle
        if (result.SyncToCloud.HasValue && result.SyncToCloud.Value != fresh.IsFromAccount)
        {
            bool wantsSync = result.SyncToCloud.Value;

            if (wantsSync && !fresh.IsFromAccount && _auth.IsAuthenticated)
            {
                _mainWindow.LockNavigation(SL["Playlist_LinkingToCloud"] ?? "Linking...");
                try
                {
                    var ytId = await _youtube.CreatePlaylistAsync(fresh.Name);
                    if (!string.IsNullOrEmpty(ytId))
                    {
                        fresh.YoutubeId = ytId;
                        fresh.SyncMode = PlaylistSyncMode.TwoWaySync;
                        changed = true;
                        Log.Info($"[Library] Linked playlist to YouTube: {ytId}");

                        // Upload existing tracks in background
                        var trackIds = await _library.GetPlaylistTrackIdsAsync(fresh.Id);
                        if (trackIds.Count > 0)
                        {
                            _ = UploadTracksInBackgroundAsync(ytId, trackIds);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Library] Cloud link failed: {ex.Message}");
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"] ?? "Error",
                        string.Format(SL["Playlist_CloudLinkFailed"] ?? "Link failed: {0}", ex.Message));
                }
                finally
                {
                    _mainWindow.UnlockNavigation();
                }
            }
            else if (!wantsSync && fresh.IsFromAccount)
            {
                var confirm = await _dialog.ConfirmAsync(
                    SL["Dialog_Confirm_Title"] ?? "Confirm",
                    SL["Playlist_UnlinkConfirm"] ?? "Unlink from YouTube Music?",
                    SL["Playlist_Unlink"] ?? "Unlink",
                    SL["Button_Cancel"] ?? "Cancel");

                if (confirm)
                {
                    fresh.SyncMode = PlaylistSyncMode.LocalOnly;
                    fresh.YoutubeId = null;
                    changed = true;
                    Log.Info($"[Library] Unlinked playlist from YouTube: {fresh.Id}");
                }
            }
        }

        if (changed)
        {
            fresh.UpdatedAt = DateTime.Now;
            await _library.AddOrUpdatePlaylistAsync(fresh);
            Log.Info($"[Library] Saved playlist {fresh.Id}: SyncMode={fresh.SyncMode}, YoutubeId={fresh.YoutubeId ?? "null"}");
            await LoadPlaylistsAsync();
        }
    }

    /// <summary>
    /// Background upload of existing tracks to newly linked YouTube playlist.
    /// </summary>
    private async Task UploadTracksInBackgroundAsync(string youtubePlaylistId, List<string> trackIds)
    {
        try
        {
            int uploaded = 0;
            for (int i = 0; i < trackIds.Count; i++)
            {
                var trackId = trackIds[i];
                if (!trackId.StartsWith("yt_")) continue;

                await _youtube.AddToPlaylistAsync(youtubePlaylistId, trackId);
                uploaded++;

                // Rate limiting
                await Task.Delay(uploaded % 5 == 0 ? 1000 : 300);
            }

            Log.Info($"[Library] Uploaded {uploaded} tracks to YouTube playlist {youtubePlaylistId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Background upload failed: {ex.Message}");
        }
    }

    private async Task DeletePlaylistAsync(string playlistId)
    {
        if (_isDisposed) return;
        var playlist = await _library.GetPlaylistAsync(playlistId);
        if (playlist == null) return;

        if (playlistId == "liked")
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Playlist_CannotDeleteLiked"]);
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"],
            string.Format(SL["Playlist_DeleteConfirm"], playlist.Name),
            SL["Button_Delete"], SL["Button_Cancel"]);

        if (!confirmed) return;

        _mainWindow.LockNavigation(SL["Playlist_Deleting"]);
        try
        {
            await _manager.DeletePlaylistAsync(playlistId);
            await LoadPlaylistsAsync();
        }
        finally { _mainWindow.UnlockNavigation(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            _staggerCts?.Cancel();
            _staggerCts?.Dispose();
            _statsAnimCts?.Cancel();
            _statsAnimCts?.Dispose();
            foreach (var vm in Playlists) vm.Dispose();
            Playlists.Clear();
            _auth.OnAuthStateChanged -= OnAuthChanged;
            _syncCts?.Cancel();
            _syncCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}