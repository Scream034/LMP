using System.Reactive;
using System.Reactive.Linq;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Diagnostics;
using LMP.UI.Features.Player;
using LMP.Core.ViewModels;
using LMP.UI.Features.Search;
using LMP.UI.Features.Home;
using LMP.UI.Features.Library;
using LMP.UI.Features.Settings;
using LMP.UI.Features.Playlist;
using LMP.UI.Features.Notifications;
using System.Runtime;
using LMP.Core.Models;

namespace LMP.UI.Features.Shell;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;

    // ═══ VERSION INFO ═══
    [Reactive] public bool IsVersionInfoVisible { get; set; } = true;
    public static string VersionDisplay => G.Build.DisplayVersion;
    public static string GitHashDisplay => G.Build.GitHash;

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
    [Reactive] public string CurrentPageName { get; private set; } = "";
    [Reactive] public bool IsNavigationLocked { get; private set; }
    [Reactive] public string NavigationLockReason { get; private set; } = "";

    // ═══ NOTIFICATIONS ═══
    [Reactive] public NotificationButtonViewModel NotificationButton { get; private set; }
    [Reactive] public NotificationPanelViewModel NotificationPanel { get; private set; }
    [Reactive] public ToastOverlayViewModel ToastOverlay { get; private set; }

    // ═══ DIALOG HOST ═══
    /// <summary>
    /// Контейнер для overlay-диалогов (над контентом, под TopBar).
    /// </summary>
    [Reactive] public DialogHostViewModel DialogHost { get; private set; }

    private const int DeferredLoadDelayMs = 180;

    public ReactiveCommand<string, Unit> NavigateCommand { get; }

    public MainWindowViewModel(
        IServiceProvider services,
        PlayerBarViewModel playerBar,
        NotificationButtonViewModel notificationButton,
        NotificationPanelViewModel notificationPanel,
        ToastOverlayViewModel toastOverlay,
        DialogHostViewModel dialogHost) // ← Добавить параметр
    {
        Log.Info("MainWindowViewModel constructor started.");

        _services = services;
        PlayerBar = playerBar;
        NotificationButton = notificationButton;
        NotificationPanel = notificationPanel;
        ToastOverlay = toastOverlay;
        DialogHost = dialogHost; // ← Сохранить

        UpdateCommitsDisplay();

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            UpdateCommitsDisplay();
            this.RaisePropertyChanged(nameof(L));
        };

        ToggleVersionInfoCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsVersionInfoVisible = !IsVersionInfoVisible;
        }));

        OpenGitHubCommand = CreateCommand(ReactiveCommand.Create(OpenGitHub));

        // Навигация заблокирована если IsNavigationLocked ИЛИ есть активный диалог
        var canNavigate = this.WhenAnyValue(
            x => x.IsNavigationLocked,
            x => x.DialogHost.HasActiveDialog,
            (locked, hasDialog) => !locked && !hasDialog);

        NavigateCommand = CreateCommand(ReactiveCommand.Create<string>(pageName =>
        {
            if (!IsNavigationLocked && !DialogHost.HasActiveDialog)
            {
                Navigate(pageName);
            }
        }, canNavigate));

        Navigate("Home");

        _ = ValidateAuthOnStartupAsync();

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
        try { await operation(); }
        finally { UnlockNavigation(); }
    }

    public async Task<T> WithNavigationLockAsync<T>(string reason, Func<Task<T>> operation)
    {
        LockNavigation(reason);
        try { return await operation(); }
        finally { UnlockNavigation(); }
    }

    private void Navigate(string pageName)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        if (CurrentPageName == pageName)
        {
            Log.Debug($"[Navigation] Already on '{pageName}', skipping");
            return;
        }

        var sw = Stopwatch.StartNew();
        Log.Info($"Switching to page: {pageName}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        ViewModelBase? newPage = pageName switch
        {
            "Home" => _services.GetRequiredService<HomeViewModel>(),
            "Search" => _services.GetRequiredService<SearchViewModel>(),
            "Library" => _services.GetRequiredService<LibraryViewModel>(),
            "Settings" => _services.GetRequiredService<SettingsViewModel>(),
            "Queue" => _services.GetRequiredService<QueueViewModel>(),
            _ => null
        };

        if (newPage == null)
        {
            Log.Warn($"[Navigation] Unknown page: {pageName}");
            return;
        }

        // Принудительно разрываем ссылку TCC на старый контент.
        // TransitioningContentControl хранит _lastPresenter с предыдущим визуальным
        // деревом — если CrossFade не очистил его, старый View живёт вечно.
        // null → newPage создаёт "пустой" переход, после которого _lastPresenter
        // содержит null-контент, а не SettingsView с 200+ контролами.
        CurrentPage = null;
        CurrentPage = newPage;
        CurrentPageName = pageName;

        sw.Stop();
        Log.Info($"Page '{pageName}' VM created in {sw.ElapsedMilliseconds}ms, scheduling deferred init...");

        _ = DeferredInitAsync(newPage, pageName);

        if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
        {
            _ = DisposePageDelayedAsync(disposable, oldPageName);
        }
    }

    private async Task DeferredInitAsync(ViewModelBase page, string pageName)
    {
        try
        {
            await Task.Delay(DeferredLoadDelayMs);

            if (CurrentPage != page)
            {
                Log.Debug($"[Navigation] Page '{pageName}' is no longer current, skipping deferred init");
                return;
            }

            var sw = Stopwatch.StartNew();
            await page.OnNavigatedToAsync();
            sw.Stop();

            Log.Info($"[Navigation] Deferred init for '{pageName}' completed in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Error($"[Navigation] Deferred init failed for '{pageName}': {ex.Message}");
        }
    }

    private async Task DisposePageDelayedAsync(IDisposable page, string pageName)
    {
        await Task.Delay(200);

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

    /// <summary>
    /// Навигация к плейлисту.
    ///
    /// <para><b>FIX диспозал независим от загрузки:</b> старая страница
    /// диспозится через фиксированный <see cref="DisposePageDelayedAsync"/> delay,
    /// не дожидаясь завершения LoadPlaylistAsync новой.
    /// Это устраняет накопление zombie PlaylistVM при быстрых кликах:
    /// каждая пред. страница диспозится через ~200ms после навигации,
    /// независимо от того, сколько грузится новая.</para>
    /// </summary>
    public void NavigateToPlaylist(string playlistId)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        Log.Info($"Navigating to Playlist: {playlistId}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        var playlistVM = _services.GetRequiredService<PlaylistViewModel>();

        // Разрыв ссылки _lastPresenter (см. комментарий в Navigate)
        CurrentPage = null;
        CurrentPage = playlistVM;
        CurrentPageName = "Playlist";

        if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
        {
            _ = DisposePageDelayedAsync(disposable, oldPageName);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await playlistVM.LoadPlaylistAsync(playlistId);
            }
            catch (Exception ex)
            {
                Log.Error($"[Navigation] Playlist load failed: {ex.Message}");
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

    private async Task ValidateAuthOnStartupAsync()
    {
        try
        {
            await Task.Delay(2000);

            var auth = _services.GetRequiredService<CookieAuthService>();

            if (auth.HasProfileLoadError)
            {
                Log.Warn("[Auth] Profile load error detected on startup");

                if (auth.IsAuthenticated)
                {
                    var notifications = _services.GetRequiredService<NotificationService>();
                    await notifications.ShowToastAsync(
                        "Auth_ProfileLoadError_Title",
                        "Auth_ProfileLoadError_Message",
                        NotificationSeverity.Warning,
                        durationMs: 6000);
                }
                return;
            }

            if (!auth.IsAuthenticated) return;

            var (isValid, error) = await auth.ValidateSessionAsync();

            if (!isValid)
            {
                Log.Warn($"[Auth] Session expired on startup: {error}");

                var notifications = _services.GetRequiredService<NotificationService>();
                await notifications.ShowToastAsync(
                    "Auth_SessionExpired_Title",
                    "Auth_SessionExpired_Message",
                    NotificationSeverity.Warning,
                    durationMs: 8000);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Startup validation error: {ex.Message}");
        }
    }
}