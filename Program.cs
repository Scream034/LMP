using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.ViewModels;
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
        // Установка кодировки для корректного вывода в консоль (важно для yt-dlp)
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
            // В продакшн-коде здесь стоит добавить запись в файл лога
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    /// <summary>
    /// Конфигурация внедрения зависимостей.
    /// Здесь мы регистрируем все сервисы и модели представления.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        Debug.WriteLine("[DI] Configuring services...");

        // --- Core Services (Singleton - один экземпляр на всё приложение) ---
        services.AddSingleton<LibraryService>();
        services.AddSingleton<GoogleAuthService>();
        services.AddSingleton<YoutubeProvider>();
        services.AddSingleton<IDialogService, Dia
        logService>();

        // --- Fast search & caching ---
        services.AddSingleton<PipedProvider>();        // Быстрый поиск через Piped
        services.AddSingleton<SearchCacheService>();   // Кэширование результатов поиска на диск
        services.AddSingleton<ImageCacheService>();    // Кэширование обложек (память + диск)
        services.AddSingleton<MemoryMonitor>();        // Мониторинг потребления ОЗУ

        // Регистрация пула yt-dlp для ускорения получения ссылок (был пропущен)
        // Мы берем путь к yt-dlp из того же места, что и YoutubeProvider
        services.AddSingleton<YtDlpPool>(sp =>
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string ytdlpPath = System.IO.Path.Combine(appData, "LiteMusicPlayer", "Bin", "yt-dlp.exe");
            return new YtDlpPool(ytdlpPath, maxConcurrent: 3);
        });

        // --- Audio & Downloads ---
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        // --- ViewModels (Навигационные страницы - Transient, создаются при каждом переходе) ---
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<SettingsViewModel>();

        // ИСПРАВЛЕНО: Регистрация PlaylistViewModel была пропущена
        services.AddTransient<PlaylistViewModel>();

        // --- Main ViewModels (Глобальные элементы - Singleton) ---
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        Debug.WriteLine("[DI] Services registered successfully.");
    }
}