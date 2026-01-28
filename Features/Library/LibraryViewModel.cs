// Features/Library/LibraryViewModel.cs
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

namespace LMP.Features.Library;

public class LibraryViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly MainWindowViewModel _mainWindow;
    private readonly MusicLibraryManager _manager;

    private CancellationTokenSource? _syncCts;
    private IDisposable? _librarySubscription;
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

        OpenCreateCommand = ReactiveCommand.Create(() => { IsCreatingPlaylist = true; NewPlaylistName = ""; });
        CancelCreateCommand = ReactiveCommand.Create(() => { IsCreatingPlaylist = false; NewPlaylistName = ""; });

        var canSave = this.WhenAnyValue(x => x.NewPlaylistName, n => !string.IsNullOrWhiteSpace(n));
        // Changed: Use async command
        SavePlaylistCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var playlist = await _library.CreatePlaylistAsync(NewPlaylistName.Trim());
            playlist.SyncMode = _auth.IsAuthenticated ? PlaylistSyncMode.TwoWaySync : PlaylistSyncMode.LocalOnly;
            await _library.AddOrUpdatePlaylistAsync(playlist);
            IsCreatingPlaylist = false;
            NewPlaylistName = string.Empty;
            await LoadPlaylistsAsync();
        }, canSave);

        var canSync = this.WhenAnyValue(x => x.IsSyncing, syncing => !syncing);
        SyncAccountPlaylistsCommand = ReactiveCommand.CreateFromTask(SyncAccountPlaylistsAsync, canSync);

        CancelSyncCommand = ReactiveCommand.Create(() =>
        {
            _syncCts?.Cancel();
            SyncStatus = SL["Sync_Cancelling"];
        });

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadPlaylistsAsync);

        _librarySubscription = Observable.FromEvent(
                h => _library.OnDataChanged += h,
                h => _library.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => !_isDisposed && !IsSyncing)
            .Subscribe(async _ => await LoadPlaylistsAsync());

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
            // Changed: Use Settings property
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
                    SyncStatus = SL["Sync_LikedSongs"];
                    await _manager.SyncLikedTracksAsync();
                }

                if (!_isDisposed)
                    await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Sync_NoPlaylists"]);
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
                    try
                    {
                        await _manager.SyncLikedTracksAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Sync] Background liked songs sync failed: {ex.Message}");
                    }
                }, ct);
            }

            // Changed: Use async method
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

                var decision = MergeAction.Duplicate;
                if (decisionLookup.TryGetValue(candidate.Title, out var action))
                {
                    decision = action;
                }
                else if (!existingLocalPlaylists.ContainsKey(candidate.Title))
                {
                    decision = MergeAction.Duplicate;
                }

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

        // Dispose old ViewModels
        foreach (var vm in Playlists) vm.Dispose();
        Playlists.Clear();

        // GetAllPlaylistsAsync now returns playlists with TrackCount populated
        var allPlaylists = await _library.GetAllPlaylistsAsync();

        // Sort: Liked first, then local, then by name
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

        // Changed: Use async method
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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var vm in Playlists) vm.Dispose();
        Playlists.Clear();

        _auth.OnAuthStateChanged -= OnAuthChanged;
        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _librarySubscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}