using System.Reactive;
using System.Reactive.Linq;
using System.Windows.Input;
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
using LMP.Features.Notifications;
using System.Runtime;

namespace LMP.Features.Shell;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;

    // ═══ VERSION INFO (реактивные для смены языка) ═══
    private bool _isVersionInfoVisible;
    public bool IsVersionInfoVisible
    {
        get => _isVersionInfoVisible;
        set => this.RaiseAndSetIfChanged(ref _isVersionInfoVisible, value);
    }

    // Статические данные (не меняются)
    public static string VersionDisplay => G.Build.DisplayVersion;
    public static string GitHashDisplay => G.Build.GitHash;

    // Реактивное свойство для локализованного текста коммитов
    private string _commitsDisplay = "";
    public string CommitsDisplay
    {
        get => _commitsDisplay;
        private set => this.RaiseAndSetIfChanged(ref _commitsDisplay, value);
    }

    public ICommand ToggleVersionInfoCommand { get; }
    public ICommand OpenGitHubCommand { get; }

    // ═══ NAVIGATION ═══
    [Reactive] public ViewModelBase? CurrentPage { get; private set; }
    [Reactive] public PlayerBarViewModel PlayerBar { get; private set; }
    [Reactive] public string CurrentPageName { get; private set; } = "Home";

    [Reactive] public bool IsNavigationLocked { get; private set; }
    [Reactive] public string NavigationLockReason { get; private set; } = "";

    // ═══ NOTIFICATIONS ═══
    [Reactive] public NotificationButtonViewModel NotificationButton { get; private set; }
    [Reactive] public NotificationPanelViewModel NotificationPanel { get; private set; }
    [Reactive] public ToastOverlayViewModel ToastOverlay { get; private set; }

    public ReactiveCommand<string, Unit> NavigateCommand { get; }

    public MainWindowViewModel(
        IServiceProvider services,
        PlayerBarViewModel playerBar,
        NotificationButtonViewModel notificationButton,
        NotificationPanelViewModel notificationPanel,
        ToastOverlayViewModel toastOverlay)
    {
        Log.Info("MainWindowViewModel constructor started.");

        _services = services;
        PlayerBar = playerBar;
        NotificationButton = notificationButton;
        NotificationPanel = notificationPanel;
        ToastOverlay = toastOverlay;

        // ═══ ИНИЦИАЛИЗАЦИЯ ТЕКСТА КОММИТОВ ═══
        UpdateCommitsDisplay();

        // ═══ ПОДПИСКА НА СМЕНУ ЯЗЫКА ═══
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            UpdateCommitsDisplay();
            // Уведомляем UI об изменении L
            this.RaisePropertyChanged(nameof(L));
        };

        // ═══ КОМАНДА TOGGLE VERSION ═══
        ToggleVersionInfoCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsVersionInfoVisible = !IsVersionInfoVisible;
        }));

        OpenGitHubCommand = CreateCommand(ReactiveCommand.Create(OpenGitHub));

        var canNavigate = this.WhenAnyValue(x => x.IsNavigationLocked, locked => !locked);

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

    private void UpdateCommitsDisplay()
    {
        var L = LocalizationService.Instance;
        CommitsDisplay = $"({string.Format(L["Build_CommitsCount"], G.Build.CommitCount)})";
    }

    public void LockNavigation(string reason)
    {
        IsNavigationLocked = true;
        NavigationLockReason = reason;
        Log.Info($"[Navigation] Locked: {reason}");
    }

    public void UnlockNavigation()
    {
        IsNavigationLocked = false;
        NavigationLockReason = "";
        Log.Info("[Navigation] Unlocked");
    }

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

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

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

        if (oldPage is IDisposable disposable)
        {
            _ = DisposePageDelayedAsync(disposable, oldPageName);
        }
    }

    private async Task DisposePageDelayedAsync(IDisposable page, string pageName)
    {
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

        _services.GetRequiredService<TrackViewModelFactory>().CleanupCache();
        _services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();

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

        _ = Task.Run(async () =>
        {
            var playlistVM = _services.GetRequiredService<PlaylistViewModel>();
            await playlistVM.LoadPlaylistAsync(playlistId);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentPage = playlistVM;
                CurrentPageName = "Playlist";
            });

            if (oldPage is IDisposable disposable)
            {
                await DisposePageDelayedAsync(disposable, "Previous");
            }
        });
    }

    private static void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = G.GitHubUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open GitHub: {ex.Message}");
        }
    }
}