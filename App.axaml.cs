using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using LMP.Features.Shell;
using LMP.Core.Services;
using LMP.Core.Models;
using AsyncImageLoader;
using LMP.Core.Audio;
using LMP.Core.Audio.Cache;
using System.Diagnostics;

#if DEBUG
using LMP.Tests;
#endif

namespace LMP;

public partial class App : Application
{
    private SplashWindow? _splash;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Info("Framework initialization completed.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ═══ ЭТАП 1: Тема (мгновенно) ═══
            var themeManager = Program.Services.GetRequiredService<ThemeManagerService>();
            themeManager.LoadAndApplyThemeOnStartup();

            // ═══ ЭТАП 2: Локализация (мгновенно) ═══
            var bootstrap = Program.Services.GetRequiredService<BootstrapSettings>();
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

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAppAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var stopwatch = Stopwatch.StartNew();
        var L = LocalizationService.Instance;

        try
        {
            await Task.Delay(100);

            _splash?.SetProgress(5);
            _splash?.UpdateStatus(L["Splash_Initializing"]);

            // ─── Audio Cache ───
            _splash?.UpdateStatus(L["Splash_InitAudioCache"]);
            var audioCacheManager = await Task.Run(() =>
                Program.Services.GetRequiredService<AudioCacheManager>());
            AudioSourceFactory.InitializeGlobalCache(audioCacheManager);
            _splash?.SetProgress(20);

            // ─── Memory Monitor ───
            MemoryDiagnostics.Instance.OnMemoryWarning += Log.Warn;
            MemoryDiagnostics.Instance.WarningThresholdMb = 400;
            MemoryDiagnostics.Instance.CriticalThresholdMb = 450;
            _splash?.SetProgress(25);

            // ─── Library Service ───
            _splash?.UpdateStatus(L["Splash_LoadingLibrary"]);
            var library = Program.Services.GetRequiredService<LibraryService>();
            await Task.Run(async () => await library.InitializeAsync());
            _splash?.SetProgress(45);

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
            var notifications = Program.Services.GetRequiredService<NotificationService>();
            await notifications.InitializeAsync();
            Log.Info("[Startup] NotificationService history loaded");
            _splash?.SetProgress(55);

            // ═══ PlaybackErrorOrchestrator после NotificationService ═══
            var orchestrator = Program.Services.GetRequiredService<PlaybackErrorOrchestrator>();
            Log.Info("[Startup] PlaybackErrorOrchestrator ready");
            _splash?.SetProgress(60);

            // ─── Image Cache ───
            _splash?.UpdateStatus(L["Splash_PreparingImages"]);
            var imageCache = await Task.Run(() =>
                Program.Services.GetRequiredService<ImageCacheService>());
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);
            _splash?.SetProgress(70);

            // ─── YouTube Provider ───
            _splash?.UpdateStatus(L["Splash_ConnectingYouTube"]);
            var youtube = Program.Services.GetRequiredService<YoutubeProvider>();
            await Task.Run(async () => await youtube.InitializeAsync());
            _splash?.SetProgress(80);

            // ─── Create Main Window ───
            _splash?.UpdateStatus(L["Splash_BuildingInterface"]);
            MainWindow? mainWindow = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
                mainWindow = new MainWindow { DataContext = mainWindowVM };
            });
            _splash?.SetProgress(95);

            // ─── Ready! ───
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

            // ─── Switch Windows ───
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                desktop.MainWindow = mainWindow;
                mainWindow?.Show();
                _splash?.Close();
                _splash = null;
            });

            Log.Info($"Main window ready. Total splash time: {stopwatch.ElapsedMilliseconds}ms");

            // ─── Shutdown Handler ───
            desktop.ShutdownRequested += async (_, _) =>
            {
                try
                {
                    MemoryDiagnostics.LogReport();
                    MemoryDiagnostics.Instance.Dispose();
                    await audioCacheManager.DisposeAsync();
                    await library.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"Shutdown error: {ex.Message}");
                }
            };

#if DEBUG
            desktop.MainWindow!.AttachDevTools();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow?.KeyDown += (s, e) =>
                    {
                        if (e.Key == Avalonia.Input.Key.F9)
                            new Features.Debug.DebugWindow().Show();

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