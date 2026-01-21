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

        OpenCreateCommand = ReactiveCommand.Create(() =>
        {
            IsCreatingPlaylist = true;
            NewPlaylistName = "";
        });

        CancelCreateCommand = ReactiveCommand.Create(() =>
        {
            IsCreatingPlaylist = false;
            NewPlaylistName = "";
        });

        var canSave = this.WhenAnyValue(x => x.NewPlaylistName, n => !string.IsNullOrWhiteSpace(n));
        SavePlaylistCommand = ReactiveCommand.Create(() =>
        {
            var pl = _library.CreatePlaylist(NewPlaylistName.Trim());
            pl.AllowOffline = true;
            pl.AllowNetwork = true;
            _library.AddOrUpdatePlaylist(pl);

            IsCreatingPlaylist = false;
            NewPlaylistName = string.Empty;
            LoadPlaylists();
        }, canSave);

        // --- СИНХРОНИЗАЦИЯ (ИСПРАВЛЕНО НА ЛОКАЛИЗАЦИЮ) ---
        SyncAccountPlaylistsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            IsLoading = true;
            try
            {
                List<PlaylistSearchResult> playlistsToImport = [];

                if (_auth.IsAuthenticated)
                {
                    // "Синхронизация через OAuth пока в разработке..."
                    await _dialog.ShowInfoAsync(L["Dialog_Info_Title"], L["Sync_OAuthNotReady"]);
                }

                if (_library.HasFakeAccount && !string.IsNullOrEmpty(_library.Data.FakeAccountChannelUrl))
                {
                    var result = await _youtube.GetChannelPlaylistsForSyncAsync(_library.Data.FakeAccountChannelUrl);

                    if (result != null && result.Value.Playlists.Count > 0)
                    {
                        playlistsToImport = await _dialog.ShowSyncSelectionAsync(result.Value.Playlists);
                    }
                    else
                    {
                        // "На канале '{0}' не найдено публичных плейлистов."
                        string msg = string.Format(L["Sync_NoPlaylistsFound"], result?.ChannelName ?? "Unknown");
                        await _dialog.ShowInfoAsync(L["Library_SyncYoutube"], msg);
                        return;
                    }
                }
                else
                {
                    // "Настройте 'Fake Account Channel URL'..."
                    await _dialog.ShowInfoAsync(L["Library_SyncYoutube"], L["Sync_ConfigureUrlFirst"]);
                    return;
                }

                if (playlistsToImport.Count == 0) return;

                int importedCount = 0;
                int mergedCount = 0;

                foreach (var candidate in playlistsToImport)
                {
                    var existing = _library.GetAllPlaylists()
                        .FirstOrDefault(p => p.Name.Equals(candidate.Title, StringComparison.OrdinalIgnoreCase) && p.IsLocal);

                    bool shouldMerge = false;
                    string targetName = candidate.Title;

                    if (existing != null)
                    {
                        var decision = await _dialog.ShowMergeConflictDialogAsync(candidate.Title);

                        if (decision == "Skip") continue;
                        if (decision == "Merge") shouldMerge = true;
                        if (decision == "Duplicate") targetName = $"{candidate.Title} (Imported)";
                    }

                    var fullPlaylist = await _youtube.ImportPlaylistAsync(candidate.Id.Value);

                    if (fullPlaylist == null) continue;

                    if (shouldMerge && existing != null)
                    {
                        int newTracks = 0;
                        foreach (var trackId in fullPlaylist.TrackIds)
                        {
                            if (!existing.TrackIds.Contains(trackId))
                            {
                                existing.TrackIds.Add(trackId);
                                newTracks++;
                            }

                            var t = _library.GetTrack(trackId);
                            if (t != null && !t.InPlaylists.Contains(existing.Id))
                            {
                                t.InPlaylists.Add(existing.Id);
                                _library.AddOrUpdateTrack(t);
                            }
                        }

                        if (newTracks > 0)
                        {
                            existing.UpdatedAt = DateTime.Now;
                            _library.AddOrUpdatePlaylist(existing);
                            mergedCount++;
                        }
                    }
                    else
                    {
                        fullPlaylist.Name = targetName;
                        fullPlaylist.IsLocal = true;
                        _library.AddOrUpdatePlaylist(fullPlaylist);
                        importedCount++;
                    }
                }

                // "Готово", "Импортировано: {0}, Объединено: {1}"
                await _dialog.ShowInfoAsync(
                    L["Dialog_Done_Title"],
                    string.Format(L["Sync_Success_Msg"], importedCount, mergedCount));
            }
            catch (Exception ex)
            {
                // "Ошибка", "Сбой синхронизации: {0}"
                await _dialog.ShowInfoAsync(
                    L["Dialog_Error_Title"],
                    string.Format(L["Sync_Error_Msg"], ex.Message));
            }
            finally
            {
                LoadPlaylists();
                IsLoading = false;
            }
        });

        RefreshCommand = ReactiveCommand.Create(LoadPlaylists);

        Observable.FromEvent(
                h => _library.OnDataChanged += h,
                h => _library.OnDataChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => LoadPlaylists());

        LoadPlaylists();
    }

    private void LoadPlaylists()
    {
        Playlists.Clear();
        var sorted = _library.GetAllPlaylists()
                             .OrderByDescending(p => p.IsLocal)
                             .ThenBy(p => p.Name);

        foreach (var playlist in sorted)
        {
            Playlists.Add(new PlaylistCardViewModel(playlist, _navigateToPlaylist));
        }
    }
}