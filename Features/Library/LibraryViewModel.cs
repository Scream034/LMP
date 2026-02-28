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

        var playlist = await _library.CreatePlaylistAsync(result.Name.Trim());
        playlist.SyncMode = _auth.IsAuthenticated
            ? PlaylistSyncMode.TwoWaySync
            : PlaylistSyncMode.LocalOnly;
        await _library.AddOrUpdatePlaylistAsync(playlist);
        await LoadPlaylistsAsync();
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
                        SL["Sync_NoPlaylistsFound_AskLiked"] ?? "Do you want to sync Liked Songs?",
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

            var selected = await _dialog.ShowSyncSelectionAsync(playlistsToImport);
            if (selected.Count == 0 || _isDisposed) return;

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

            var allPlaylists = await _library.GetAllPlaylistsAsync(ct);
            var existingLocal = allPlaylists.Where(p => p.IsLocal).ToDictionary(p => p.Name, p => p);

            var conflicts = selected.Where(p => existingLocal.ContainsKey(p.Title)).Select(p => p.Title).ToList();

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

            int importedCount = 0, mergedCount = 0, processed = 0;
            int totalToProcess = selected.Count;

            foreach (var candidate in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (_isDisposed) return;

                var decision = decisionLookup.TryGetValue(candidate.Title, out var action)
                    ? action : MergeAction.Duplicate;

                if (decision == MergeAction.Skip) { processed++; continue; }

                SyncStatus = string.Format(SL["Sync_ImportingPlaylist"], candidate.Title);

                var fullPlaylist = await _youtube.ImportPlaylistAsync(candidate.Id.Value, _auth.IsAuthenticated, ct);
                if (fullPlaylist == null) { processed++; continue; }

                existingLocal.TryGetValue(candidate.Title, out var existing);

                if (decision == MergeAction.Merge && existing != null)
                {
                    var existingTrackSet = new HashSet<string>(existing.TrackIds);
                    bool changed = false;
                    foreach (var trackId in fullPlaylist.TrackIds)
                    {
                        if (existingTrackSet.Add(trackId)) { existing.TrackIds.Add(trackId); changed = true; }
                        var t = await _library.GetTrackAsync(trackId, ct);
                        if (t != null && !t.InPlaylists.Contains(existing.Id))
                        {
                            t.InPlaylists.Add(existing.Id);
                            await _library.AddOrUpdateTrackAsync(t, ct);
                        }
                    }
                    if (changed) { existing.UpdatedAt = DateTime.Now; await _library.AddOrUpdatePlaylistAsync(existing, ct); }
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
                if (!likedSongsSyncTask.IsCompleted) SyncStatus = SL["Sync_FinalizingLikedSongs"];
                await likedSongsSyncTask;
            }

            SyncProgress = 1.0;
            SyncStatus = SL["Sync_Complete"];

            if (!_isDisposed)
                await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"],
                    string.Format(SL["Sync_Success_Msg"], importedCount, mergedCount));
        }
        catch (OperationCanceledException) { SyncStatus = SL["Sync_Cancelled"]; }
        catch (Exception ex)
        {
            Log.Error($"[Library] Sync error: {ex.Message}");
            if (!_isDisposed) await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], ex.Message);
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

    /// <summary>
    /// Анимированный подсчет статистики.
    /// GetPlural уже возвращает строку с числом — не дублируем.
    /// </summary>
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

        var avgTrack = trackCountForAvg > 0 ? TimeSpan.FromTicks(totalDuration.Ticks / trackCountForAvg) : TimeSpan.Zero;
        var avgPlaylist = targetPlaylists > 0 ? TimeSpan.FromTicks(totalDuration.Ticks / targetPlaylists) : TimeSpan.Zero;

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

    /// <summary>
    /// Diff-алгоритм: обновляет, добавляет и удаляет карточки без Clear().
    /// Использует GetAllPlaylistsWithCountsAsync для получения актуального TrackCount.
    /// </summary>
    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed) return;

        _staggerCts?.Cancel();
        _staggerCts?.Dispose();
        _staggerCts = new CancellationTokenSource();
        var ct = _staggerCts.Token;

        IsStatsVisible = false;

        // Получаем данные из БД с количеством треков за один запрос
        var allPlaylistsWithCounts = await _library.GetAllPlaylistsWithCountsAsync();
        if (_isDisposed || ct.IsCancellationRequested) return;

        // Сортировка
        var sorted = allPlaylistsWithCounts
            .OrderByDescending(x => x.Playlist.Id == LibraryService.LikedPlaylistId)
            .ThenByDescending(x => x.Playlist.IsLocal)
            .ThenBy(x => x.Playlist.Name)
            .ToList();

        // ═══ DIFF ALGORITHM ═══

        var existingDict = Playlists.ToDictionary(vm => vm.Id);
        var newIdSet = new HashSet<string>(sorted.Select(x => x.Playlist.Id));

        // 1. Удаляем плейлисты, которых больше нет в БД
        var toRemove = Playlists.Where(vm => !newIdSet.Contains(vm.Id)).ToList();
        foreach (var vm in toRemove)
        {
            vm.Dispose();
            Playlists.Remove(vm);
        }

        // 2. Обновляем существующие и собираем новые
        var newItems = new List<(PlaylistCardViewModel vm, int targetIndex)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (playlist, trackCount) = sorted[i];

            if (existingDict.TryGetValue(playlist.Id, out var existingVm))
            {
                // Обновляем свойства без пересоздания VM
                existingVm.UpdateFrom(playlist, trackCount);

                // Проверяем позицию
                int currentIndex = Playlists.IndexOf(existingVm);
                if (currentIndex != i && currentIndex >= 0 && i < Playlists.Count)
                {
                    Playlists.Move(currentIndex, Math.Min(i, Playlists.Count - 1));
                }
            }
            else
            {
                // Новый плейлист — создаём VM с правильным trackCount
                var vm = CreatePlaylistCardVm(playlist, trackCount);
                newItems.Add((vm, i));
            }
        }

        // 3. Вставляем новые карточки на правильные позиции
        foreach (var (vm, targetIndex) in newItems)
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            if (targetIndex >= Playlists.Count)
                Playlists.Add(vm);
            else
                Playlists.Insert(targetIndex, vm);
        }

        // 4. Stagger reveal только для НОВЫХ карточек
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

        // 5. Анимируем статистику
        if (!_isDisposed && !ct.IsCancellationRequested)
        {
            _ = UpdateStatsAnimatedAsync();
        }
    }

    /// <summary>
    /// Фабричный метод: создаёт PlaylistCardViewModel с правильным trackCount из БД.
    /// </summary>
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
            DeletePlaylistAsync);
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