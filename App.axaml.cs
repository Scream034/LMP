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

            // 1. Initialize localization
            var library = Program.Services.GetRequiredService<LibraryService>();
            LocalizationService.Instance.Initialize(library.Data.LanguageCode);

            // 2. Create UI
            var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowVM
            };
            Log.Info("Main window created and shown.");

            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);

            // 3. Background initialization tasks
            _ = Task.Run(static async () =>
            {
                try
                {
                    var youtube = Program.Services.GetRequiredService<YoutubeProvider>();
                    await youtube.InitializeAsync();
                    var musicLibraryManager = Program.Services.GetRequiredService<MusicLibraryManager>();
#if DEBUG
                    var dialogService = Program.Services.GetRequiredService<IDialogService>();
                    var canSync =await dialogService.ConfirmAsync("Debug", "Sync liked tracks?", "Yes", "No");
                    if (canSync) await musicLibraryManager.SyncLikedTracksAsync();
#else
                    await musicLibraryManager.SyncLikedTracksAsync();
#endif
                }
                catch (Exception ex)
                {
                    Log.Error($"Background initialization failed: {ex.Message}");
                }
            });

#if DEBUG
            desktop.MainWindow.AttachDevTools();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}