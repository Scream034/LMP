using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using LMP.UI.Features.Shell;
using AsyncImageLoader;
using LMP.Core.Audio.Cache;
using System.Diagnostics;


#if DEBUG
using LMP.Tests;
using LMP.Core.Diagnostics;
#endif

namespace LMP;

public partial class App : Application
{
    private SplashWindow? _splash;

#if DEBUG
    private static UIHangWatchdog? _uiWatchdog;
#endif

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Info("Framework initialization completed.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            // Безопасный запуск отладчика после того, как Dispatcher UI-потока гарантированно инициализирован
            try
            {
                _uiWatchdog = new UIHangWatchdog();
                _uiWatchdog.Start();

                desktop.ShutdownRequested += (_, _) =>
                {
                    _uiWatchdog?.Dispose();
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"[Watchdog] Failed to initialize diagnostic watchdog: {ex.Message}");
            }
#endif


            // ═══ ЭТАП 1: Тема (мгновенно) ═══
            var themeManager = AppEntry.Services.GetRequiredService<ThemeManagerService>();
            themeManager.LoadAndApplyThemeOnStartup();

            // ═══ ЭТАП 2: Локализация (мгновенно) ═══
            var bootstrap = AppEntry.Services.GetRequiredService<BootstrapSettings>();
            LocalizationService.Instance.Initialize(bootstrap.LanguageCode);
            Log.Info($"Localization: {bootstrap.LanguageCode}");

            // ═══ ЭТАП 3: Splash Screen ═══
            _splash = new SplashWindow();
            desktop.MainWindow = _splash;
            _splash.Show();

            // ═══ КРИТИЧНО: Даём UI-потоку отрисовать splash ═══
            Dispatcher.UIThread.Post(() =>
            {
                _ = InitializeAppAsync(desktop);
            }, DispatcherPriority.Background);
        }

#if !WINDOWS
        // Этот код вообще не попадет в сборку под Windows
        InitializeTrayIcon();
#endif

        base.OnFrameworkInitializationCompleted();
    }

#if !WINDOWS
    private void InitializeTrayIcon()
    {
        var trayIcon = new TrayIcon
        {
            // В Avalonia 11 иконки загружаются через AssetLoader
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LMP/Assets/app.ico"))),
            ToolTipText = LocalizationService.Instance["Common_AppName"],
            IsVisible = false
        };

        var trayIcons = new TrayIcons { trayIcon };
        TrayIcon.SetIcons(this, trayIcons);
    }
