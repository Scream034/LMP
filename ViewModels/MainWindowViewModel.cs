// ViewModels/MainWindowViewModel.cs
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;

    [Reactive] public ViewModelBase? CurrentPage { get; private set; }
    [Reactive] public PlayerBarViewModel PlayerBar { get; private set; }
    [Reactive] public string? CurrentPageName { get; private set; }
    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string? UserName { get; private set; }
    [Reactive] public string? UserAvatarUrl { get; private set; }

    // Navigation commands
    public ReactiveCommand<Unit, Unit> NavigateHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateSettingsCommand { get; }
    public ReactiveCommand<string, Unit> NavigatePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }

    public MainWindowViewModel(
        AudioEngine audio,
        LibraryService library,
        GoogleAuthService auth,
        PlayerBarViewModel playerBar)
    {
        _audio = audio;
        _library = library;
        _auth = auth;
        PlayerBar = playerBar;

        UpdateAuthState();

        // Subscribe to auth changes
        Observable.FromEvent(
                h => _auth.OnAuthStateChanged += h,
                h => _auth.OnAuthStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateAuthState());

        // Navigation commands
        NavigateHomeCommand = ReactiveCommand.Create(() =>
        {
            CurrentPage = Program.Services.GetRequiredService<HomeViewModel>();
            CurrentPageName = "Home";
        });

        NavigateSearchCommand = ReactiveCommand.Create(() =>
        {
            CurrentPage = Program.Services.GetRequiredService<SearchViewModel>();
            CurrentPageName = "Search";
        });

        NavigateLibraryCommand = ReactiveCommand.Create(() =>
        {
            CurrentPage = Program.Services.GetRequiredService<LibraryViewModel>();
            CurrentPageName = "Library";
        });

        NavigateSettingsCommand = ReactiveCommand.Create(() =>
        {
            CurrentPage = Program.Services.GetRequiredService<SettingsViewModel>();
            CurrentPageName = "Settings";
        });

        NavigatePlaylistCommand = ReactiveCommand.Create<string>(playlistId =>
        {
            var vm = Program.Services.GetRequiredService<PlaylistViewModel>();
            vm.LoadPlaylist(playlistId);
            CurrentPage = vm;
            CurrentPageName = "Playlist";
        });

        LoginCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _auth.StartLoginAsync();
        });

        LogoutCommand = ReactiveCommand.Create(() =>
        {
            _auth.Logout();
        });

        // Start with Home page
        NavigateHomeCommand.Execute().Subscribe();
    }

    private void UpdateAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        UserName = _auth.State.UserName;
        UserAvatarUrl = _auth.State.UserAvatarUrl;
    }

    public void NavigateToPlaylist(string playlistId)
    {
        NavigatePlaylistCommand.Execute(playlistId).Subscribe();
    }
}