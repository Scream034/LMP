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

    // ═══ VERSION INFO ═══

    /// <summary>
    /// Видимость версии. По умолчанию true — показываем сразу.
    /// Пользователь может скрыть кликом на лого.
    /// </summary>
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

    /// <summary>
    /// Текущая страница. Пустая строка при старте — первый Navigate не блокируется.
    /// </summary>
    [Reactive] public string CurrentPageName { get; private set; } = "";

    [Reactive] public bool IsNavigationLocked { get; private set; }
    [Reactive] public string NavigationLockReason { get; private set; } = "";

    // ═══ NOTIFICATIONS ═══
    [Reactive] public NotificationButtonViewModel NotificationButton { get; private set; }
    [Reactive] public NotificationPanelViewModel NotificationPanel { get; private set; }
    [Reactive] public ToastOverlayViewModel ToastOverlay { get; private set; }

    /// <summary>
    /// Задержка перед запуском тяжёлой инициализации страницы (ms).
    /// Даёт время CrossFade-анимации (150ms) отработать без фризов.
    /// Страница рендерит лёгкий скелетон/каркас за это время.
    /// </summary>
    private const int DeferredLoadDelayMs = 180;

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

        UpdateCommitsDisplay();

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            UpdateCommitsDisplay();
            this.RaisePropertyChanged(nameof(L));
        };

        // Toggle версии — клик на лого
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
        try { await operation(); }
        finally { UnlockNavigation(); }
    }

    public async Task<T> WithNavigationLockAsync<T>(string reason, Func<Task<T>> operation)
    {
        LockNavigation(reason);
        try { return await operation(); }
        finally { UnlockNavigation(); }
    }

    /// <summary>
    /// Навигация с отложенной инициализацией (Deferred Loading).
    /// 
    /// Порядок действий:
    /// 1. Создаём ViewModel через DI (конструктор ЛЁГКИЙ — только DI, команды, свойства).
    /// 2. Устанавливаем CurrentPage → TransitioningContentControl начинает CrossFade (150ms).
    /// 3. Страница рендерит пустой каркас/скелетон (IsContentReady=false).
    /// 4. Через DeferredLoadDelayMs (180ms) вызываем OnNavigatedToAsync() → тяжёлая загрузка.
    /// 5. По завершении загрузки страница переключает скелетон на реальный контент.
    /// 
    /// Результат: анимация всегда плавная, независимо от тяжести страницы.
    /// </summary>
    private void Navigate(string pageName)
    {
        if (IsNavigationLocked) return;

        if (CurrentPageName == pageName)
        {
            Log.Debug($"[Navigation] Already on '{pageName}', skipping");
            return;
        }

        var sw = Stopwatch.StartNew();
        Log.Info($"Switching to page: {pageName}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        // Создаём ViewModel — конструктор должен быть лёгким (без загрузки данных)
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

        // Устанавливаем страницу СРАЗУ — CrossFade начинается, страница рендерит скелетон
        CurrentPage = newPage;
        CurrentPageName = pageName;

        sw.Stop();
        Log.Info($"Page '{pageName}' VM created in {sw.ElapsedMilliseconds}ms, scheduling deferred init...");

        // Отложенная инициализация: даём CrossFade отработать, затем загружаем данные
        _ = DeferredInitAsync(newPage, pageName);

        // Dispose старой страницы с задержкой (чтобы CrossFade доиграл)
        if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
        {
            _ = DisposePageDelayedAsync(disposable, oldPageName);
        }
    }

    /// <summary>
    /// Ждёт завершения CrossFade-анимации, затем запускает тяжёлую инициализацию страницы.
    /// Если пользователь уже переключился на другую страницу — пропускаем.
    /// </summary>
    private async Task DeferredInitAsync(ViewModelBase page, string pageName)
    {
        try
        {
            // Ждём пока CrossFade-анимация отработает
            await Task.Delay(DeferredLoadDelayMs);

            // Проверяем что страница всё ещё актуальна (пользователь мог переключиться)
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

    public void NavigateToPlaylist(string playlistId)
    {
        if (IsNavigationLocked) return;

        Log.Info($"Navigating to Playlist: {playlistId}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        _ = Task.Run(async () =>
        {
            var playlistVM = _services.GetRequiredService<PlaylistViewModel>();
            await playlistVM.LoadPlaylistAsync(playlistId);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentPage = playlistVM;
                CurrentPageName = "Playlist";
            });

            if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
            {
                await DisposePageDelayedAsync(disposable, oldPageName);
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