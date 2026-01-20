// ViewModels/SearchViewModel.cs
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive;

namespace MyLiteMusicPlayer.ViewModels;

public class SearchViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;

    private string _currentQuery = "";

    protected override int BatchSize => 20;

    [Reactive] public string SearchQuery { get; set; } = string.Empty;
    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ObservableCollection<TrackItemViewModel> Results => Items;

    public SearchViewModel(
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _audio = audio;
        _library = library;
        _downloads = downloads;

        var canSearch = this.WhenAnyValue(x => x.SearchQuery, q => !string.IsNullOrWhiteSpace(q));
        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteSearchAsync, canSearch);
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        if (_library.HasTrack(track.Id))
        {
            var existing = _library.GetTrack(track.Id);
            if (existing != null)
            {
                track.IsLiked = existing.IsLiked;
                track.IsDownloaded = existing.IsDownloaded;
            }
        }

        return new TrackItemViewModel(track, _audio, _library, _downloads, PlayTrackWithContext);
    }

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentQuery)) return [];

        var sw = Stopwatch.StartNew();
        var newTracks = await _youtube.SearchAsync(_currentQuery, TotalCount + 50);
        var result = newTracks.Skip(TotalCount).ToList();

        Log.Info($"Fetched {result.Count} more in {sw.ElapsedMilliseconds}ms");

        if (result.Count > 0)
        {
            var allTracks = AllItems.Concat(result).ToList();
            _ = _searchCache.SetAsync(_currentQuery, allTracks);
        }

        return result;
    }

    private async Task ExecuteSearchAsync()
    {
        CancelLoading();
        IsLoading = true;
        ErrorMessage = null;
        ClearItems();

        _currentQuery = SearchQuery.Trim();
        var sw = Stopwatch.StartNew();

        try
        {
            var queryType = _youtube.DetectQueryType(_currentQuery);
            List<TrackInfo> tracks;

            if (queryType == QueryType.DirectUrl)
            {
                var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
                tracks = track != null ? [track] : [];
                await InitializeItemsAsync(tracks, canFetchMore: false);
            }
            else if (queryType == QueryType.Playlist)
            {
                IsFetchingFromNetwork = true;
                var playlist = await _youtube.GetPlaylistAsync(_currentQuery);
                tracks = playlist?.Tracks ?? [];
                IsFetchingFromNetwork = false;
                await InitializeItemsAsync(tracks, canFetchMore: false);
            }
            else
            {
                // Проверяем кэш
                var cached = await _searchCache.GetAsync(_currentQuery, 20);

                if (cached != null && cached.Count > 0)
                {
                    tracks = cached;
                    Log.Info($"From cache: {cached.Count}");
                }
                else
                {
                    IsFetchingFromNetwork = true;
                    tracks = await _youtube.SearchAsync(_currentQuery, 100);
                    IsFetchingFromNetwork = false;

                    if (tracks.Count > 0)
                    {
                        _ = _searchCache.SetAsync(_currentQuery, tracks);
                    }
                }

                // Предзагрузка изображений
                var imageUrls = tracks.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!);

                await InitializeItemsAsync(tracks, canFetchMore: true);
            }

            HasResults = tracks.Count > 0;
            if (!HasResults)
            {
                ErrorMessage = L["Search_NoResults"];
            }

            Log.Info($"'{_currentQuery}': {tracks.Count} results in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Log.Info($"Error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            IsFetchingFromNetwork = false;
        }
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        _audio.ClearQueue();
        _ = _audio.PlayTrackAsync(track);

        bool found = false;
        foreach (var item in Items)
        {
            if (found) _audio.Enqueue(item.Track);
            if (item.Track.Id == track.Id) found = true;
        }
    }

    public void Dispose()
    {
        CancelLoading();
    }
}