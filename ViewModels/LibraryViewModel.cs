using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using YoutubeExplode.Search;

namespace MyLiteMusicPlayer.ViewModels;

public class LibraryViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly GoogleAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly Action<string> _navigateToPlaylist;

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsCreatingPlaylist { get; set; }
    [Reactive] public string NewPlaylistName { get; set; } = string.Empty;
    [Reactive] public bool IsAuthenticated { get; private set; }

    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCreateCommand { get; }
    public ReactiveCommand<Unit, Unit> SavePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCreateCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncAccountPlaylistsCommand { get; }

    public LibraryViewModel(
        LibraryService library,
        YoutubeProvider youtube,
        GoogleAuthService auth,
        MainWindowViewModel mainWindow,
        IDialogService dialog)
    {
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _dialog = dialog;
        _navigateToPlaylist = mainWindow.NavigateToPlaylist;

        IsAuthenticated = _auth.IsAuthenticated;
        _auth.OnAuthStateChanged += () => IsAuthenticated = _auth.IsAuthenticated;

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

        // --- СИНХРОНИЗАЦИЯ ---
        SyncAccountPlaylistsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            IsLoading = true;
            try
            {
                List<PlaylistSearchResult> playlistsToImport = [];

                if (_auth.IsAuthenticated)
                {
                    try
                    {
                        // ИСПРАВЛЕНО: Используем _youtube (экземпляр), а не имя класса
                        var ytPlaylists = await _youtube.GetUserPlaylistsByAuthAsync();

                        playlistsToImport = ytPlaylists.Select(p =>
                        {
                            // 1. Безопасное создание ID
                            var pid = !string.IsNullOrEmpty(p.YoutubeId)
                                ? new YoutubeExplode.Playlists.PlaylistId(p.YoutubeId)
                                : new YoutubeExplode.Playlists.PlaylistId("PL0000000000000000");

                            // 2. Создание объекта Author из строки
                            var author = new YoutubeExplode.Common.Author(
                                new YoutubeExplode.Channels.ChannelId("UC0000000000000000000000"),
                                p.Author ?? "Unknown");

                            return new PlaylistSearchResult(pid, p.Name, author, []);
                        }).ToList();
                    }
                    catch (Exception ex)
                    {
                        await _dialog.ShowInfoAsync("Ошибка API", "Ошибка получения плейлистов: " + ex.Message);
                        return;
                    }
                }
                else if (_library.HasFakeAccount && !string.IsNullOrEmpty(_library.Data.FakeAccountChannelUrl))
                {
                    var result = await _youtube.GetChannelPlaylistsForSyncAsync(_library.Data.FakeAccountChannelUrl);
                    if (result != null) playlistsToImport = result.Value.Playlists;
                }
                else
                {
                    await _dialog.ShowInfoAsync(L["Library_SyncYoutube"], L["Sync_ConfigureUrlFirst"]);
                    return;
                }

                if (playlistsToImport.Count == 0)
                {
                    await _dialog.ShowInfoAsync(L["Library_SyncYoutube"], "Плейлисты не найдены.");
                    return;
                }

                var selected = await _dialog.ShowSyncSelectionAsync(playlistsToImport);
                if (selected.Count == 0) return;

                var existingLocalNames = _library.GetAllPlaylists().Where(p => p.IsLocal).Select(p => p.Name).ToHashSet();
                var conflicts = selected.Where(p => existingLocalNames.Contains(p.Title)).Select(p => p.Title).ToList();

                List<MergeDecision> decisions = [];
                if (conflicts.Count > 0)
                {
                    decisions = await _dialog.ShowMergeConflictResolutionDialogAsync(conflicts);
                }

                int importedCount = 0;
                int mergedCount = 0;

                foreach (var candidate in selected)
                {
                    var decision = decisions.FirstOrDefault(d => d.PlaylistName == candidate.Title)?.Action ?? MergeAction.Duplicate;
                    if (!conflicts.Contains(candidate.Title)) decision = MergeAction.Duplicate;
                    if (decision == MergeAction.Skip) continue;

                    var fullPlaylist = await _youtube.ImportPlaylistAsync(candidate.Id.Value, _auth.IsAuthenticated);
                    if (fullPlaylist == null) continue;

                    var existing = _library.GetAllPlaylists().FirstOrDefault(p => p.Name == candidate.Title && p.IsLocal);

                    if (decision == MergeAction.Merge && existing != null)
                    {
                        foreach (var trackId in fullPlaylist.TrackIds)
                        {
                            if (!existing.TrackIds.Contains(trackId)) existing.TrackIds.Add(trackId);
                            var t = _library.GetTrack(trackId);
                            if (t != null && !t.InPlaylists.Contains(existing.Id))
                            {
                                t.InPlaylists.Add(existing.Id);
                                _library.AddOrUpdateTrack(t);
                            }
                        }
                        existing.UpdatedAt = DateTime.Now;
                        _library.AddOrUpdatePlaylist(existing);
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
                }

                await _dialog.ShowInfoAsync(L["Dialog_Done_Title"], string.Format(L["Sync_Success_Msg"], importedCount, mergedCount));
            }
            catch (Exception ex)
            {
                await _dialog.ShowInfoAsync(L["Dialog_Error_Title"], ex.Message);
            }
            finally
            {
                LoadPlaylists();
                IsLoading = false;
            }
        });

        RefreshCommand = ReactiveCommand.Create(LoadPlaylists);
        Observable.FromEvent(h => _library.OnDataChanged += h, h => _library.OnDataChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler).Subscribe(_ => LoadPlaylists());
        LoadPlaylists();
    }

    private void LoadPlaylists()
    {
        Playlists.Clear();
        var allPlaylists = _library.GetAllPlaylists();
        var sorted = allPlaylists.OrderByDescending(p => p.IsLocal).ThenBy(p => p.Name);
        foreach (var playlist in sorted)
        {
            Playlists.Add(new PlaylistCardViewModel(playlist, _navigateToPlaylist));
        }
    }
}