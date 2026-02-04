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

public class LibraryViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly MainWindowViewModel _mainWindow;
    private readonly MusicLibraryManager _manager;

    private CancellationTokenSource? _syncCts;
    private bool _isDisposed;

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsSyncing { get; private set; }
    [Reactive] public double SyncProgress { get; private set; }
    [Reactive] public string SyncStatus { get; private set; } = "";
    [Reactive] public bool IsCreatingPlaylist { get; set; }
    [Reactive] public string NewPlaylistName { get; set; } = string.Empty;
    [Reactive] public bool IsAuthenticated { get; private set; }

    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCreateCommand { get; }
    public ReactiveCommand<Unit, Unit> SavePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCreateCommand { get; }
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

        // FIX: Используем CreateCommand из ViewModelBase для предотвращения утечек
        OpenCreateCommand = CreateCommand(ReactiveCommand.Create(() => { IsCreatingPlaylist = true; NewPlaylistName = ""; }));
        CancelCreateCommand = CreateCommand(ReactiveCommand.Create(() => { IsCreatingPlaylist = false; NewPlaylistName = ""; }));

        var canSave = this.WhenAnyValue(x => x.NewPlaylistName, n => !string.IsNullOrWhiteSpace(n));
        SavePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            var playlist = await _library.CreatePlaylistAsync(NewPlaylistName.Trim());
            playlist.SyncMode = _auth.IsAuthenticated ? PlaylistSyncMode.TwoWaySync : PlaylistSyncMode.LocalOnly;
            await _library.AddOrUpdatePlaylistAsync(playlist);
            IsCreatingPlaylist = false;
            NewPlaylistName = string.Empty;
            await LoadPlaylistsAsync();
        }, canSave));

        var canSync = this.WhenAnyValue(x => x.IsSyncing, syncing => !syncing);
        SyncAccountPlaylistsCommand = CreateCommand(ReactiveCommand.CreateFromTask(SyncAccountPlaylistsAsync, canSync));

        CancelSyncCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _syncCts?.Cancel();
            SyncStatus = SL["Sync_Cancelling"];
        }));

        RefreshCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadPlaylistsAsync));

        Observable.FromEvent(
                h => _library.OnDataChanged += h,
                h => _library.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => !_isDisposed && !IsSyncing)
            .Subscribe(async _ => await LoadPlaylistsAsync())
            .DisposeWith(Disposables); // Используем Disposables из базового класса

        _ = LoadPlaylistsAsync();
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
                            !p.YoutubeId.StartsWith("RD")
                        )
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
                        await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Sync_Error_API"] + ": " + ex.Message);
                    return;
                }
            }
            else if (_library.HasFakeAccount && !string.IsNullOrEmpty(_library.Settings.FakeAccountChannelUrl))
            {
                var result = await _youtube.GetChannelPlaylistsForSyncAsync(_library.Settings.FakeAccountChannelUrl, ct);
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
                        SL["Sync_NoPlaylistsFound_AskLiked"] ?? "We couldn't find any public playlists. Do you want to sync your Liked Songs instead?",
                        SL["Common_Yes"] ?? "Yes",
                        SL["Common_No"] ?? "No"
                    );

                    if (confirmSyncLikes)
                    {
                        SyncStatus = SL["Sync_LikedSongs"];
                        await _manager.SyncLikedTracksAsync();
                        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Sync_Success_Msg_LikedOnly"] ?? "Liked songs synced successfully.");
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

            var selected = await _dialog.ShowSyncSelectionAsync(playlistsToImport);
            if (selected.Count == 0 || _isDisposed) return;

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.2;

            Task? likedSongsSyncTask = null;
            if (_auth.IsAuthenticated)
            {
                Log.Info("[Sync] Starting background Liked Songs sync...");
                likedSongsSyncTask = Task.Run(async () =>
                {
                    try { await _manager.SyncLikedTracksAsync(); }
                    catch (Exception ex) { Log.Error($"[Sync] Background liked songs sync failed: {ex.Message}"); }
                }, ct);
            }

            var allPlaylists = await _library.GetAllPlaylistsAsync(ct);
            var existingLocalPlaylists = allPlaylists
                .Where(p => p.IsLocal)
                .ToDictionary(p => p.Name, p => p);

            var conflicts = selected
                .Where(p => existingLocalPlaylists.ContainsKey(p.Title))
                .Select(p => p.Title)
                .ToList();

            List<MergeDecision> decisions = [];
            if (conflicts.Count > 0)
            {
                SyncStatus = SL["Sync_ResolvingConflicts"];
                decisions = await _dialog.ShowMergeConflictResolutionDialogAsync(conflicts);
                if (_isDisposed) return;
            }

            var decisionLookup = decisions.ToDictionary(d => d.PlaylistName, d => d.Action);

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.25;
            SyncStatus = SL["Sync_ImportingPlaylists"];

            int importedCount = 0;
            int mergedCount = 0;
            int totalToProcess = selected.Count;
            int processed = 0;

            foreach (var candidate in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (_isDisposed) return;

                var decision = decisionLookup.TryGetValue(candidate.Title, out var action)
                    ? action
                    : MergeAction.Duplicate;

                if (decision == MergeAction.Skip)
                {
                    processed++;
                    continue;
                }

                SyncStatus = string.Format(SL["Sync_ImportingPlaylist"], candidate.Title);

                var fullPlaylist = await _youtube.ImportPlaylistAsync(candidate.Id.Value, _auth.IsAuthenticated, ct);
                if (fullPlaylist == null)
                {
                    processed++;
                    continue;
                }

                existingLocalPlaylists.TryGetValue(candidate.Title, out var existing);

                if (decision == MergeAction.Merge && existing != null)
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

                        // Changed: Use async method
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
                    if (decision == MergeAction.Duplicate && existing != null)
                        fullPlaylist.Name = $"{candidate.Title} ({SL["Sync_DuplicateName"]})";

                    if (_auth.IsAuthenticated)
                    {
                        fullPlaylist.SyncMode = PlaylistSyncMode.TwoWaySync;
                        fullPlaylist.YoutubeId = candidate.Id.Value;
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
                {
                    SyncStatus = SL["Sync_FinalizingLikedSongs"];
                }
                await likedSongsSyncTask;
            }

            SyncProgress = 1.0;
            SyncStatus = SL["Sync_Complete"];

            if (!_isDisposed)
                await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], string.Format(SL["Sync_Success_Msg"], importedCount, mergedCount));
        }
        catch (OperationCanceledException)
        {
            Log.Info("[Library] Sync cancelled");
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

    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed) return;

        foreach (var vm in Playlists) vm.Dispose();
        Playlists.Clear();

        var allPlaylists = await _library.GetAllPlaylistsAsync();

        var sorted = allPlaylists
            .OrderByDescending(p => p.Id == LibraryService.LikedPlaylistId)
            .ThenByDescending(p => p.IsLocal)
            .ThenBy(p => p.Name);

        foreach (var playlist in sorted)
        {
            Playlists.Add(new PlaylistCardViewModel(
                playlist,
                _mainWindow.NavigateToPlaylist,
                async (p) =>
                {
                    var tracks = await _library.GetPlaylistTracksAsync(p.Id);
                    _audio.EnqueueRange(tracks);
                },
                DeletePlaylistAsync));
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
            SL["Button_Delete"],
            SL["Button_Cancel"]);

        if (!confirmed) return;

        _mainWindow.LockNavigation(SL["Playlist_Deleting"]);
        try
        {
            await _manager.DeletePlaylistAsync(playlistId);
            await LoadPlaylistsAsync();
        }
        finally
        {
            _mainWindow.UnlockNavigation();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true; // Устанавливаем флаг в начале
            foreach (var vm in Playlists) vm.Dispose();
            Playlists.Clear();

            _auth.OnAuthStateChanged -= OnAuthChanged;
            _syncCts?.Cancel();
            _syncCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}