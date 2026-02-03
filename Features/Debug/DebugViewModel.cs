using System.Reactive;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Microsoft.Extensions.DependencyInjection;
using LMP.Features.Shared;

namespace LMP.Features.Debug;

public sealed class DebugViewModel : ViewModelBase, IDisposable
{
    private readonly YoutubeProvider _youtube;

    [Reactive] public string LogOutput { get; set; } = "Debug Session Started...\n";
    [Reactive] public string SearchQuery { get; set; } = "Linkin Park";
    [Reactive] public bool IsBusy { get; set; }

    public ReactiveCommand<Unit, Unit> GetLikedVideosCommand { get; }
    public ReactiveCommand<Unit, Unit> GetLikedMusicCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchVideosCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchMusicCommand { get; }
    public ReactiveCommand<Unit, string> ClearLogCommand { get; }

    public ReactiveCommand<Unit, Unit> DumpMemoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceGcCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCachesCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckVmLeaksCommand { get; }

    public DebugViewModel()
    {
        _youtube = Program.Services.GetRequiredService<YoutubeProvider>();

        GetLikedVideosCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteGetLikedVideos));
        GetLikedMusicCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteGetLikedMusic));
        SearchVideosCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteSearchVideos));
        SearchMusicCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteSearchMusic));
        ClearLogCommand = CreateCommand(ReactiveCommand.Create(() => LogOutput = ""));

        // Memory commands
        DumpMemoryCommand = CreateCommand(ReactiveCommand.Create(ExecuteDumpMemory));
        ForceGcCommand = CreateCommand(ReactiveCommand.Create(ExecuteForceGc));
        ClearCachesCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteClearCaches));
        
        CheckVmLeaksCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();

            // Через reflection
            var cacheField = vmFactory.GetType().GetField("_cache",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);


            if (cacheField?.GetValue(vmFactory) is System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> cache)
            {
                int alive = 0;
                int dead = 0;
                int disposed = 0;

                foreach (var kvp in cache)
                {
                    if (kvp.Value.TryGetTarget(out var vm))
                    {
                        if (vm.IsDisposed) disposed++;
                        else alive++;
                    }
                    else
                    {
                        dead++;
                    }
                }

                AppendLog($"\n--- TRACK VM CACHE ---");
                AppendLog($"Total entries: {cache.Count}");
                AppendLog($"Alive (not disposed): {alive}");
                AppendLog($"Disposed but in cache: {disposed}");
                AppendLog($"Dead (collected): {dead}");
                AppendLog($"--- END ---\n");
            }
        }));
    }

    private void ExecuteDumpMemory()
    {
        var report = MemoryDiagnostics.Instance.GetFullReport();
        AppendLog(report);

        // Также логируем в файл
        MemoryDiagnostics.LogReport();
    }

    private void ExecuteForceGc()
    {
        AppendLog("\n--- FORCING GARBAGE COLLECTION ---");

        var before = GC.GetTotalMemory(false) / 1024 / 1024;
        AppendLog($"Before: {before} MB");

        MemoryDiagnostics.ForceCleanup();

        var after = GC.GetTotalMemory(true) / 1024 / 1024;
        AppendLog($"After:  {after} MB");
        AppendLog($"Freed:  {before - after} MB");
        AppendLog("--- GC COMPLETE ---\n");
    }

    private async Task ExecuteClearCaches()
    {
        AppendLog("\n--- CLEARING ALL CACHES ---");
        IsBusy = true;

        try
        {
            // Image cache
            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            imageCache.ClearMemoryCache();
            AppendLog("✓ Image memory cache cleared");

            // Search cache
            var searchCache = Program.Services.GetRequiredService<SearchCacheService>();
            searchCache.ClearAll();
            AppendLog("✓ Search cache cleared");

            // YouTube stream cache
            _youtube.ClearCache();
            AppendLog("✓ YouTube stream URL cache cleared");

            // TrackViewModelFactory
            var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();
            var cleaned = vmFactory.CleanupCache();
            AppendLog($"✓ TrackVM cache: cleaned {cleaned} dead refs");

            // Force GC after clearing
            MemoryDiagnostics.ForceCleanup();
            AppendLog("✓ GC completed");

            // Show new memory state
            var stats = MemoryDiagnostics.Instance.CurrentStats;
            AppendLog($"\nCurrent memory: {stats.WorkingSetMb} MB (GC: {stats.GcTotalMemoryMb} MB)");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            AppendLog("--- CACHES CLEARED ---\n");
        }
    }

    private async Task ExecuteGetLikedVideos()
    {
        await RunSafe("YT LIKED (LL)", async () =>
        {
            var videos = await _youtube.GetClient().Playlists
                .GetVideosAsync(new Core.Youtube.Playlists.PlaylistId("LL"))
                .Take(10)
                .ToListAsync();
            return videos;
        });
    }

    private async Task ExecuteGetLikedMusic()
    {
        await RunSafe("YTM LIKED (VLLM)", async () =>
        {
            var tracks = await _youtube.GetClient().Music.GetLikedTracksAsync();
            return tracks.Take(10).ToList();
        });
    }

    private async Task ExecuteSearchVideos()
    {
        await RunSafe($"YT SEARCH: {SearchQuery}", async () =>
        {
            return await _youtube.SearchFastAsync(SearchQuery, 10, SearchFilter.Video);
        });
    }

    private async Task ExecuteSearchMusic()
    {
        await RunSafe($"YTM SEARCH: {SearchQuery}", async () =>
        {
            return await _youtube.SearchFastAsync(SearchQuery, 10, SearchFilter.Music);
        });
    }

    private async Task RunSafe(string title, Func<Task<List<TrackInfo>>> action)
    {
        IsBusy = true;
        AppendLog($"\n--- STARTING: {title} ---");
        try
        {
            var results = await action();
            AppendLog($"Success! Found {results.Count} items:");
            foreach (var item in results)
            {
                AppendLog($"- [{item.Id}] {item.Title} by {item.Author} (Music: {item.IsMusic})");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            if (ex.InnerException != null) AppendLog($"INNER: {ex.InnerException.Message}");
        }
        finally
        {
            AppendLog($"--- FINISHED: {title} ---\n");
            IsBusy = false;
        }
    }

    private void AppendLog(string text)
    {
        LogOutput += text + "\n";
    }
}