using LMP.Core.Data;
using LMP.Core.Data.Repositories;
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
using Microsoft.EntityFrameworkCore;
using Avalonia;
using AsyncImageLoader;
using LMP.UI.Dialogs;

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

            Log.Info($"{G.AppId} starting...!");

            G.Folder.Create();

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

#if HLS_FORMAT_TEST
            // Запуск теста HLS форматов
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000); // Подождать инициализации
                // await Core.Dev.HlsFormatTester.RunAllTestsAsync();
                // await Core.Dev.HlsFormatTester.TestAllStreamsAsync();
                // await Core.Dev.HlsFormatTester.TestIosRangeLimitAsync();
                // await Core.Dev.HlsFormatTester.TestIosOpusRangeLimitAsync();
                // await Core.Dev.HlsFormatTester.TestHeadersBypassAsync();
                await Core.Dev.HlsFormatTester.TestSignatureRefreshAsync();
                Console.WriteLine("\n\n");

                await Core.Dev.HlsFormatTester.TestCookieBypassAsync();
                Console.WriteLine("\n\n");

                await Core.Dev.HlsFormatTester.TestParallelConnectionsAsync();
            });
#endif

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

        // === Database ===
        // Configure DbContext with connection string here, not in DbContext
        var dbPath = G.File.Database;

        services.AddDbContextFactory<LibraryDbContext>(options =>
        {
            options.UseSqlite($"Data Source={dbPath};Cache=Shared");
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        // === Repositories ===
        services.AddSingleton<ITrackRepository, TrackRepository>();
        services.AddSingleton<IPlaylistRepository, PlaylistRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        // === Core Services ===
        services.AddSingleton(sp =>
        {
            var trackRepo = sp.GetRequiredService<ITrackRepository>();
            var playlistRepo = sp.GetRequiredService<IPlaylistRepository>();
            return new TrackRegistry(trackRepo, playlistRepo);
        });

        // Для обложек треков (маленькие)
        services.AddSingleton<IAsyncImageLoader>(sp =>
            new CachedImageLoader(sp.GetRequiredService<ImageCacheService>(), ImageQuality.Low));

        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<CookieAuthService>();
        services.AddSingleton<YoutubeProvider>();
        services.AddSingleton<YoutubeUserDataService>();
        services.AddSingleton<MusicLibraryManager>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();

        // === Caching ===
        services.AddSingleton<StreamCacheManager>();
        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();

        // === Audio & Downloads ===
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        // === ViewModels ===
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