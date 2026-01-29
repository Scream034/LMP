using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using LMP.Features.Shell;
using LMP.Core.Services;
using AsyncImageLoader;

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

            // 1. Get library service (don't await initialization here!)
            var library = Program.Services.GetRequiredService<LibraryService>();

            // 2. Initialize localization with default, will update after DB init
            LocalizationService.Instance.Initialize("en");

            // 3. Start Memory Monitor
            var memoryMonitor = Program.Services.GetRequiredService<MemoryMonitor>();
            memoryMonitor.OnMemoryWarning += Log.Warn;

            // 4. Create UI FIRST
            var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowVM
            };

            Log.Info("Main window created and shown.");

            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);

            // 5. Initialize library and other services IN BACKGROUND
            _ = InitializeServicesAsync(library);

            // 6. Cleanup on shutdown
            desktop.ShutdownRequested += async (_, e) =>
            {
                try
                {
                    await library.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"Shutdown cleanup error: {ex.Message}");
                }
            };

#if DEBUG
            desktop.MainWindow.AttachDevTools();
    
            // Debug window
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