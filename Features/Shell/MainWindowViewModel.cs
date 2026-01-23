using System.Reactive;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Diagnostics;
using MyLiteMusicPlayer.Features.Player;
using MyLiteMusicPlayer.Core.ViewModels;
using MyLiteMusicPlayer.Features.Search;
using MyLiteMusicPlayer.Features.Home;
using MyLiteMusicPlayer.Features.Library;
using MyLiteMusicPlayer.Features.Settings;
using MyLiteMusicPlayer.Features.Playlist;

namespace MyLiteMusicPlayer.Features.Shell;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly LibraryService _library;

    [Reactive] public ViewModelBase? CurrentPage { get; private set; }
    [Reactive] public PlayerBarViewModel PlayerBar { get; private set; }
    [Reactive] public string CurrentPageName { get; private set; } = "Home";

    // Блокировка навигации
    [Reactive] public bool IsNavigationLocked { get; private set; }
    [Reactive] public string NavigationLockReason { get; private set; } = "";

    // Команды навигации
    public ReactiveCommand<string, Unit> NavigateCommand { get; }

    public MainWindowViewModel(
        IServiceProvider services,
        PlayerBarViewModel playerBar,
        LibraryService library)
    {
        Log.Info("MainWindowViewModel constructor started.");

        _services = services;
        _library = library;
        PlayerBar = playerBar;

        // Навигация возможна только если не заблокирована
        var canNavigate = this.WhenAnyValue(x => x.IsNavigationLocked, locked => !locked);

        NavigateCommand = ReactiveCommand.Create<string>(pageName =>
        {
            if (!IsNavigationLocked)
            {
                Navigate(pageName);
            }
        }, canNavigate);

        Navigate("Home");

        Log.Info("MainWindowViewModel initialized.");
    }

    /// <summary>
    /// Блокирует навигацию на время выполнения операции
    /// </summary>
    public void LockNavigation(string reason)
    {
        IsNavigationLocked = true;
        NavigationLockReason = reason;
        Log.Info($"[Navigation] Locked: {reason}");
    }

    /// <summary>
    /// Разблокирует навигацию
    /// </summary>
    public void UnlockNavigation()
    {
        IsNavigationLocked = false;
        NavigationLockReason = "";
        Log.Info("[Navigation] Unlocked");
    }

    /// <summary>
    /// Выполняет операцию с блокировкой навигации
    /// </summary>
    public async Task WithNavigationLockAsync(string reason, Func<Task> operation)
    {
        LockNavigation(reason);
        try
        {
            await operation();
        }
        finally
        {
            UnlockNavigation();
        }
    }

    /// <summary>
    /// Выполняет операцию с блокировкой навигации и возвратом результата
    /// </summary>
    public async Task<T> WithNavigationLockAsync<T>(string reason, Func<Task<T>> operation)
    {
        LockNavigation(reason);
        try
        {
            return await operation();
        }
        finally
        {
            UnlockNavigation();
        }
    }

    private void Navigate(string pageName)
    {
        if (IsNavigationLocked) return;

        var sw = Stopwatch.StartNew();
        Log.Info($"Switching to page: {pageName} (Type: {pageName}ViewModel)");

        // Диспозим текущую страницу если она IDisposable
        if (CurrentPage is IDisposable disposable)
        {
            disposable.Dispose();
        }

        CurrentPage = pageName switch
        {
            "Home" => _services.GetRequiredService<HomeViewModel>(),
            "Search" => _services.GetRequiredService<SearchViewModel>(),
            "Library" => _services.GetRequiredService<LibraryViewModel>(),
            "Settings" => _services.GetRequiredService<SettingsViewModel>(),
            "Queue" => _services.GetRequiredService<QueueViewModel>(),
            _ => CurrentPage
        };

        CurrentPageName = pageName;
        sw.Stop();
        Log.Info($"Successfully switched to {pageName} in {sw.ElapsedMilliseconds}ms");
    }

    public void NavigateToPlaylist(string playlistId)
    {
        if (IsNavigationLocked) return;

        Log.Info($"Navigating to Playlist: {playlistId}");

        // Диспозим текущую страницу
        if (CurrentPage is IDisposable disposable)
        {
            disposable.Dispose();
        }

        var playlistVM = _services.GetRequiredService<PlaylistViewModel>();
        playlistVM.LoadPlaylist(playlistId);
        CurrentPage = playlistVM;
        CurrentPageName = "Playlist";
    }
}

