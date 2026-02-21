using System.Reactive;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Diagnostics;
using LMP.Features.Player;
using LMP.Core.ViewModels;
using LMP.Features.Search;
using LMP.Features.Home;
using LMP.Features.Library;
using LMP.Features.Settings;
using LMP.Features.Playlist;
using System.Reactive.Concurrency;
using System.Runtime;

namespace LMP.Features.Shell;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;

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
        PlayerBarViewModel playerBar)
    {
        Log.Info("MainWindowViewModel constructor started.");

        _services = services;
        PlayerBar = playerBar;

        var audio = services.GetRequiredService<AudioEngine>();
        var dialog = services.GetRequiredService<IDialogService>();

        // Навигация возможна только если не заблокирована
        var canNavigate = this.WhenAnyValue(x => x.IsNavigationLocked, locked => !locked);

        // FIX: ThrownExceptions subscription to prevent memory leak
        NavigateCommand = CreateCommand(ReactiveCommand.Create<string>(pageName =>
        {
            if (!IsNavigationLocked)
            {
                Navigate(pageName);
            }
        }, canNavigate));

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

        // 1. Сохраняем ссылку на старую страницу
        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        // 2. Создаём и показываем новую страницу СРАЗУ
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

        // 3. ОТЛОЖЕННО диспозим старую страницу (после завершения render pass)
        if (oldPage is IDisposable disposable)
        {
            _ = DisposePageDelayedAsync(disposable, oldPageName);
        }
    }

    private async Task DisposePageDelayedAsync(IDisposable page, string pageName)
    {
        // Ждём 2 render frames (~32ms при 60fps) чтобы UI точно отвязался
        await Task.Delay(50);

        try
        {
            page.Dispose();
            Log.Debug($"[Navigation] Disposed old page: {pageName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Navigation] Error disposing {pageName}: {ex.Message}");
        }

        // Cleanup после dispose
        _services.GetRequiredService<TrackViewModelFactory>().CleanupCache();
        _services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();

        // Компактификация только при уходе с тяжёлых страниц
        if (pageName is "Search" or "Library" or "Home")
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
    }

    public void NavigateToPlaylist(string playlistId)
    {
        if (IsNavigationLocked) return;

        Log.Info($"Navigating to Playlist: {playlistId}");

        var oldPage = CurrentPage;

        // Создаём и загружаем плейлист
        _ = Task.Run(async () =>
        {
            var playlistVM = _services.GetRequiredService<PlaylistViewModel>();
            await playlistVM.LoadPlaylistAsync(playlistId);

            // Переключаемся на UI потоке
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentPage = playlistVM;
                CurrentPageName = "Playlist";
            });

            // Отложенный dispose
            if (oldPage is IDisposable disposable)
            {
                await DisposePageDelayedAsync(disposable, "Previous");
            }
        });
    }
}