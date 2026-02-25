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
using LMP.Core.Audio.Cache;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Features.Notifications;
using LMP.Core.Models;

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
            // ═══ ЭТАП 1: Логгер (мгновенно) ═══
            Log.Initialize();
            Log.Info($"{G.AppId} starting...");

            // ═══ ЭТАП 2: Создать папки ═══
            G.Folder.Create();

            // ═══ ЭТАП 3: Bootstrap настройки (быстро, без БД) ═══
            BootstrapSettings.Initialize();

            // ═══ ЭТАП 4: DI контейнер ═══
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            // ═══ ЭТАП 5: Eager services ═══
            InitializeEagerServices();

            // ═══ ЭТАП 6: Avalonia ═══
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
#if DEBUG
            .LogToTrace()
#endif
            .UseReactiveUI();

    private static void InitializeEagerServices()
    {
        try
        {
            var orchestrator = Services.GetRequiredService<PlaybackErrorOrchestrator>();
            Log.Info("[Startup] PlaybackErrorOrchestrator ready");

            var notifications = Services.GetRequiredService<NotificationService>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await notifications.InitializeAsync();
                    Log.Info("[Startup] NotificationService history loaded");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Startup] NotificationService.InitializeAsync failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[Startup] Eager service initialization failed: {ex.Message}");
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Info("Configuring services...");

        // === Bootstrap Settings (уже загружены) ===
        services.AddSingleton(_ => BootstrapSettings.Current);

        // === Database ===
        var dbPath = G.FilePath.Database;
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
        services.AddSingleton<INotificationRepository, NotificationRepository>();

        // === Core Services ===
        services.AddSingleton(sp =>
        {
            var trackRepo = sp.GetRequiredService<ITrackRepository>();
            var playlistRepo = sp.GetRequiredService<IPlaylistRepository>();
            return new TrackRegistry(trackRepo, playlistRepo);
        });

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

        services.AddSingleton(_ => new PlayerContextManager(SharedHttpClient.Instance));
        services.AddSingleton<SigCipherDecryptor>();
        services.AddSingleton<NTokenDecryptor>();

        // === Caching ===
        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();

        // === Audio & Downloads ====
        services.AddSingleton<AudioCacheManager>();
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        // === NOTIFICATION SYSTEM ===
        services.AddSingleton<NotificationService>();
        services.AddTransient<NotificationButtonViewModel>();
        services.AddTransient<NotificationPanelViewModel>();
        services.AddTransient<ToastOverlayViewModel>();

        // === ERROR ORCHESTRATOR ===
        services.AddSingleton<PlaybackErrorOrchestrator>();

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

        Log.Info("Services registered.");
    }
}