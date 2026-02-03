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

        audio.OnCriticalError += (title, msg) =>
        {
            // Гарантируем, что диалог не заблокирует поток обработки аудио
            RxApp.MainThreadScheduler.Schedule(async () =>
            {
                await dialog.ShowInfoAsync(title, msg);
            });
        };

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

        // При смене страницы чистим мертвые VM из фабрики
        Program.Services.GetRequiredService<TrackViewModelFactory>().CleanupCache();

        // И чистим мертвые ссылки в реестре треков
        Program.Services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();

        // Если уходим с тяжелой страницы (Search/Library), форсируем очистку
        if (CurrentPageName == "Search" || CurrentPageName == "Library")
        {
            // Очищаем кэш VM, так как при возврате мы все равно пересоздадим список
            Program.Services.GetRequiredService<TrackViewModelFactory>().Clear();

            // Компактификация LOH (убирает дыры от JSON строк и буферов)
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced);
        }

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

        _ = Task.Run(async () =>
        {
            var playlistVM = _services.GetRequiredService<PlaylistViewModel>();
            await playlistVM.LoadPlaylistAsync(playlistId);
            CurrentPage = playlistVM;
            CurrentPageName = "Playlist";
        });
    }
}