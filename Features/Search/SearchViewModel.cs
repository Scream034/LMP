using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Diagnostics;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using LMP.Core.Helpers;

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
    private readonly StreamCacheManager _streamCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly TrackViewModelFactory _vmFactory;

    private string _currentQuery = "";
    private ContentSource _currentSource = ContentSource.YouTubeMusic;
    private CancellationTokenSource? _searchCts;
    private YoutubeProvider.SearchSession? _searchSession;
    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool _isDisposed;
    #endregion

    #region Properties
    private int InitialBatchSize => LibService.Settings.LoadBatchSize > 0 ? LibService.Settings.LoadBatchSize * 2 : 50;
    private int ScrollBatchSize => LibService.Settings.SearchBatchSize > 0 ? LibService.Settings.SearchBatchSize : 30;

    [Reactive] public string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// Источник поиска: YouTube Music (музыка), YouTube (всё), Local (локальные).
    /// </summary>
    [Reactive] public ContentSource Source { get; set; } = ContentSource.YouTubeMusic;

    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }
    [Reactive] public bool IsFromCache { get; private set; }
    [Reactive] public bool IsOfflineMode { get; private set; }

    public bool ShowForceSearchButton => LibService.Settings.EnableSearchCache && IsFromCache && !IsLoading;
    public ObservableCollection<string> RecentSearches { get; } = [];

    public string SourceDisplayName => Source.GetDisplayName();
    public string SourceIcon => Source.GetIcon();
    #endregion

    #region Commands
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceSearchCommand { get; }
    public ReactiveCommand<string, Unit> HistoryClickCommand { get; }
    public ReactiveCommand<string, Unit> RemoveHistoryCommand { get; }
    public ReactiveCommand<string, Unit> SetSourceCommand { get; }
    #endregion

    public SearchViewModel(
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        AudioEngine audio,
        TrackViewModelFactory vmFactory,
        StreamCacheManager streamCache)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _audio = audio;
        _vmFactory = vmFactory;
        _streamCache = streamCache;

        foreach (var item in LibService.Settings.SearchHistory)
            RecentSearches.Add(item);

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

        SetSourceCommand = ReactiveCommand.Create<string>(sourceStr =>
        {
            if (Enum.TryParse<ContentSource>(sourceStr, true, out var result))
            {
                Source = result;
            }
        });

        SearchCommand.ThrownExceptions.Subscribe(ex => LogError("SearchCommand", ex));
        ForceSearchCommand.ThrownExceptions.Subscribe(ex => LogError("ForceSearchCommand", ex));

        this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowForceSearchButton)));

        // При изменении источника — обновляем UI и перезапускаем поиск
        this.WhenAnyValue(x => x.Source)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async source =>
            {
                this.RaisePropertyChanged(nameof(SourceDisplayName));
                this.RaisePropertyChanged(nameof(SourceIcon));
                IsOfflineMode = source == ContentSource.Local;

                if (!string.IsNullOrWhiteSpace(SearchQuery))
                {
                    Log.Debug($"[Search] Source changed to {source}, re-executing search...");
                    await ExecuteSearchAsync(forceNetwork: false);
                }
            });

        if (!string.IsNullOrEmpty(LibService.Settings.LastSearchQuery))
        {
            SearchQuery = LibService.Settings.LastSearchQuery;
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
    /// Конвертирует ContentSource в SearchFilter для YouTube API.
    /// </summary>
    private SearchFilter GetSearchFilter() => Source switch
    {
        ContentSource.YouTubeMusic => SearchFilter.MusicSong,
        ContentSource.YouTube => SearchFilter.Video,
        ContentSource.Local => SearchFilter.None, // Не используется для локальных
        _ => SearchFilter.MusicSong
    };

    protected override bool FilterItem(TrackInfo item, string query)
        => TrackFilters.MatchesTitleOrAuthor(item, query);

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        return _vmFactory.GetOrCreate(track, PlayTrackWithContext);
    }

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        // Локальные файлы не подгружаются с сети
        if (Source == ContentSource.Local || TotalCount >= MaxResults)
            return [];

        var currentFilter = GetSearchFilter();

        if (_searchSession != null && _searchSession.Filter != currentFilter)
        {
            _searchSession.Dispose();
            _searchSession = null;
        }

        if (_searchSession == null && !string.IsNullOrEmpty(_currentQuery))
        {
            var existingIds = GetLoadedItemsIds();
            _searchSession = _youtube.CreateSearchSession(_currentQuery, MaxResults, currentFilter, existingIds);
        }

        if (_searchSession != null && _searchSession.HasMore)
        {
            try
            {
                var newTracks = await _searchSession.FetchNextBatchAsync(ScrollBatchSize, ct);

                if (ct.IsCancellationRequested) return [];

                // Помечаем треки из YouTube Music как музыку
                if (Source == ContentSource.YouTubeMusic)
                {
                    foreach (var t in newTracks) t.IsMusic = true;
                }

                if (newTracks.Count > 0)
                {
                    // ИСПРАВЛЕНИЕ #3: Проверка наличия трека в полном кэше
                    // ChunkCacheService (StreamCacheManager) знает, скачан ли файл полностью
                    _streamCache.HydrateCacheStatus(newTracks);

                    if (LibService.Settings.EnableSearchCache)
                    {
                        var currentItems = GetItemsSnapshot();
                        var allTracks = currentItems.Concat(newTracks).ToList();
                        _ = _searchCache.SetAsync(_currentQuery, SourceToSearchSource(), allTracks);

                        var imageUrls = newTracks.Take(10)
                            .Select(static t => t.ThumbnailUrl)
                            .Where(static u => !string.IsNullOrEmpty(u));
                        _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                    }
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

    /// <summary>
    /// Конвертирует ContentSource в SearchSource для кэша.
    /// </summary>
    private SearchSource SourceToSearchSource() => Source switch
    {
        ContentSource.YouTubeMusic => SearchSource.YouTubeMusic,
        ContentSource.YouTube => SearchSource.YouTube,
        _ => SearchSource.YouTube
    };

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
            _currentSource = Source;

            try
            {
                LibService.UpdateSettings(s => s.LastSearchQuery = _currentQuery);
                AddToHistory(_currentQuery);
            }
            catch (Exception ex) { Log.Error($"History save error: {ex.Message}"); }

            await Task.Delay(50, ct);

            // Локальный поиск
            if (Source == ContentSource.Local)
            {
                await HandleLocalSearchAsync(ct);
                return;
            }

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

    /// <summary>
    /// Поиск по локальной библиотеке (оффлайн).
    /// </summary>
    private async Task HandleLocalSearchAsync(CancellationToken ct)
    {
        Log.Debug($"[Search] Local search: '{_currentQuery}'");

        List<TrackInfo> filtered;

        if (string.IsNullOrWhiteSpace(_currentQuery))
        {
            // Если запрос пустой — показываем все локальные треки
            filtered = await LibService.GetLocalTracksAsync(MaxResults, 0, ct);
        }
        else
        {
            // Поиск по локальной библиотеке
            filtered = await LibService.SearchLocalTracksAsync(_currentQuery, MaxResults, ct);
        }

        ct.ThrowIfCancellationRequested();

        await InitializeItemsAsync(filtered, canFetchMore: false);

        HasResults = filtered.Count > 0;

        if (!HasResults)
        {
            var localCount = await LibService.GetLocalTrackCountAsync(ct);
            ErrorMessage = localCount == 0
                ? SL["Search_NoLocalFiles"]
                : SL["Search_NoResults"];
        }

        Log.Info($"[Search] Local search complete: {filtered.Count} results");
    }

    private async Task HandleDirectUrlAsync(CancellationToken ct)
    {
        var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();

        var tracks = track != null ? [track] : new List<TrackInfo>();
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (track != null && LibService.Settings.AutoPlayOnUrlPaste)
        {
            _ = Task.Run(async () => await _audio.PlayTrackAsync(track), ct);
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
        var sw = Stopwatch.StartNew();
        var apiFilter = GetSearchFilter();
        var cacheSource = SourceToSearchSource();

        bool useCache = !forceNetwork && LibService.Settings.EnableSearchCache;

        if (useCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, cacheSource, minCount: 20);
            ct.ThrowIfCancellationRequested();

            if (cached != null && cached.Count >= 20)
            {
                IsFromCache = true;
                await InitializeItemsAsync(cached, canFetchMore: cached.Count < MaxResults);
                HasResults = true;

                var urls = cached.Take(20).Select(static t => t.ThumbnailUrl);
                _ = _imageCache.PrefetchAsync(urls!, ct);

                Log.Debug($"[Search] Cache hit: {cached.Count} items in {sw.ElapsedMilliseconds}ms");
                return;
            }
        }

        IsFetchingFromNetwork = true;
        IsFromCache = false;

        if (forceNetwork)
            _searchCache.InvalidateQuery(_currentQuery, cacheSource);

        Log.Debug($"[Search] Network search: '{_currentQuery}' (Source: {Source})");

        var (tracks, session) = await _youtube.SearchWithSessionAsync(
            _currentQuery,
            InitialBatchSize,
            MaxResults,
            apiFilter,
            ct);

        _searchSession = session;

        // Помечаем треки из YouTube Music как музыку
        if (Source == ContentSource.YouTubeMusic)
        {
            foreach (var t in tracks) t.IsMusic = true;
        }

        ct.ThrowIfCancellationRequested();
        IsFetchingFromNetwork = false;

        if (tracks.Count > 0 && LibService.Settings.EnableSearchCache)
        {
            _ = _searchCache.SetAsync(_currentQuery, cacheSource, tracks);
            var urls = tracks.Take(20).Select(static t => t.ThumbnailUrl);
            _ = _imageCache.PrefetchAsync(urls!, ct);
        }

        bool hasMore = session?.HasMore ?? false;
        await InitializeItemsAsync(tracks, canFetchMore: hasMore);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];

        sw.Stop();
        Log.Info($"[Search] Got {tracks.Count} results in {sw.ElapsedMilliseconds}ms, hasMore: {hasMore}");
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
        LibService.UpdateSettings(s => s.SearchHistory = [.. RecentSearches]);
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        _ = Task.Run(async () => await _audio.StartQueueAsync([track], track));
        _ = LibService.AddToRecentlyPlayedAsync(track);
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