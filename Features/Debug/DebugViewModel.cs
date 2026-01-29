using System.Collections.ObjectModel;
using System.Reactive;
using System.Text;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Features.Debug;

public class DebugViewModel : ViewModelBase
{
    private readonly YoutubeProvider _youtube;
    private readonly YoutubeUserDataService _ytUser;

    [Reactive] public string LogOutput { get; set; } = "Debug Session Started...\n";
    [Reactive] public string SearchQuery { get; set; } = "Linkin Park";
    [Reactive] public bool IsBusy { get; set; }

    public ReactiveCommand<Unit, Unit> GetLikedVideosCommand { get; }
    public ReactiveCommand<Unit, Unit> GetLikedMusicCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchVideosCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchMusicCommand { get; }
    public ReactiveCommand<Unit, string> ClearLogCommand { get; }

    public DebugViewModel()
    {
        _youtube = Program.Services.GetRequiredService<YoutubeProvider>();
        _ytUser = Program.Services.GetRequiredService<YoutubeUserDataService>();

        GetLikedVideosCommand = ReactiveCommand.CreateFromTask(ExecuteGetLikedVideos);
        GetLikedMusicCommand = ReactiveCommand.CreateFromTask(ExecuteGetLikedMusic);
        SearchVideosCommand = ReactiveCommand.CreateFromTask(ExecuteSearchVideos);
        SearchMusicCommand = ReactiveCommand.CreateFromTask(ExecuteSearchMusic);
        ClearLogCommand = ReactiveCommand.Create(() => LogOutput = "");
    }

    private async Task ExecuteGetLikedVideos()
    {
        await RunSafe("YT LIKED (LL)", async () => {
            // Используем прямой клиент плейлистов для "LL" (Liked Videos)
            var videos = await _youtube.GetClient().Playlists
                .GetVideosAsync(new Core.Youtube.Playlists.PlaylistId("LL"))
                .Take(10)
                .ToListAsync();
            return videos;
        });
    }

    private async Task ExecuteGetLikedMusic()
    {
        await RunSafe("YTM LIKED (VLLM)", async () => {
            // Используем Music API для получения лайков
            var tracks = await _youtube.GetClient().Music.GetLikedTracksAsync();
            return tracks.Take(10).ToList();
        });
    }

    private async Task ExecuteSearchVideos()
    {
        await RunSafe($"YT SEARCH: {SearchQuery}", async () => {
            return await _youtube.SearchFastAsync(SearchQuery, 10, SearchFilter.Video);
        });
    }

    private async Task ExecuteSearchMusic()
    {
        await RunSafe($"YTM SEARCH: {SearchQuery}", async () => {
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