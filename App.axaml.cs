using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.ViewModels;
using MyLiteMusicPlayer.Views;
using MyLiteMusicPlayer.Services;
using System.Diagnostics;
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
        Debug.WriteLine("[LIFECYCLE] Framework initialization completed.");

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

            Debug.WriteLine("[UI] Main window created and shown.");

            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);

            // 3. Фоновая инициализация
            _ = Task.Run(async () =>
            {
                try
                {
                    Debug.WriteLine("[SERVICE] Starting background services initialization...");
                    var youtube = Program.Services.GetRequiredService<YoutubeProvider>();
                    await youtube.InitializeAsync();
                    Debug.WriteLine("[SERVICE] YouTube provider is ready.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Background initialization failed: {ex.Message}");
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}