using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using YoutubeExplode.Search;

namespace MyLiteMusicPlayer.ViewModels;

public class LibraryViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly GoogleAuthService _auth;
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
        GoogleAuthService auth,
        MainWindowViewModel mainWindow,
        IDialogService dialog,
        MusicLibraryManager manager)
    {
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
        SavePlaylistCommand = ReactiveCommand.Create(() =>
        {
            var playlist = _library.CreatePlaylist(NewPlaylistName.Trim());
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
            _library.AddOrUpdatePlaylist(playlist);
            IsCreatingPlaylist = false;
            NewPlaylistName = string.Empty;
            LoadPlaylists();
        }, canSave);

        var canSync = this.WhenAnyValue(x => x.IsSyncing, syncing => !syncing);
        SyncAccountPlaylistsCommand = ReactiveCommand.CreateFromTask(SyncAccountPlaylistsAsync, canSync);
        
        CancelSyncCommand = ReactiveCommand.Create(() =>
        {
            _syncCts?.Cancel();
            SyncStatus = L["Sync_Cancelling"];
        });

        RefreshCommand = ReactiveCommand.Create(LoadPlaylists);
        
        _librarySubscription = Observable.FromEvent(
                h => _library.OnDataChanged += h, 
                h => _library.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => !_isDisposed && !IsSyncing)
            .Subscribe(_ => LoadPlaylists());
        
        LoadPlaylists();
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
        SyncStatus = L["Sync_FetchingPlaylists"];
        
        _mainWindow.LockNavigation(L["Sync_InProgress"]);
        
        try
        {
            List<PlaylistSearchResult> playlistsToImport = [];

            if (_auth.IsAuthenticated)
            {
                try
                {
                    var ytPlaylists = await _youtube.GetUserPlaylistsByAuthAsync();
                    ct.ThrowIfCancellationRequested();
                    SyncProgress = 0.1;

                    playlistsToImport = ytPlaylists.Select(p =>
                    {
                        var pid = !string.IsNullOrEmpty(p.YoutubeId)
                            ? new YoutubeExplode.Playlists.PlaylistId(p.YoutubeId)
                            : new YoutubeExplode.Playlists.PlaylistId("PL0000000000000000");

                        var author = new YoutubeExplode.Common.Author(
                            new YoutubeExplode.Channels.ChannelId("UC0000000000000000000000"),
                            p.Author ?? "Unknown");

                        return new PlaylistSearchResult(pid, p.Name, author, []);
                    }).ToList();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (!_isDisposed)
                        await _dialog.ShowInfoAsync(L["Dialog_Error_Title"], L["Sync_Error_API"] + ": " + ex.Message);
                    return;
                }
            }
            else if (_library.HasFakeAccount && !string.IsNullOrEmpty(_library.Data.FakeAccountChannelUrl))
            {
                var result = await _youtube.GetChannelPlaylistsForSyncAsync(_library.Data.FakeAccountChannelUrl, ct);
                ct.ThrowIfCancellationRequested();
                SyncProgress = 0.1;
                if (result != null) playlistsToImport = result.Value.Playlists;
            }
            else
            {
                if (!_isDisposed)
                    await _dialog.ShowInfoAsync(L["Library_SyncYoutube"], L["Sync_ConfigureUrlFirst"]);
                return;
            }

            if (playlistsToImport.Count == 0)
            {
                if (!_isDisposed)
                    await _dialog.ShowInfoAsync(L["Library_SyncYoutube"], L["Sync_NoPlaylists"]);
                return;
            }

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.15;
            SyncStatus = L["Sync_SelectPlaylists"];

            var selected = await _dialog.ShowSyncSelectionAsync(playlistsToImport);
            if (selected.Count == 0 || _isDisposed) return;

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.2;

            var existingLocalPlaylists = _library.GetAllPlaylists()
                .Where(p => p.IsLocal)
                .ToDictionary(p => p.Name, p => p);
            
            var conflicts = selected
                .Where(p => existingLocalPlaylists.ContainsKey(p.Title))
                .Select(p => p.Title)
                .ToList();

            List<MergeDecision> decisions = [];
            if (conflicts.Count > 0)
            {
                SyncStatus = L["Sync_ResolvingConflicts"];
                decisions = await _dialog.ShowMergeConflictResolutionDialogAsync(conflicts);
                if (_isDisposed) return;
            }
            
            var decisionLookup = decisions.ToDictionary(d => d.PlaylistName, d => d.Action);

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.25;
            SyncStatus = L["Sync_ImportingPlaylists"];

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

                SyncStatus = string.Format(L["Sync_ImportingPlaylist"], candidate.Title);

                var fullPlaylist = await _youtube.ImportPlaylistAsync(candidate.Id.Value, _auth.IsAuthenticated, ct);
                if (fullPlaylist == null)
                {
                    processed++;
                    continue;
                }

                existingLocalPlaylists.TryGetValue(candidate.Title, out var existing);

                if (decision == MergeAction.Merge && existing != null)
                {
                    // Оптимизация: Алгоритмическая сложность
                    // Преобразуем список существующих треков в HashSet для поиска O(1)
                    // Вместо Contains на List (O(N)), что дает суммарную сложность O(M) вместо O(N*M)
                    var existingTrackSet = new HashSet<string>(existing.TrackIds);
                    bool changed = false;

                    foreach (var trackId in fullPlaylist.TrackIds)
                    {
                        if (existingTrackSet.Add(trackId))
                        {
                            existing.TrackIds.Add(trackId);
                            changed = true;
                        }

                        // Обновляем метаданные трека
                        var t = _library.GetTrack(trackId);
                        if (t != null && !t.InPlaylists.Contains(existing.Id))
                        {
                            t.InPlaylists.Add(existing.Id);
                            _library.AddOrUpdateTrack(t);
                        }
                    }

                    if (changed)
                    {
                        existing.UpdatedAt = DateTime.Now;
                        _library.AddOrUpdatePlaylist(existing);
                    }
                    mergedCount++;
                }
                else
                {
                    if (decision == MergeAction.Duplicate && existing != null)
                        fullPlaylist.Name = $"{candidate.Title} (Imported)";

                    if (_auth.IsAuthenticated)
                    {
                        fullPlaylist.SyncMode = PlaylistSyncMode.TwoWaySync;
                        fullPlaylist.YoutubeId = candidate.Id.Value;
                    }
                    _library.AddOrUpdatePlaylist(fullPlaylist);
                    importedCount++;
                }

                processed++;
                SyncProgress = 0.25 + (0.75 * processed / totalToProcess);
            }

            SyncProgress = 1.0;
            SyncStatus = L["Sync_Complete"];

            if (!_isDisposed)
                await _dialog.ShowInfoAsync(L["Dialog_Done_Title"], string.Format(L["Sync_Success_Msg"], importedCount, mergedCount));
        }
        catch (OperationCanceledException)
        {
            Log.Info("[Library] Sync cancelled");
            SyncStatus = L["Sync_Cancelled"];
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Sync error: {ex.Message}");
            if (!_isDisposed)
                await _dialog.ShowInfoAsync(L["Dialog_Error_Title"], ex.Message);
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
                LoadPlaylists();
            }
        }
    }

    private void LoadPlaylists()
    {
        if (_isDisposed) return;
        
        Playlists.Clear();
        var allPlaylists = _library.GetAllPlaylists();
        var sorted = allPlaylists.OrderByDescending(p => p.IsLocal).ThenBy(p => p.Name);
        foreach (var playlist in sorted)
        {
            Playlists.Add(new PlaylistCardViewModel(
                playlist, 
                _mainWindow.NavigateToPlaylist,
                DeletePlaylistAsync));
        }
    }

    private async Task DeletePlaylistAsync(string playlistId)
    {
        if (_isDisposed) return;
        
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;
        
        if (playlistId == "liked")
        {
            await _dialog.ShowInfoAsync(L["Dialog_Error_Title"], L["Playlist_CannotDeleteLiked"]);
            return;
        }
        
        var confirmed = await _dialog.ConfirmAsync(
            L["Dialog_Confirm_Title"],
            string.Format(L["Playlist_DeleteConfirm"], playlist.Name),
            L["Button_Delete"],
            L["Button_Cancel"]);
        
        if (!confirmed) return;
        
        _mainWindow.LockNavigation(L["Playlist_Deleting"]);
        
        try
        {
            await _manager.DeletePlaylistAsync(playlistId);
            LoadPlaylists();
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
        
        _auth.OnAuthStateChanged -= OnAuthChanged;
        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _librarySubscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}