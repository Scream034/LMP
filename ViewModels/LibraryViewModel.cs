using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class LibraryViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly GoogleAuthService _auth;
    private readonly Action<string> _navigateToPlaylist;

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public string NewPlaylistName { get; set; } = string.Empty;

    // Теперь PlaylistCardViewModel определен в отдельном файле и доступен
    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = [];

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

        // Навигация через главную страницу
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
            var accountPlaylists = await YoutubeProvider.GetUserPlaylistsAsync();
            _library.MergeAccountPlaylists(accountPlaylists);
            LoadPlaylists();
            IsLoading = false;
        });

        // Подписка на изменения в библиотеке (например, добавление в избранное)
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

        // Превращаем модели данных из сервиса в ViewModels для отображения
        foreach (var playlist in _library.GetAllPlaylists())
        {
            Playlists.Add(new PlaylistCardViewModel(playlist, _navigateToPlaylist));
        }
    }
}