#endif

    private async Task InitializeAppAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var stopwatch = Stopwatch.StartNew();
        var L = LocalizationService.Instance;

        try
        {
            await Task.Delay(100);

            _splash?.SetProgress(5);
            _splash?.UpdateStatus(L["Splash_Initializing"]);

            // Audio Cache
            _splash?.UpdateStatus(L["Splash_InitAudioCache"]);
            var audioCacheManager = await Task.Run(() =>
                AppEntry.Services.GetRequiredService<AudioCacheManager>());
            AudioSourceFactory.InitializeGlobalCache(audioCacheManager);
            _splash?.SetProgress(20);

            // Memory Monitor
            MemoryCleanupHelper.StartAutoCleanup();
            Log.Info("[Startup] Memory auto-cleanup scheduled");
            _splash?.SetProgress(25);

            // Library Service
            _splash?.UpdateStatus(L["Splash_LoadingLibrary"]);
            var library = AppEntry.Services.GetRequiredService<LibraryService>();
            await Task.Run(async () => await library.InitializeAsync());
            _splash?.SetProgress(45);

            // AudioEngine был создан до InitializeAsync с дефолтными Settings.
            // Теперь Settings загружены из DB — перечитываем.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var audio = AppEntry.Services.GetRequiredService<AudioEngine>();
                audio.ShuffleEnabled = library.Settings.ShuffleEnabled;
                audio.RepeatMode = library.Settings.RepeatMode;

                var playerControl = AppEntry.Services.GetRequiredService<PlayerControlService>();
                playerControl.ForceSync(); // уже существующий метод — синхронизирует все subjects из AudioEngine
            });

            // ═══ СИНХРОНИЗАЦИЯ ЯЗЫКА ═══
            var savedLang = library.Settings.LanguageCode;
            var currentLang = L.CurrentLanguageCode;

            if (!string.IsNullOrEmpty(savedLang) && savedLang != currentLang)
            {
                if (savedLang == "en" && currentLang != "en")
                {
                    Log.Info($"First run: saving auto-detected '{currentLang}' to DB (was default 'en')");
                    library.Settings.LanguageCode = currentLang;
                }
                else if (savedLang != "en")
                {
                    Log.Info($"Applying saved language from DB: {savedLang}");
                    L.CurrentLanguage = savedLang;
                }
            }
            _splash?.SetProgress(50);

            // ═══ КРИТИЧНО: NotificationService после LibraryService ═══
            var notifications = AppEntry.Services.GetRequiredService<NotificationService>();
            await notifications.InitializeAsync();
            Log.Info("[Startup] NotificationService history loaded");
            _splash?.SetProgress(55);

            // ═══ PlaybackErrorOrchestrator после NotificationService ═══
            var orchestrator = AppEntry.Services.GetRequiredService<PlaybackErrorOrchestrator>();
            Log.Info("[Startup] PlaybackErrorOrchestrator ready");
            _splash?.SetProgress(60);

            // Image Cache
            _splash?.UpdateStatus(L["Splash_PreparingImages"]);
            var imageCache = await Task.Run(() =>
                AppEntry.Services.GetRequiredService<ImageCacheService>());

            // Создаём loader только один раз, не дублируем.
            // Старый loader из splash (если был установлен) диспозим.
            var oldLoader = ImageLoader.AsyncImageLoader;
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);
            if (oldLoader is IDisposable disposableLoader)
                disposableLoader.Dispose();

            _splash?.SetProgress(70);

            // YouTube Provider
            _splash?.UpdateStatus(L["Splash_ConnectingYouTube"]);
            var youtube = AppEntry.Services.GetRequiredService<Lazy<YoutubeProvider>>();

            // Запускаем инициализацию без await, чтобы не блокировать Splash Screen (особенно при DPI-блокировках)
            _ = Task.Run(async () =>
            {
                try
                {
                    await youtube.Value.InitializeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[YouTube] Background init failed: {ex.Message}");
                }
            });

            _splash?.SetProgress(80);

            // Create Main Window
            _splash?.UpdateStatus(L["Splash_BuildingInterface"]);
            MainWindow? mainWindow = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var mainWindowVM = AppEntry.Services.GetRequiredService<MainWindowViewModel>();
                mainWindow = new MainWindow { DataContext = mainWindowVM };
            });
            _splash?.SetProgress(93);

            // Ready!
            _splash?.UpdateStatus(L["Splash_Ready"]);
            _splash?.SetProgress(100);

            // ═══ МИНИМАЛЬНОЕ ВРЕМЯ ПОКАЗА ═══
            var elapsed = stopwatch.ElapsedMilliseconds;
            var minTime = G.Build.MinSplashTimeMs;
            var remaining = minTime - (int)elapsed;

            if (remaining > 0)
            {
                Log.Info($"[Splash] Waiting additional {remaining}ms");
                await Task.Delay(remaining);
            }

            // Switch Windows
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                desktop.MainWindow = mainWindow;
                mainWindow?.Show();
                _splash?.Close();
                _splash = null;
            });

            Log.Info($"Main window ready. Total splash time: {stopwatch.ElapsedMilliseconds}ms");

            // Post-Startup GC Compaction
            // Сборка мусора с уплотнением кучи после завершения тяжелой фазы инициализации приложения.
            // Возвращает неиспользуемую память (Gen 0/1/2) операционной системе.
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500); // Даем время Skia и UI-потоку полностью завершить отрисовку
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Log.Info("[Memory] Post-startup GC compaction complete. Cold memory reclaimed.");
            });

            // Shutdown Handler
            desktop.ShutdownRequested += async (_, _) =>
            {
                try
                {
                    MemoryCleanupHelper.Dispose();
                    await audioCacheManager.DisposeAsync();
                    await library.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"Shutdown error: {ex.Message}");
                }
            };

#if DEBUG
            // Avalonia 12: AttachDeveloperTools — extension на Application, не Window.
            // Открывается F12 по умолчанию (настраивается через DeveloperToolsOptions).
            this.AttachDeveloperTools();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow?.KeyDown += (s, e) =>
                    {
                        if (e.Key == Avalonia.Input.Key.F9)
                            new UI.Features.Debug.DebugWindow().Show();

                        if (e.Key == Avalonia.Input.Key.F10)
                            _ = Task.Run(ManualTests.RunAllAsync);
                    };
            });
#endif
        }
        catch (Exception ex)
        {
            Log.Fatal($"Initialization failed: {ex.Message}\n{ex.StackTrace}");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _splash?.UpdateStatus(string.Format(L["Splash_Error"], ex.Message));
            });

            await Task.Delay(3000);
        }
    }
}