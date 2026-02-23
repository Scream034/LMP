using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using LMP.Features.Shell;
using LMP.Core.Services;
using AsyncImageLoader;
using LMP.Core.Audio;
using LMP.Core.Audio.Cache;

#if DEBUG
using LMP.Tests;
#endif

namespace LMP;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Info("Framework initialization completed.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 0. Load theme
            var themeManager = Program.Services.GetRequiredService<ThemeManagerService>();
            themeManager.LoadAndApplyThemeOnStartup();

            // 1. Get library service
            var library = Program.Services.GetRequiredService<LibraryService>();

            // 2. Initialize localization
            LocalizationService.Instance.Initialize("en");

            // 3. Initialize audio cache
            var audioCacheManager = Program.Services.GetRequiredService<AudioCacheManager>();
            AudioSourceFactory.InitializeGlobalCache(audioCacheManager);

            // 4. Memory monitor
            MemoryDiagnostics.Instance.OnMemoryWarning += Log.Warn;
            MemoryDiagnostics.Instance.WarningThresholdMb = 400;
            MemoryDiagnostics.Instance.CriticalThresholdMb = 450;

            // 5. Create UI
            var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowVM
            };

            Log.Info("Main window created.");

            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);

            // 6. Background initialization
            _ = InitializeServicesAsync(library);

            // 7. Cleanup on shutdown
            desktop.ShutdownRequested += async (_, e) =>
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
                    Log.Error($"Shutdown cleanup error: {ex.Message}");
                }
            };

#if DEBUG
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000); // Даём время на инициализацию
                    
                    // ═══════════════════════════════════════════════════════
                    // ВЫБЕРИ ОДИН ИЗ ВАРИАНТОВ:
                    // ═══════════════════════════════════════════════════════
                    
                    // 1. Полный test suite
                    // await ManualTests.RunAllAsync();
                    
                    // 2. Только N-Token (самый важный)
                    await ManualTests.TestNTokenQuickAsync();
                    
                    // 3. Только Sig Cipher
                    await ManualTests.TestSigCipherQuickAsync();
                    
                    // 4. Полный pipeline для конкретного видео
                    // await ManualTests.TestSigCipherFullAsync("dQw4w9WgXcQ");
                    
                    // 5. Benchmark
                    // await LMP.Tests.Unit.NTokenTests.BenchmarkAsync(Program.Services);
                }
                catch (Exception ex)
                {
                    Log.Error($"Tests failed: {ex.Message}");
                }
            });

            desktop.MainWindow.AttachDevTools();

            desktop.MainWindow.KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.F9)
                {
                    new Features.Debug.DebugWindow().Show();
                }

                // F10 — запустить тесты вручную
                if (e.Key == Avalonia.Input.Key.F10)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ManualTests.RunAllAsync();
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Manual test error: {ex.Message}");
                        }
                    });
                }
            };
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeServicesAsync(LibraryService library)
    {
        try
        {
            await library.InitializeAsync();

            var savedLang = library.Settings.LanguageCode;
            if (!string.IsNullOrEmpty(savedLang) && savedLang != "en")
            {
                Log.Info($"Using saved language: {savedLang}");
                LocalizationService.Instance.CurrentLanguage = savedLang;
            }

            var youtube = Program.Services.GetRequiredService<YoutubeProvider>();
            await youtube.InitializeAsync();

            var musicLibraryManager = Program.Services.GetRequiredService<MusicLibraryManager>();

#if DEBUG
            var dialogService = Program.Services.GetRequiredService<IDialogService>();
            var canSync = await dialogService.ConfirmAsync("Debug", "Sync liked tracks?", "Yes", "No");
            if (canSync)
                await musicLibraryManager.SyncLikedTracksAsync();
#else
            await musicLibraryManager.SyncLikedTracksAsync();
#endif
        }
        catch (Exception ex)
        {
            Log.Error($"Background initialization failed: {ex.Message}");
        }
    }
}