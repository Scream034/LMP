using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

/// <summary>
/// Главная ViewModel управления состоянием всего приложения и навигацией.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;

    [Reactive] public ViewModelBase? CurrentPage { get; private set; }
    [Reactive] public PlayerBarViewModel PlayerBar { get; private set; }
    [Reactive] public string? CurrentPageName { get; private set; }
    
    // Auth state
    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string? UserName { get; private set; }
    [Reactive] public string? UserAvatarUrl { get; private set; }

    // Commands
    public ReactiveCommand<Unit, Unit> NavigateHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateSettingsCommand { get; }
    public ReactiveCommand<string, Unit> NavigatePlaylistCommand { get; }

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

        Log.Info("MainWindowViewModel constructor started.");

        // Подписка на обновление состояния авторизации
        UpdateAuthState();
        Observable.FromEvent(h => _auth.OnAuthStateChanged += h, h => _auth.OnAuthStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateAuthState());

        // Определение команд навигации с логированием
        NavigateHomeCommand = ReactiveCommand.Create(() => SwitchPage<HomeViewModel>("Home"));
        NavigateSearchCommand = ReactiveCommand.Create(() => SwitchPage<SearchViewModel>("Search"));
        NavigateLibraryCommand = ReactiveCommand.Create(() => SwitchPage<LibraryViewModel>("Library"));
        NavigateSettingsCommand = ReactiveCommand.Create(() => SwitchPage<SettingsViewModel>("Settings"));
        
        NavigatePlaylistCommand = ReactiveCommand.Create<string>(id => {
            Log.Info($"Navigating to Playlist: {id}");
            try {
                var vm = Program.Services.GetRequiredService<PlaylistViewModel>();
                vm.LoadPlaylist(id);
                CurrentPage = vm;
                CurrentPageName = "Playlist";
            } catch (Exception ex) {
                Log.Info($"Playlist navigation failed: {ex.Message}\n{ex.StackTrace}");
            }
        });

        // Стартовая страница
        NavigateHomeCommand.Execute().Subscribe();
        Log.Info("MainWindowViewModel initialized.");
    }

    /// <summary>
    /// Обобщенный метод смены страниц с отладкой
    /// </summary>
    private void SwitchPage<T>(string name) where T : ViewModelBase
    {
        Log.Info($"Switching to page: {name} (Type: {typeof(T).Name})");
        try
        {
            var stopWatch = Stopwatch.StartNew();
            var page = Program.Services.GetRequiredService<T>();
            CurrentPage = page;
            CurrentPageName = name;
            stopWatch.Stop();
            Log.Info($"Successfully switched to {name} in {stopWatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Info($"Navigation to {name} failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void UpdateAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        UserName = _auth.State.UserName;
        UserAvatarUrl = _auth.State.UserAvatarUrl;
        Log.Info($"State updated. Authenticated: {IsAuthenticated}, User: {UserName}");
    }

    public void NavigateToPlaylist(string playlistId) 
        => NavigatePlaylistCommand.Execute(playlistId).Subscribe();
}