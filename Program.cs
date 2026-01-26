
using LMP.Core.Services;
using LMP.Features.Home;
using LMP.Features.Library;
using LMP.Features.Player;
using LMP.Features.Playlist;
using LMP.Features.Search;
using LMP.Features.Settings;
using LMP.Features.Shell;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;

namespace LMP;

class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        try
        {
            Console.WriteLine("Logger initializing...");
            Log.Initialize();

            Log.Info("LiteMusicPlayer starting...!");

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal($"Global crash: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Info("Configuring services...");

        // --- Core Services ---
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<CookieAuthService>();
        services.AddSingleton<YoutubeProvider>();
        services.AddSingleton<YoutubeUserDataService>();
        services.AddSingleton<MusicLibraryManager>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();

        // --- Caching ---
        services.AddSingleton<StreamCacheManager>();
        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();
        services.AddSingleton<MemoryMonitor>();

        // --- Audio & Downloads ---
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        // --- ViewModels ---
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<QueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<MergeConflictViewModel>();
        services.AddTransient<SyncSelectionViewModel>();
        services.AddSingleton<TrackViewModelFactory>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        Log.Info("Services registered successfully.");
    }
}


