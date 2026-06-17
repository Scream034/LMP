using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using LMP.UI.Features.Home;
using LMP.UI.Features.Library;
using LMP.UI.Features.Player;
using LMP.UI.Features.Playlist;
using LMP.UI.Features.Search;
using LMP.UI.Features.Settings;
using LMP.UI.Features.Shell;
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
using LMP.UI.Features.Notifications;
using ReactiveUI.Avalonia;
using LMP.UI.Features.Queue;
using LMP.Core.Data.Entities;
using LMP.Core.Models;

namespace LMP;

/// <summary>
/// Точка входа в приложение Lite Music Player.
/// </summary>
public sealed class AppEntry
{
    /// <summary>
    /// Глобальный провайдер служб внедрения зависимостей.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        SetupGlobalExceptionHandlers();

        try
        {
            G.Folder.Create();

            Log.Initialize();
            Log.Info($"{G.AppId} starting...");

            BootstrapSettings.Initialize();

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            LifecycleRegistry.Instance = Services.GetRequiredService<LifecycleRegistry>();
            LifecycleRegistry.ActiveUiPageResolver = () =>
                Services.GetService<MainWindowViewModel>()?.CurrentPage;

            // Безопасная инициализация и автоматическая миграция БД
            MigrateDatabaseSync();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal($"Global crash: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    /// <summary>
    /// Выполняет инициализацию базы данных. При обнаружении старой версии схемы 
    /// осуществляет её резервное копирование и разворачивает чистую БД.
    /// </summary>
    private static void MigrateDatabaseSync()
    {
        var dbPath = G.FilePath.Database;
        bool needsRecreation = false;

        if (File.Exists(dbPath))
        {
            try
            {
                // Ограничиваем область видимости DbContext, чтобы он гарантированно закрылся
                using (var ctx = Services.GetRequiredService<IDbContextFactory<LibraryDbContext>>().CreateDbContext())
                {
                    int dbVersion = ctx.GetDatabaseVersionAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (dbVersion < DatabaseExtensions.CurrentDbVersion)
                    {
                        Log.Info($"[DB] Outdated database version detected (Current: {dbVersion}, Required: {DatabaseExtensions.CurrentDbVersion}). Requiring clean recreation...");
                        needsRecreation = true;
                    }
                }

                // КРИТИЧЕСКИЙ ПАТЧ: Освобождаем пулы соединений SQLite, снимая блокировку с файла БД
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
            catch (Exception ex)
            {
                Log.Warn($"[DB] Error while verifying database schema version: {ex.Message}. Recreating to be safe...");
                needsRecreation = true;

                // Сбрасываем пулы в случае исключения
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        if (needsRecreation)
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    // Принудительно собираем мусор, чтобы закрыть невыгруженные файловые дескрипторы
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    var backupPath = dbPath + $".backup.{DateTime.Now:yyyyMMddHHmmss}";
                    File.Move(dbPath, backupPath, overwrite: true);
                    Log.Info($"[DB] Old database successfully backed up to: {backupPath}");
                }

                // Сбрасываем сессию, чтобы гарантировать чистый вход в аккаунт на новой БД
                var auth = Services.GetRequiredService<CookieAuthService>();
                auth.Logout();
                Log.Info("[DB] Authorization cleared to prevent state conflicts.");
            }
            catch (Exception backupEx)
            {
                Log.Error($"[DB] Failed to backup legacy database or clear cookies: {backupEx.Message}");
            }
        }

        try
        {
            TryInitializeDatabaseInternal();

            if (needsRecreation)
            {
                SaveEmergencyNotification();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DB] Critical database initialization/migration error: {ex.Message}");

            try
            {
                // В случае критического сбоя пробуем очистить пулы еще раз и повторить процедуру
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();

                if (File.Exists(dbPath))
                {
                    var backupPath = dbPath + $".backup.{DateTime.Now:yyyyMMddHHmmss}";
                    File.Move(dbPath, backupPath, overwrite: true);
                    Log.Info($"[DB] Current incompatible database successfully backed up to: {backupPath}");

                    TryInitializeDatabaseInternal();
                    SaveEmergencyNotification();
                }
            }
            catch (Exception backupEx)
            {
                Log.Fatal($"[DB] Failed to recover database with a clean slate: {backupEx.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Выполняет стандартные процедуры развертывания структуры SQLite с проставлением версии.
    /// </summary>
    private static void TryInitializeDatabaseInternal()
    {
        var dbFactory = Services.GetRequiredService<IDbContextFactory<LibraryDbContext>>();
        using var ctx = dbFactory.CreateDbContext();

        ctx.Database.EnsureCreated();
        ctx.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
        ctx.EnsureFtsTablesAsync(CancellationToken.None).GetAwaiter().GetResult();
        ctx.SetDatabaseVersionAsync(DatabaseExtensions.CurrentDbVersion, CancellationToken.None).GetAwaiter().GetResult();

        Log.Info($"[DB] Database initialization/migration complete (Version: {DatabaseExtensions.CurrentDbVersion})");
    }

    /// <summary>
    /// Записывает локализованное уведомление о сбросе базы данных с использованием JSON-ключей
    /// </summary>
    private static void SaveEmergencyNotification()
    {
        try
        {
            var dbFactory = Services.GetRequiredService<IDbContextFactory<LibraryDbContext>>();
            using var ctx = dbFactory.CreateDbContext();

            var notification = new NotificationEntity
            {
                Id = Guid.NewGuid().ToString(),
                TitleKey = "Dialog_Warning_Title", // "Предупреждение" / "Warning"
                MessageKey = "Auth_ProfileLoadError_Message", // Сообщение об ошибке профиля/БД
                RecommendationKey = "Recommendation_ContactDev", // "Обратитесь к разработчику"
                Severity = (int)NotificationSeverity.Warning,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            };

            ctx.Notifications.Add(notification);
            ctx.SaveChanges();
            Log.Info("[DB] Emergency recovery notification saved to database using localization keys");
        }
        catch (Exception ex)
        {
            Log.Warn($"[DB] Failed to save emergency notification: {ex.Message}");
        }
    }

    private static void SetupGlobalExceptionHandlers()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            try
            {
                var msg = e.Exception?.InnerException?.Message
                       ?? e.Exception?.Message
                       ?? "unknown";
                Log.Debug($"[UnobservedTask] Suppressed: {msg}");
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
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
            catch { }
        };
    }

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
    {
        BootstrapSettings.Initialize();
        var gpuCacheBytes = BootstrapSettings.Current.GpuTextureCacheMb * 1024L * 1024L;

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = gpuCacheBytes
            })
#if DEBUG
            .LogToTrace()
#endif
            .UseReactiveUI(_ => { });
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Info("Configuring services...");

        services.AddSingleton(_ => BootstrapSettings.Current);

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

        services.AddSingleton<ITrackRepository, TrackRepository>();
        services.AddSingleton<IPlaylistRepository, PlaylistRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<INotificationRepository, NotificationRepository>();

        services.AddSingleton(sp =>
        {
            var trackRepo = sp.GetRequiredService<ITrackRepository>();
            var playlistRepo = sp.GetRequiredService<IPlaylistRepository>();
            var auth = sp.GetRequiredService<CookieAuthService>();
            return new TrackRegistry(trackRepo, playlistRepo, auth);
        });

        services.AddSingleton<IAsyncImageLoader>(sp =>
            new CachedImageLoader(sp.GetRequiredService<ImageCacheService>(), ImageQuality.Low));

        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<CookieAuthService>();
        services.AddSingleton<LocalAuthServer>();
        services.AddSingleton<YoutubeProvider>();
        services.AddTransient(sp => new Lazy<YoutubeProvider>(sp.GetRequiredService<YoutubeProvider>));
        services.AddSingleton<YoutubeUserDataService>();
        services.AddSingleton<MusicLibraryManager>();

        services.AddSingleton<DialogHostViewModel>();

        services.AddSingleton(sp =>
        {
            var auth = sp.GetRequiredService<CookieAuthService>();
            var userData = sp.GetRequiredService<YoutubeUserDataService>();
            var localServer = sp.GetRequiredService<LocalAuthServer>();

            DialogHostViewModel GetDialogHost()
            {
                var mainWindow = sp.GetRequiredService<MainWindowViewModel>();
                return mainWindow.DialogHost;
            }

            return new DialogService(auth, userData, localServer, GetDialogHost);
        });

        services.AddSingleton(_ => new PlayerContextManager(SharedHttpClient.Instance));
        services.AddSingleton<SigCipherDecryptor>();
        services.AddSingleton<NTokenDecryptor>();

        services.AddSingleton<PlaylistSyncService>();
        services.AddSingleton<PlaylistEditService>();

        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();

        services.AddSingleton<AudioCacheManager>();
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        services.AddSingleton<NotificationService>();
        services.AddTransient<NotificationButtonViewModel>();
        services.AddTransient<NotificationPanelViewModel>();
        services.AddTransient<ToastOverlayViewModel>();

        services.AddSingleton<PlaybackErrorOrchestrator>();

        services.AddSingleton<DominantColorService>();
        services.AddSingleton<PlayerControlService>();

        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<QueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<SyncSelectionViewModel>();

        services.AddSingleton<LifecycleRegistry>();
        services.AddSingleton<TrackViewModelFactory>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        Log.Info("Services registered.");
    }
}