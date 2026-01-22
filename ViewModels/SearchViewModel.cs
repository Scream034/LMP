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
    private readonly TrackViewModelFactory _vmFactory;

    private string _currentQuery = "";
    private CancellationTokenSource? _searchCts;

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
        TrackViewModelFactory vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _audio = audio;
        _vmFactory = vmFactory;

        // Восстановление истории
        if (LibService.Data.SearchHistory != null)
        {
            foreach (var item in LibService.Data.SearchHistory)
                RecentSearches.Add(item);
        }

        // Восстановление последнего запроса
        if (!string.IsNullOrEmpty(LibService.Data.LastSearchQuery))
        {
            SearchQuery = LibService.Data.LastSearchQuery;
            _ = ExecuteSearchAsync();
        }

        var canSearch = this.WhenAnyValue(x => x.SearchQuery, q => !string.IsNullOrWhiteSpace(q));
        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteSearchAsync, canSearch);

        HistoryClickCommand = ReactiveCommand.Create<string>(query =>
        {
            if (string.IsNullOrEmpty(query)) return;
            SearchQuery = query;
            _ = ExecuteSearchAsync();
        });

        RemoveHistoryCommand = ReactiveCommand.Create<string>(query =>
        {
            if (string.IsNullOrEmpty(query)) return;
            RecentSearches.Remove(query);
            UpdateHistoryStorage();
        });
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        if (LibService.HasTrack(track.Id))
        {
            var existing = LibService.GetTrack(track.Id);
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
        var newTracks = await _youtube.SearchAsync(_currentQuery, TotalCount);
        
        if (ct.IsCancellationRequested) return [];
        
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
        // Отмена предыдущего поиска
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        CancelLoading();
        IsLoading = true;
        ErrorMessage = null;
        ClearItems();
        HasResults = false;

        _currentQuery = SearchQuery.Trim();

        // Сохраняем запрос и историю
        LibService.Data.LastSearchQuery = _currentQuery;
        AddToHistory(_currentQuery);
        LibService.Save();

        try
        {
            // Небольшой дебаунс для быстрых кликов
            await Task.Delay(50, ct);

            var queryType = YoutubeProvider.DetectQueryType(_currentQuery);
            List<TrackInfo> tracks;

            if (queryType == QueryType.DirectUrl)
            {
                var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
                
                if (ct.IsCancellationRequested) return;
                
                tracks = track != null ? [track] : [];
                await InitializeItemsAsync(tracks, canFetchMore: false);

                if (track != null && LibService.Data.AutoPlayOnUrlPaste)
                {
                    _ = _audio.PlayTrackAsync(track);
                }
            }
            else if (queryType == QueryType.Playlist)
            {
                IsFetchingFromNetwork = true;
                var playlist = await _youtube.GetPlaylistAsync(_currentQuery);
                
                if (ct.IsCancellationRequested) return;
                
                tracks = playlist?.Tracks ?? [];
                IsFetchingFromNetwork = false;
                await InitializeItemsAsync(tracks, canFetchMore: false);
            }
            else
            {
                var cached = await _searchCache.GetAsync(_currentQuery, 20);
                
                if (ct.IsCancellationRequested) return;
                
                if (cached != null && cached.Count > 0)
                {
                    tracks = cached;
                }
                else
                {
                    IsFetchingFromNetwork = true;
                    tracks = await _youtube.SearchAsync(_currentQuery, 100);
                    
                    if (ct.IsCancellationRequested) return;
                    
                    IsFetchingFromNetwork = false;
                    if (tracks.Count > 0) _ = _searchCache.SetAsync(_currentQuery, tracks);
                }

                var imageUrls = tracks.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                
                if (ct.IsCancellationRequested) return;
                
                await InitializeItemsAsync(tracks, canFetchMore: true);
            }

            if (ct.IsCancellationRequested) return;

            HasResults = tracks.Count > 0;
            if (!HasResults) ErrorMessage = L["Search_NoResults"];
        }
        catch (OperationCanceledException)
        {
            // Поиск был отменен - это нормально
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
                IsFetchingFromNetwork = false;
            }
        }
    }

    private void AddToHistory(string query)
    {
        RecentSearches.Remove(query);

        RecentSearches.Insert(0, query);

        while (RecentSearches.Count > 10)
            RecentSearches.RemoveAt(RecentSearches.Count - 1);

        UpdateHistoryStorage();
    }

    private void UpdateHistoryStorage()
    {
        LibService.Data.SearchHistory = [.. RecentSearches];
        LibService.Save();
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        // Атомарно устанавливаем очередь и начинаем воспроизведение
        var tracks = Items.Select(x => x.Track).ToList();
        _ = _audio.StartQueueAsync(tracks, track);
        LibService.AddToRecentlyPlayed(track);
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        CancelLoading();
        GC.SuppressFinalize(this);
    }
}