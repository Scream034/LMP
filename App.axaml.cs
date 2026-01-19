using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.ViewModels;
using MyLiteMusicPlayer.Views;
using MyLiteMusicPlayer.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Точка входа после инициализации Avalonia.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        Debug.WriteLine("[LIFECYCLE] Framework initialization completed.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 1. Создаем ViewModel и Окно немедленно
            var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowVM
            };

            Debug.WriteLine("[UI] Main window created and shown.");

            // 2. Запускаем тяжелую инициализацию в фоне
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
                    Debug.WriteLine($"[ERROR] Background initialization failed: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}