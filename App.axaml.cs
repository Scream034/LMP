using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using LMP.Features.Shell;
using LMP.Core.Services;
using AsyncImageLoader;
using LMP.Core.Audio;
using LMP.Core.Audio.Cache;

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
            // 0. Load theme BEFORE any UI is created
            var themeManager = Program.Services.GetRequiredService<ThemeManagerService>();
            themeManager.LoadAndApplyThemeOnStartup();

            // 1. Get library service
            var library = Program.Services.GetRequiredService<LibraryService>();

            // 2. Initialize localization with default
            LocalizationService.Instance.Initialize("en");

            // 3. Initialize GLOBAL audio cache
            var audioCacheManager = Program.Services.GetRequiredService<AudioCacheManager>();
            AudioSourceFactory.InitializeGlobalCache(audioCacheManager);

            // 4. Start Memory Monitor
            MemoryDiagnostics.Instance.OnMemoryWarning += Log.Warn;
            MemoryDiagnostics.Instance.WarningThresholdMb = 400;
            MemoryDiagnostics.Instance.CriticalThresholdMb = 450;

            // 5. Create UI
            var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowVM
            };

            Log.Info("Main window created and shown.");

            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);

            // 6. Initialize services IN BACKGROUND
            _ = InitializeServicesAsync(library);

            // 7. Cleanup on shutdown
            desktop.ShutdownRequested += async (_, e) =>
            {
                try
                {
                    MemoryDiagnostics.LogReport();
                    MemoryDiagnostics.Instance.Dispose();

                    // Dispose audio cache
                    await audioCacheManager.DisposeAsync();

                    await library.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"Shutdown cleanup error: {ex.Message}");
                }
            };

#if DEBUG
            desktop.MainWindow.AttachDevTools();

            desktop.MainWindow.KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.F9)
                {
                    new Features.Debug.DebugWindow().Show();
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
            // Initialize database (this can take time on first run)
            await library.InitializeAsync();

            // Update localization with saved language
            var savedLang = library.Settings.LanguageCode;
            if (!string.IsNullOrEmpty(savedLang) && savedLang != "en")
            {
                Log.Info($"Using saved language: {savedLang}");
                LocalizationService.Instance.CurrentLanguage = savedLang;
            }

            // Initialize YouTube
            var youtube = Program.Services.GetRequiredService<YoutubeProvider>();
            await youtube.InitializeAsync();

            // Sync liked tracks if authenticated
            var musicLibraryManager = Program.Services.GetRequiredService<MusicLibraryManager>();

#if DEBUG
            var dialogService = Program.Services.GetRequiredService<IDialogService>();
            var canSync = await dialogService.ConfirmAsync("Debug", "Sync liked tracks?", "Yes", "No");
            if (canSync) await musicLibraryManager.SyncLikedTracksAsync();
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