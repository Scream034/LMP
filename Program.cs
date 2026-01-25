
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Features.Home;
using MyLiteMusicPlayer.Features.Library;
using MyLiteMusicPlayer.Features.Player;
using MyLiteMusicPlayer.Features.Playlist;
using MyLiteMusicPlayer.Features.Search;
using MyLiteMusicPlayer.Features.Settings;
using MyLiteMusicPlayer.Features.Shell;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using YoutubeExplode;
using YoutubeExplode.Search;
using System.Diagnostics;

namespace MyLiteMusicPlayer;

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

            _ = TestSearch();
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
        services.AddSingleton<GoogleAuthService>();
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

   static async Task TestSearch()
    {
        var youtube = new YoutubeClient();
        string query = "Линия Фронта - Советская высадка в Нормандии! ★ В тылу врага: Штурм 2 ★ #447";

        Console.WriteLine($"--- ТЕСТ ПОИСКА: {query} ---\n");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            Console.WriteLine("[ФИЛЬТР: MUSIC (Песни)]");
            // Берем первый батч результатов
            var musicBatch = await youtube.Search.GetResultBatchesAsync(query, SearchFilter.Music).FirstOrDefaultAsync();
            if (musicBatch != null && musicBatch.Items.Any())
            {
                foreach (var item in musicBatch.Items.Take(5))
                {
                    if (item is VideoSearchResult video)
                        Console.WriteLine($"> ПЕСНЯ: {video.Title} | Автор: {video.Author.ChannelTitle} | Short: {video.IsShort}");
                }
            }
            else
            {
                Console.WriteLine("> Результатов по фильтру 'Музыка' не найдено.");
            }

            // 2. Поиск видео
            Console.WriteLine("\n[ФИЛЬТР: VIDEO (Обычные видео)]");
            var videoResults = await youtube.Search.GetVideosAsync(query).Take(5).ToListAsync();
            foreach (var v in videoResults)
                Console.WriteLine($"> ВИДЕО: {v.Title} | Длительность: {v.Duration} | URL: {v.Url}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nОШИБКА: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            stopwatch.Stop();
            Console.WriteLine($"\nТест завершен за: {stopwatch.ElapsedMilliseconds} мс.");
            Console.ReadKey();
        }
    }
}


