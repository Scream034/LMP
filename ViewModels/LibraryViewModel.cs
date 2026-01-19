// ViewModels/LibraryViewModel.cs
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.ViewModels;

public class PlaylistCardViewModel : ViewModelBase
{
    public Playlist Playlist { get; }
    public string Name => Playlist.Name;
    public string? ThumbnailUrl => Playlist.ThumbnailUrl;
    public int TrackCount => Playlist.TrackCount;
    public bool IsLocal => Playlist.IsLocal;
    public bool IsLiked => Playlist.Id == "liked";

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public PlaylistCardViewModel(Playlist playlist, Action<string> onOpen)
    {
        Playlist = playlist;
        OpenCommand = ReactiveCommand.Create(() => onOpen(playlist.Id));
    }
}

public class LibraryViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly GoogleAuthService _auth;
    private readonly Action<string> _navigateToPlaylist;

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public string NewPlaylistName { get; set; } = string.Empty;

    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = new();

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CreatePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncAccountPlaylistsCommand { get; }

    public LibraryViewModel(
        LibraryService library,
        YoutubeProvider youtube,
        GoogleAuthService auth,
        MainWindowViewModel mainWindow)
    {
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _navigateToPlaylist = mainWindow.NavigateToPlaylist;

        RefreshCommand = ReactiveCommand.Create(LoadPlaylists);

        var canCreate = this.WhenAnyValue(x => x.NewPlaylistName, n => !string.IsNullOrWhiteSpace(n));
        CreatePlaylistCommand = ReactiveCommand.Create(() =>
        {
            _library.CreatePlaylist(NewPlaylistName.Trim());
            NewPlaylistName = string.Empty;
            LoadPlaylists();
        }, canCreate);

        SyncAccountPlaylistsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_auth.IsAuthenticated) return;

            IsLoading = true;
            var accountPlaylists = await _youtube.GetUserPlaylistsAsync();
            _library.MergeAccountPlaylists(accountPlaylists);
            LoadPlaylists();
            IsLoading = false;
        });

        // Subscribe to library changes
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

        foreach (var playlist in _library.GetAllPlaylists())
        {
            Playlists.Add(new PlaylistCardViewModel(playlist, _navigateToPlaylist));
        }
    }
}