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

        SetupGlobalExceptionHandlers();

        try
        {
            // ═══ ЭТАП 1: Создать папки ПЕРВЫМ ═══
            G.Folder.Create();

            // ═══ ЭТАП 2: Логгер ═══
            Log.Initialize();
            Log.Info($"{G.AppId} starting...");

            // ═══ ЭТАП 3: Bootstrap настройки (быстро, без БД) ═══
            BootstrapSettings.Initialize();

            // ═══ ЭТАП 4: DI контейнер ═══
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            // ═══ ЭТАП 5: КРИТИЧНО — Мигрировать БД ДО создания сервисов ═══
            MigrateDatabaseSync();

            // ═══ ЭТАП 6: Avalonia ═══
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal($"Global crash: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            // ═══ ГАРАНТИРОВАННЫЙ SHUTDOWN логгера ═══
            Log.Shutdown();
        }
    }

    /// <summary>
    /// Синхронно выполняет миграцию БД ДО старта Avalonia и создания сервисов.
    /// Гарантирует что NotificationService и LibraryService найдут готовую схему.
    /// </summary>
    private static void MigrateDatabaseSync()
    {
        try
        {
            var dbFactory = Services.GetRequiredService<IDbContextFactory<LibraryDbContext>>();
            using var ctx = dbFactory.CreateDbContext();

            ctx.Database.EnsureCreated();
            ctx.MigrateSchemaAsync(CancellationToken.None).GetAwaiter().GetResult();
            ctx.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
            ctx.EnsureFtsTablesAsync(CancellationToken.None).GetAwaiter().GetResult();

            Log.Info("[DB] Schema migration complete");
        }
        catch (Exception ex)
        {
            Log.Error($"[DB] Migration failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Регистрирует глобальные обработчики исключений.
    /// Должен вызываться ДО создания любых Task, HttpClient, etc.
    /// </summary>
    private static void SetupGlobalExceptionHandlers()
    {
        // 1. Unobserved Task Exceptions — подавляем ВСЕ, логируем когда можем.
        //    Это ловит exceptions из Task'ов которые не были await'нуты
        //    и собираются GC.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved(); // Предотвращает crash в любом случае

            try
            {
                var msg = e.Exception?.InnerException?.Message
                       ?? e.Exception?.Message
                       ?? "unknown";
                Log.Debug($"[UnobservedTask] Suppressed: {msg}");
            }
            catch
            {
                // Логгер ещё не инициализирован — молча подавляем
            }
        };

        // 2. Unhandled Exceptions на уровне AppDomain — 
        //    ловит исключения из IO completion threads, finalizer thread, etc.
        //    IsTerminating=true означает что CLR уже решил крашить процесс,
        //    но мы хотя бы залогируем.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    // SSL/TLS ошибки из SocketsHttpHandler — не фатальные
                    if (IsSslRelatedException(ex))
                    {
                        Log.Warn($"[AppDomain] SSL/TLS exception suppressed: {ex.Message}");
                        return;
                    }

                    Log.Error($"[AppDomain] Unhandled: {ex.Message}", ex);
                }
                else
                {
                    Log.Error($"[AppDomain] Unhandled non-exception: {e.ExceptionObject}");
                }
            }
            catch
            {
                // Логгер не готов — ничего не можем сделать
            }
        };
    }

    /// <summary>
    /// Проверяет, связано ли исключение с SSL/TLS (known .NET HTTP/2 issue).
    /// </summary>
    private static bool IsSslRelatedException(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            var typeName = current.GetType().FullName ?? "";
            var msg = current.Message ?? "";

            if (typeName.Contains("SslStream", StringComparison.Ordinal) ||
                typeName.Contains("Ssl", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Tls", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("secure channel", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("EnsureFullTlsFrame", StringComparison.Ordinal))
            {
                return true;
            }

            current = current.InnerException;
        }

        // Проверяем AggregateException
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                if (IsSslRelatedException(inner))
                    return true;
            }
        }

        return false;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 256 * 1024 * 1024  // 256 MB вместо дефолтных 28 MB
            })
#if DEBUG
            .LogToTrace()
#endif
            .UseReactiveUI();

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

        services.AddSingleton<DialogHostViewModel>();

        services.AddSingleton(sp =>
        {
            var auth = sp.GetRequiredService<CookieAuthService>();

            // Lazy accessor — избегаем циклической зависимости
            // MainWindowViewModel создаётся позже, чем DialogService
            DialogHostViewModel GetDialogHost()
            {
                var mainWindow = sp.GetRequiredService<MainWindowViewModel>();
                return mainWindow.DialogHost;
            }

            return new DialogService(auth, GetDialogHost);
        });

        services.AddSingleton(_ => new PlayerContextManager(SharedHttpClient.Instance));
        services.AddSingleton<SigCipherDecryptor>();
        services.AddSingleton<NTokenDecryptor>();

        services.AddSingleton<PlaylistSyncService>();
        services.AddSingleton<PlaylistEditService>();

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

        // === Other Services ===
        services.AddSingleton<DominantColorService>();
        services.AddSingleton<PlayerControlService>();

        // === ViewModels ===
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<QueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<SyncSelectionViewModel>();
        services.AddSingleton<TrackViewModelFactory>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        Log.Info("Services registered.");
    }
}