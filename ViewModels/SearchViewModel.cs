using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class SearchViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly TrackViewModelFactory _vmFactory;

    private string _currentQuery = "";

    protected override int BatchSize => 20;

    [Reactive] public string SearchQuery { get; set; } = string.Empty;
    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }

    public ObservableCollection<string> RecentSearches { get; } = [];

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<string, Unit> HistoryClickCommand { get; }
    public ReactiveCommand<string, Unit> RemoveHistoryCommand { get; }

    public SearchViewModel(
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        AudioEngine audio,
        LibraryService library,
        TrackViewModelFactory vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _audio = audio;
        _library = library;
        _vmFactory = vmFactory;

        // Восстановление истории
        if (_library.Data.SearchHistory != null)
        {
            foreach (var item in _library.Data.SearchHistory)
                RecentSearches.Add(item);
        }

        // Восстановление последнего запроса
        if (!string.IsNullOrEmpty(_library.Data.LastSearchQuery))
        {
            SearchQuery = _library.Data.LastSearchQuery;
            _ = ExecuteSearchAsync();
        }

        var canSearch = this.WhenAnyValue(x => x.SearchQuery, q => !string.IsNullOrWhiteSpace(q));
        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteSearchAsync, canSearch);

        HistoryClickCommand = ReactiveCommand.Create<string>(query =>
        {
            SearchQuery = query;
            _ = ExecuteSearchAsync();
        });

        RemoveHistoryCommand = ReactiveCommand.Create<string>(query =>
        {
            RecentSearches.Remove(query);
            UpdateHistoryStorage();
        });
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
        return _vmFactory.GetOrCreate(track, PlayTrackWithContext);
    }

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentQuery)) return [];
        var newTracks = await _youtube.SearchAsync(_currentQuery, TotalCount + 50);
        var result = newTracks.Skip(TotalCount).ToList();
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

        // Сохраняем запрос и историю
        _library.Data.LastSearchQuery = _currentQuery;
        AddToHistory(_currentQuery);
        _library.Save();

        try
        {
            var queryType = YoutubeProvider.DetectQueryType(_currentQuery);
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
                var cached = await _searchCache.GetAsync(_currentQuery, 20);
                if (cached != null && cached.Count > 0)
                {
                    tracks = cached;
                }
                else
                {
                    IsFetchingFromNetwork = true;
                    tracks = await _youtube.SearchAsync(_currentQuery, 100);
                    IsFetchingFromNetwork = false;
                    if (tracks.Count > 0) _ = _searchCache.SetAsync(_currentQuery, tracks);
                }

                var imageUrls = tracks.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!);
                await InitializeItemsAsync(tracks, canFetchMore: true);
            }

            HasResults = tracks.Count > 0;
            if (!HasResults) ErrorMessage = L["Search_NoResults"];
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            IsFetchingFromNetwork = false;
        }
    }

    private void AddToHistory(string query)
    {
        if (RecentSearches.Contains(query))
            RecentSearches.Remove(query);

        RecentSearches.Insert(0, query);

        while (RecentSearches.Count > 10)
            RecentSearches.RemoveAt(RecentSearches.Count - 1);

        UpdateHistoryStorage();
    }

    private void UpdateHistoryStorage()
    {
        _library.Data.SearchHistory = RecentSearches.ToList();
        _library.Save();
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        // Атомарно устанавливаем очередь и начинаем воспроизведение
        var tracks = Items.Select(x => x.Track).ToList();
        _ = _audio.StartQueueAsync(tracks, track);
        _library.AddToRecentlyPlayed(track);
    }

    public void Dispose()
    {
        CancelLoading();
        GC.SuppressFinalize(this);
    }
}