using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.ViewModels;
using System;
using System.Diagnostics;

namespace MyLiteMusicPlayer;

class Program
{
    /// <summary>
    /// Глобальный провайдер сервисов (Dependency Injection)
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        try
        {
            Debug.WriteLine("[LIFECYCLE] App starting...");

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CRITICAL] Global crash: {ex.Message}\n{ex.StackTrace}");
            // Здесь можно добавить запись в файл лога
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    /// <summary>
    /// Конфигурация внедрения зависимостей
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        Debug.WriteLine("[DI] Configuring services...");

        // Singleton Services (Один экземпляр на всё приложение)
        services.AddSingleton<GoogleAuthService>();
        services.AddSingleton<YoutubeProvider>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        // Transient (создаются заново при каждом вызове для очистки состояния)
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<SettingsViewModel>();

        Debug.WriteLine("[DI] Services registered.");
    }
}