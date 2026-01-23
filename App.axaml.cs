using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Features.Shell;
using MyLiteMusicPlayer.Core.Services;
using AsyncImageLoader;

namespace MyLiteMusicPlayer;

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
            // 1. Инициализируем локализацию ДО создания UI
            var library = Program.Services.GetRequiredService<LibraryService>();
            LocalizationService.Instance.Initialize(library.Data.LanguageCode);

            // 2. Теперь создаём UI
            var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowVM
            };

            Log.Info("Main window created and shown.");

            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);

            // 3. Фоновая инициализация
            _ = Task.Run(async () =>
            {
                try
                {
                    Log.Info("Starting background services initialization...");
                    var youtube = Program.Services.GetRequiredService<YoutubeProvider>();
                    await youtube.InitializeAsync();
                    Log.Info("YouTube provider is ready.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Background initialization failed: {ex.Message}");
                }
            });

            // 4. Фоновая инициализация лайкнутых треков
            _ = Task.Run(async () =>
            {
                try
                {
                    Log.Info("Starting liked tracks sync...");
                    var musicLibraryManager = Program.Services.GetRequiredService<MusicLibraryManager>();
                    await musicLibraryManager.SyncLikedTracksAsync();
                    Log.Info("Liked tracks sync completed.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Liked tracks sync failed: {ex.Message}");
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}


