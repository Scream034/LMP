using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.Features.Search;

public class SearchViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly TrackViewModelFactory _vmFactory;

    private string _currentQuery = "";
    private CancellationTokenSource? _searchCts;
    private YoutubeProvider.SearchSession? _searchSession;
    private DateTime _lastSearchTime = DateTime.MinValue;
    private const int DebounceMs = 300;

    private int InitialBatchSize => LibService.Data.LoadBatchSize > 0 ? LibService.Data.LoadBatchSize * 2 : 50;
    private int ScrollBatchSize => LibService.Data.SearchBatchSize > 0 ? LibService.Data.SearchBatchSize : 30;
    private const int MaxResults = 300;

    [Reactive] public string SearchQuery { get; set; } = string.Empty;
    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }
    [Reactive] public bool IsFromCache { get; private set; }

    /// <summary>
    /// Показывать кнопку принудительного поиска
    /// </summary>
    public bool ShowForceSearchButton => LibService.Data.EnableSearchCache && IsFromCache && !IsLoading;

    public ObservableCollection<string> RecentSearches { get; } = [];

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceSearchCommand { get; }
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

        if (LibService.Data.SearchHistory != null)
        {
            foreach (var item in LibService.Data.SearchHistory)
                RecentSearches.Add(item);
        }

        var canSearch = this.WhenAnyValue(x => x.SearchQuery, x => x.IsLoading,
            (q, loading) => !string.IsNullOrWhiteSpace(q) && !loading);

        SearchCommand = ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: false),
            canSearch);

        var canForceSearch = this.WhenAnyValue(
            x => x.IsFromCache,
            x => x.IsLoading,
            (fromCache, loading) => fromCache && !loading);

        ForceSearchCommand = ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: true),
            canForceSearch);

        HistoryClickCommand = ReactiveCommand.CreateFromTask<string>(async query =>
        {
            if (string.IsNullOrEmpty(query)) return;

            SearchQuery = query;
            await ExecuteSearchAsync(forceNetwork: false);
        });

        RemoveHistoryCommand = ReactiveCommand.Create<string>(query =>
        {
            if (string.IsNullOrEmpty(query)) return;
            RecentSearches.Remove(query);
            UpdateHistoryStorage();
        });

        // Уведомление об изменении ShowForceSearchButton
        this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowForceSearchButton)));

        // Восстановление последнего запроса
        if (!string.IsNullOrEmpty(LibService.Data.LastSearchQuery))
        {
            SearchQuery = LibService.Data.LastSearchQuery;
            _ = ExecuteSearchAsync(forceNetwork: false);
        }
    }

    private bool CanExecuteSearch()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSearchTime).TotalMilliseconds < DebounceMs)
        {
            Log.Info($"[Search] Debounce: skipping (last search {(now - _lastSearchTime).TotalMilliseconds:F0}ms ago)");
            return false;
        }
        _lastSearchTime = now;
        return true;
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
        Log.Info($"[Search] FetchMoreFromNetworkAsync: total={TotalCount}, max={MaxResults}");

        if (TotalCount >= MaxResults)
        {
            Log.Info($"[Search] Reached max results limit ({MaxResults})");
            return [];
        }

        if (_searchSession == null && !string.IsNullOrEmpty(_currentQuery))
        {
            var existingIds = AllItems.Select(t => t.Id).ToList();
            Log.Info($"[Search] Creating session on-demand, skipping {existingIds.Count} cached tracks");
            _searchSession = _youtube.CreateSearchSession(_currentQuery, MaxResults, existingIds);
        }

        if (_searchSession != null && _searchSession.HasMore)
        {
            var remaining = MaxResults - TotalCount;
            var batchSize = Math.Min(ScrollBatchSize, remaining);

            Log.Info($"[Search] Fetching {batchSize} from session...");

            try
            {
                var newTracks = await _searchSession.FetchNextBatchAsync(batchSize, ct);

                if (ct.IsCancellationRequested) return [];

                if (newTracks.Count > 0 && LibService.Data.EnableSearchCache)
                {
                    var allTracks = AllItems.Concat(newTracks).ToList();
                    _ = _searchCache.SetAsync(_currentQuery, allTracks);

                    var imageUrls = newTracks.Take(10).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                    _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                    Log.Info($"[Search] Got {newTracks.Count} tracks, session hasMore: {_searchSession.HasMore}");
                }

                if (!_searchSession.HasMore)
                {
                    SetCanFetchMore(false);
                }

                return newTracks;
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"[Search] HTTP error: {ex.Message}");
                ErrorMessage = L["Search_NetworkError"];
                return [];
            }
        }

        return [];
    }

    private async Task ExecuteSearchAsync(bool forceNetwork = false)
    {
        // Debounce защита (для кнопки поиска)
        if (!forceNetwork && !CanExecuteSearch()) return;

        _searchCts?.Cancel();
        _searchSession?.Dispose();
        _searchSession = null;

        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        CancelLoading();
        IsLoading = true;
        ErrorMessage = null;
        IsFromCache = false;
        ClearItems();
        HasResults = false;

        _currentQuery = SearchQuery.Trim();

        LibService.Data.LastSearchQuery = _currentQuery;
        AddToHistory(_currentQuery);
        LibService.Save();

        try
        {
            // Небольшая задержка для UI
            await Task.Delay(50, ct);

            var queryType = YoutubeProvider.DetectQueryType(_currentQuery);

            if (queryType == QueryType.DirectUrl)
            {
                await HandleDirectUrlAsync(ct);
            }
            else if (queryType == QueryType.Playlist)
            {
                await HandlePlaylistAsync(ct);
            }
            else
            {
                await HandleSearchAsync(ct, forceNetwork);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("[Search] Cancelled");
        }
        catch (HttpRequestException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                ErrorMessage = L["Search_NetworkError"];
                Log.Error($"[Search] HTTP error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                ErrorMessage = ex.Message;
                Log.Error($"[Search] Error: {ex.Message}");
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

    private async Task HandleDirectUrlAsync(CancellationToken ct)
    {
        var track = await _youtube.GetTrackByUrlAsync(_currentQuery);

        if (ct.IsCancellationRequested) return;

        var tracks = track != null ? [track] : new List<TrackInfo>();
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (track != null && LibService.Data.AutoPlayOnUrlPaste)
        {
            _ = _audio.PlayTrackAsync(track);
        }

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = L["Search_NoResults"];
    }

    private async Task HandlePlaylistAsync(CancellationToken ct)
    {
        IsFetchingFromNetwork = true;
        var playlist = await _youtube.GetPlaylistAsync(_currentQuery);

        if (ct.IsCancellationRequested) return;

        var tracks = playlist?.Tracks ?? [];
        IsFetchingFromNetwork = false;
        await InitializeItemsAsync(tracks, canFetchMore: false);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = L["Search_NoResults"];
    }

    private async Task HandleSearchAsync(CancellationToken ct, bool forceNetwork)
    {
        // 1. Проверяем кэш (если не принудительный поиск и кэш включен)
        if (!forceNetwork && LibService.Data.EnableSearchCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, minCount: 20);

            if (ct.IsCancellationRequested) return;

            if (cached != null && cached.Count >= 20)
            {
                bool canFetchMore = cached.Count < MaxResults;

                Log.Info($"[Search] Cache HIT: {cached.Count} tracks");
                IsFromCache = true;

                await InitializeItemsAsync(cached, canFetchMore: canFetchMore);
                HasResults = true;

                var imageUrls = cached.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                return;
            }
        }

        // 2. Загружаем из сети
        Log.Info($"[Search] {(forceNetwork ? "FORCE" : "Cache MISS")}, fetching from network...");
        IsFetchingFromNetwork = true;
        IsFromCache = false;

        // Очищаем кэш для этого запроса если принудительный поиск
        if (forceNetwork)
        {
            _searchCache.InvalidateQuery(_currentQuery);
        }

        var (tracks, session) = await _youtube.SearchWithSessionAsync(_currentQuery, InitialBatchSize, MaxResults, ct);
        _searchSession = session;

        if (ct.IsCancellationRequested)
        {
            session?.Dispose();
            return;
        }

        IsFetchingFromNetwork = false;

        if (tracks.Count > 0 && LibService.Data.EnableSearchCache)
        {
            _ = _searchCache.SetAsync(_currentQuery, tracks);

            var imageUrls = tracks.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
            _ = _imageCache.PrefetchAsync(imageUrls!, ct);
        }

        bool hasMore = session?.HasMore ?? false;
        Log.Info($"[Search] Initial load: {tracks.Count} tracks, hasMore: {hasMore}");

        await InitializeItemsAsync(tracks, canFetchMore: hasMore);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = L["Search_NoResults"];
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
        LibService.Data.SearchHistory = RecentSearches.ToList();
        LibService.Save();
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        var tracks = Items.Select(x => x.Track).ToList();
        _ = _audio.StartQueueAsync(tracks, track);
        LibService.AddToRecentlyPlayed(track);
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchSession?.Dispose();
        CancelLoading();
        GC.SuppressFinalize(this);
    }
}


