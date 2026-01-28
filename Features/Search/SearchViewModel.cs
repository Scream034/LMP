using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using LMP.Core.Youtube.Search; // Важно: для SearchFilter

namespace LMP.Features.Search;

public sealed class SearchViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>
{
    #region Constants
    private const int DebounceMs = 300;
    private const int MaxResults = 300;
    #endregion

    #region Fields
    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly TrackViewModelFactory _vmFactory;

    private string _currentQuery = "";
    private CancellationTokenSource? _searchCts;
    private YoutubeProvider.SearchSession? _searchSession;
    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool _isDisposed;
    #endregion

    #region Properties
    private int InitialBatchSize => LibService.Data.LoadBatchSize > 0 ? LibService.Data.LoadBatchSize * 2 : 50;
    private int ScrollBatchSize => LibService.Data.SearchBatchSize > 0 ? LibService.Data.SearchBatchSize : 30;

    // Это строка для API запроса (Глобальный поиск)
    [Reactive] public string SearchQuery { get; set; } = string.Empty;
    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }
    [Reactive] public bool IsFromCache { get; private set; }
    public bool ShowForceSearchButton => LibService.Data.EnableSearchCache && IsFromCache && !IsLoading;
    public ObservableCollection<string> RecentSearches { get; } = [];
    #endregion

    #region Commands
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceSearchCommand { get; }
    public ReactiveCommand<string, Unit> HistoryClickCommand { get; }
    public ReactiveCommand<string, Unit> RemoveHistoryCommand { get; }
    #endregion

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

        var canForceSearch = this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading,
            (cache, loading) => cache && !loading);

        ForceSearchCommand = ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: true),
            canForceSearch);

        HistoryClickCommand = ReactiveCommand.CreateFromTask<string>(async q =>
        {
            if (string.IsNullOrEmpty(q)) return;
            SearchQuery = q;
            await ExecuteSearchAsync(false);
        });

        RemoveHistoryCommand = ReactiveCommand.Create<string>(q =>
        {
            RecentSearches.Remove(q);
            UpdateHistoryStorage();
        });

        SearchCommand.ThrownExceptions.Subscribe(ex => LogError("SearchCommand", ex));
        ForceSearchCommand.ThrownExceptions.Subscribe(ex => LogError("ForceSearchCommand", ex));
        HistoryClickCommand.ThrownExceptions.Subscribe(ex => LogError("HistoryClickCommand", ex));

        this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowForceSearchButton)));

        // Автоматический перезапуск поиска при изменении типа фильтра (Музыка/Видео/Все)
        // Исправлен синтаксис Subscribe для избежания ошибки CS0029
        this.WhenAnyValue(x => x.FilterType)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if (!string.IsNullOrWhiteSpace(SearchQuery))
                    await ExecuteSearchAsync(forceNetwork: false);
            });

        if (!string.IsNullOrEmpty(LibService.Data.LastSearchQuery))
        {
            SearchQuery = LibService.Data.LastSearchQuery;
            _ = ExecuteSearchAsync(false);
        }
    }

    private void LogError(string source, Exception ex)
    {
        if (ex is OperationCanceledException) return;
        Log.Error($"[{source}] Unhandled error: {ex.Message}");
        ErrorMessage = SL["Search_NetworkError"];
        IsLoading = false;
    }

    /// <summary>
    /// Конвертирует UI фильтр в фильтр YouTube API.
    /// </summary>
    private SearchFilter GetYoutubeSearchFilter()
    {
        return FilterType switch
        {
            ContentFilterType.Music => SearchFilter.Music,
            ContentFilterType.Video => SearchFilter.Video,
            // Для "All" используем None (YouTube сам решит, обычно смешанная выдача)
            _ => SearchFilter.None
        };
    }

    protected override bool FilterItem(TrackInfo item)
    {
        // 1. Фильтр по типу.
        // Если выбран "Video", мы не должны скрывать музыку, так как клип - это тоже видео.
        // Но если выбран "Music", мы хотим видеть только музыку.
        if (FilterType == ContentFilterType.Music && !item.IsMusic) return false;

        // 2. Текстовый поиск (локальный)
        if (!string.IsNullOrWhiteSpace(FilterQuery))
        {
            return item.Title.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                   item.Author.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase);
        }

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
        if (TotalCount >= MaxResults) return [];

        if (_searchSession == null && !string.IsNullOrEmpty(_currentQuery))
        {
            var existingIds = GetLoadedItemsIds();
            // Передаем правильный фильтр
            _searchSession = _youtube.CreateSearchSession(_currentQuery, MaxResults, GetYoutubeSearchFilter(), existingIds);
        }

        if (_searchSession != null && _searchSession.HasMore)
        {
            try
            {
                var newTracks = await _searchSession.FetchNextBatchAsync(ScrollBatchSize, ct);

                if (ct.IsCancellationRequested) return [];

                if (newTracks.Count > 0 && LibService.Data.EnableSearchCache)
                {
                    var currentItems = GetItemsSnapshot();
                    var allTracks = currentItems.Concat(newTracks).ToList();
                    _ = _searchCache.SetAsync(_currentQuery, allTracks);

                    var imageUrls = newTracks.Take(10).Select(static t => t.ThumbnailUrl).Where(static u => !string.IsNullOrEmpty(u));
                    _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                }

                if (!_searchSession.HasMore)
                {
                    SetCanFetchMore(false);
                }

                return newTracks;
            }
            catch (OperationCanceledException)
            {
                return [];
            }
            catch (HttpRequestException)
            {
                ErrorMessage = SL["Search_NetworkError"];
                return [];
            }
            catch (Exception ex)
            {
                Log.Error($"[Search] FetchMore error: {ex.Message}");
                return [];
            }
        }

        return [];
    }

    private bool CanExecuteSearch()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSearchTime).TotalMilliseconds < DebounceMs) return false;
        _lastSearchTime = now;
        return true;
    }

    private async Task ExecuteSearchAsync(bool forceNetwork)
    {
        if (!forceNetwork && !CanExecuteSearch()) return;

        CancellationTokenSource? currentCts = null;
        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            currentCts = _searchCts;
            var ct = currentCts.Token;

            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;

            CancelLoading();
            IsLoading = true;
            ErrorMessage = null;
            IsFromCache = false;

            ClearItems();

            HasResults = false;
            _currentQuery = SearchQuery.Trim();

            try
            {
                LibService.Data.LastSearchQuery = _currentQuery;
                AddToHistory(_currentQuery);
                LibService.Save();
            }
            catch (Exception ex) { Log.Error($"History save error: {ex.Message}"); }

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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Log.Error($"[Search] Error executing search: {ex}");
        }
        finally
        {
            if (currentCts == _searchCts && !currentCts!.IsCancellationRequested)
            {
                IsLoading = false;
                IsFetchingFromNetwork = false;
            }
        }
    }

    private async Task HandleDirectUrlAsync(CancellationToken ct)
    {
        var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();

        var tracks = track != null ? [track] : new List<TrackInfo>();
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (track != null && LibService.Data.AutoPlayOnUrlPaste)
        {
            _ = _audio.PlayTrackAsync(track);
        }

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandlePlaylistAsync(CancellationToken ct)
    {
        IsFetchingFromNetwork = true;
        var playlist = await _youtube.GetPlaylistAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();

        var tracks = playlist?.Tracks ?? [];
        IsFetchingFromNetwork = false;
        await InitializeItemsAsync(tracks, canFetchMore: false);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandleSearchAsync(CancellationToken ct, bool forceNetwork)
    {
        // Кэш используем только для "All", так как кэширование по фильтрам требует изменения структуры кэша.
        bool useCache = !forceNetwork && LibService.Data.EnableSearchCache && FilterType == ContentFilterType.All;

        if (useCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, minCount: 20);
            ct.ThrowIfCancellationRequested();

            if (cached != null && cached.Count >= 20)
            {
                IsFromCache = true;
                await InitializeItemsAsync(cached, canFetchMore: cached.Count < MaxResults);
                HasResults = true;

                var urls = cached.Take(20).Select(static t => t.ThumbnailUrl);
                _ = _imageCache.PrefetchAsync(urls!, ct);
                return;
            }
        }

        IsFetchingFromNetwork = true;
        IsFromCache = false;

        if (forceNetwork) _searchCache.InvalidateQuery(_currentQuery);

        // Важно: передаем правильный фильтр API
        var apiFilter = GetYoutubeSearchFilter();

        var (tracks, session) = await _youtube.SearchWithSessionAsync(
            _currentQuery,
            InitialBatchSize,
            MaxResults,
            apiFilter,
            ct);

        _searchSession = session;

        // Если мы искали с фильтром Music, принудительно ставим IsMusic = true для надежности
        // (хотя VideoSearchResult теперь должен возвращать правильно)
        if (apiFilter == SearchFilter.Music)
        {
            foreach (var t in tracks) t.IsMusic = true;
        }

        ct.ThrowIfCancellationRequested();

        IsFetchingFromNetwork = false;

        // Кэшируем только смешанную выдачу
        if (tracks.Count > 0 && LibService.Data.EnableSearchCache && FilterType == ContentFilterType.All)
        {
            _ = _searchCache.SetAsync(_currentQuery, tracks);
            var urls = tracks.Take(20).Select(static t => t.ThumbnailUrl);
            _ = _imageCache.PrefetchAsync(urls!, ct);
        }

        bool hasMore = session?.HasMore ?? false;
        await InitializeItemsAsync(tracks, canFetchMore: hasMore);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private void AddToHistory(string query)
    {
        RecentSearches.Remove(query);
        RecentSearches.Insert(0, query);
        while (RecentSearches.Count > 10) RecentSearches.RemoveAt(RecentSearches.Count - 1);
        UpdateHistoryStorage();
    }

    private void UpdateHistoryStorage()
    {
        LibService.Data.SearchHistory = [.. RecentSearches];
        LibService.Save();
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        _ = _audio.PlayTrackAsync(track);
        LibService.AddToRecentlyPlayed(track);
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;

            foreach (var item in Items)
            {
                item.Dispose();
            }
        }
        base.Dispose(disposing);
        _isDisposed = true;
    }
}