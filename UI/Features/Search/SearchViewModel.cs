using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Diagnostics;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;


namespace LMP.UI.Features.Search;

/// <summary>
/// ViewModel экрана поиска треков.
///
/// <para><b>Источники:</b> YouTube Music, YouTube, Local (библиотека).</para>
///
/// <para><b>Стратегия загрузки:</b>
/// <list type="bullet">
///   <item>Local — <see cref="LibraryService.SearchLocalTracksAsync"/>, без сети.</item>
///   <item>DirectUrl — одиночный трек через <see cref="YoutubeProvider.GetTrackByUrlAsync"/>.</item>
///   <item>Playlist URL — все треки через <see cref="YoutubeProvider.GetPlaylistAsync"/>.</item>
///   <item>Текстовый запрос — кэш → сеть, InfiniteScroll через <see cref="YoutubeProvider.SearchSession"/>.</item>
/// </list></para>
///
/// <para><b>Smart Parent</b> (активный трек, прогресс загрузки) унаследован от
/// <see cref="TrackListPaginatedViewModel"/>: нет N подписок на каждую TrackItemViewModel.</para>
/// </summary>
public sealed class SearchViewModel : TrackListPaginatedViewModel
{
    #region Constants

    private const int DebounceMs = 300;
    private const int MaxResults = 300;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;

    private string _currentQuery = "";
    private CancellationTokenSource? _searchCts;
    private YoutubeProvider.SearchSession? _searchSession;
    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool _isDisposed;

    #endregion

    #region Properties

    private int InitialBatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize * 2
        : 50;

    private int ScrollBatchSize => LibService.Settings.SearchBatchSize > 0
        ? LibService.Settings.SearchBatchSize
        : 30;

    [Reactive] public string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// Источник поиска: YouTube Music (музыка), YouTube (всё), Local (локальные).
    /// </summary>
    [Reactive] public ContentSource Source { get; set; } = ContentSource.YouTubeMusic;

    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }
    [Reactive] public bool IsFromCache { get; private set; }
    [Reactive] public bool IsOfflineMode { get; private set; }

    /// <summary>
    /// False при первом открытии — страница отрисовывается мгновенно без загрузки данных.
    /// Устанавливается в true из <see cref="OnNavigatedToAsync"/>.
    /// </summary>
    [Reactive] public bool IsInitialized { get; private set; }

    /// <summary>
    /// Кнопка принудительного обновления: видна только при наличии кэшированных результатов.
    /// </summary>
    public bool ShowForceSearchButton =>
        LibService.Settings.EnableSearchCache && IsFromCache && !IsLoading;

    /// <summary>
    /// История поиска. Отдельная коллекция (не прямой биндинг на Settings)
    /// — предотвращает binding errors при dispose страницы.
    /// </summary>
    public ObservableCollection<string> RecentSearches { get; } = [];

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceSearchCommand { get; }
    public ReactiveCommand<string, Unit> HistoryClickCommand { get; }
    public ReactiveCommand<string, Unit> RemoveHistoryCommand { get; }
    public ReactiveCommand<string, Unit> SetSourceCommand { get; }

    #endregion

    #region Constructor

    public SearchViewModel(
      AudioEngine audio,
      DownloadService downloads,
      TrackViewModelFactory vmFactory,
      YoutubeProvider youtube,
      SearchCacheService searchCache,
      ImageCacheService imageCache)
      : base(audio, downloads, vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        foreach (var item in LibService.Settings.SearchHistory)
            RecentSearches.Add(item);

        var canSearch = this.WhenAnyValue(
            x => x.SearchQuery, x => x.IsLoading,
            static (q, loading) => !string.IsNullOrWhiteSpace(q) && !loading);

        SearchCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: false),
            canSearch));

        var canForceSearch = this.WhenAnyValue(
            x => x.IsFromCache, x => x.IsLoading,
            static (cache, loading) => cache && !loading);

        ForceSearchCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: true),
            canForceSearch));

        HistoryClickCommand = CreateCommand(ReactiveCommand.CreateFromTask<string>(async q =>
        {
            if (_isDisposed || string.IsNullOrEmpty(q)) return;
            SearchQuery = q;
            await ExecuteSearchAsync(false);
        }));

        RemoveHistoryCommand = CreateCommand(ReactiveCommand.Create<string>(q =>
        {
            if (_isDisposed) return;
            RecentSearches.Remove(q);
            UpdateHistoryStorage();
        }));

        SetSourceCommand = CreateCommand(ReactiveCommand.Create<string>(sourceStr =>
        {
            if (_isDisposed) return;
            if (Enum.TryParse<ContentSource>(sourceStr, true, out var result))
                Source = result;
        }));

        this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowForceSearchButton)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.Source)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async source =>
            {
                if (_isDisposed) return;
                IsOfflineMode = source == ContentSource.Local;

                if (!string.IsNullOrWhiteSpace(SearchQuery))
                    await ExecuteSearchAsync(forceNetwork: false);
            })
            .DisposeWith(Disposables);
    }

    #endregion

    #region Navigation

    /// <summary>
    /// Отмечает страницу готовой — UI переключается со скелетона на рабочее состояние.
    /// Данные грузятся по запросу пользователя, не здесь.
    /// </summary>
    public override Task OnNavigatedToAsync()
    {
        if (!_isDisposed)
            IsInitialized = true;

        return Task.CompletedTask;
    }

    #endregion

    #region TrackListPaginatedViewModel Implementation

    /// <summary>
    /// Запускает воспроизведение из контекста поиска.
    /// StartQueueAsync формирует очередь из одного трека с автоматическим докачиванием.
    /// </summary>
    protected override void OnPlay(TrackInfo track)
    {
        if (_isDisposed) return;
        _ = Task.Run(async () => await Audio.StartQueueAsync([track], track));
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    /// <summary>
    /// InfiniteScroll: загружает следующую порцию через активную <see cref="YoutubeProvider.SearchSession"/>.
    /// Для Local-источника InfiniteScroll отключён (<see cref="InitializeItemsAsync"/> canFetchMore=false).
    /// </summary>
    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (_isDisposed || Source == ContentSource.Local || TotalCount >= MaxResults)
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
            _searchSession = _youtube.CreateSearchSession(
                _currentQuery, MaxResults, currentFilter, existingIds);
        }

        if (_searchSession == null || !_searchSession.HasMore) return [];

        try
        {
            var newTracks = await _searchSession.FetchNextBatchAsync(ScrollBatchSize, ct);
            if (ct.IsCancellationRequested || _isDisposed) return [];

            if (Source == ContentSource.YouTubeMusic)
                foreach (var t in newTracks) t.IsMusic = true;

            if (newTracks.Count > 0)
            {
                AudioSourceFactory.GlobalCache?.HydrateCacheStatus(newTracks);

                if (LibService.Settings.EnableSearchCache)
                {
                    var snapshot = GetItemsSnapshot();
                    var all = new List<TrackInfo>(snapshot.Count + newTracks.Count);
                    all.AddRange(snapshot);
                    all.AddRange(newTracks);
                    _ = _searchCache.SetAsync(_currentQuery, SourceToSearchSource(), all);

                    var imageUrls = newTracks.Take(10)
                        .Select(static t => t.ThumbnailUrl)
                        .Where(static u => !string.IsNullOrEmpty(u));
                    _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                }
            }

            if (!_searchSession.HasMore)
                SetCanFetchMore(false);

            return newTracks;
        }
        catch (OperationCanceledException) { return []; }
        catch (HttpRequestException) { ErrorMessage = SL["Search_NetworkError"]; return []; }
        catch (Exception ex)
        {
            Log.Error($"[Search] FetchMore error: {ex.Message}");
            return [];
        }
    }

    #endregion

    #region Search Logic

    private SearchFilter GetSearchFilter() => Source switch
    {
        ContentSource.YouTubeMusic => SearchFilter.MusicSong,
        ContentSource.YouTube => SearchFilter.Video,
        ContentSource.Local => SearchFilter.None,
        _ => SearchFilter.MusicSong
    };

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
        if (_isDisposed) return;
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
            HasResults = false;

            ClearItems();

            _currentQuery = SearchQuery.Trim();

            try
            {
                AddToHistory(_currentQuery);
            }
            catch (Exception ex)
            {
                Log.Error($"[Search] History save error: {ex.Message}");
            }

            await Task.Delay(50, ct);

            if (Source == ContentSource.Local)
            {
                await HandleLocalSearchAsync(ct);
                return;
            }

            var queryType = YoutubeProvider.DetectQueryType(_currentQuery);

            if (queryType == QueryType.DirectUrl)
                await HandleDirectUrlAsync(ct);
            else if (queryType == QueryType.Playlist)
                await HandlePlaylistAsync(ct);
            else
                await HandleSearchAsync(ct, forceNetwork);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                ErrorMessage = ex.Message;
                Log.Error($"[Search] Error executing search: {ex}");
            }
        }
        finally
        {
            if (!_isDisposed
                && currentCts == _searchCts
                && currentCts != null
                && !currentCts.IsCancellationRequested)
            {
                IsLoading = false;
                IsFetchingFromNetwork = false;
            }
        }
    }

    private async Task HandleLocalSearchAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        Log.Debug($"[Search] Local search: '{_currentQuery}'");

        var filtered = string.IsNullOrWhiteSpace(_currentQuery)
            ? await LibService.GetLocalTracksAsync(MaxResults, 0, ct)
            : await LibService.SearchLocalTracksAsync(_currentQuery, MaxResults, ct);

        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        await InitializeItemsAsync(filtered, canFetchMore: false);
        HasResults = filtered.Count > 0;

        if (!HasResults)
        {
            var localCount = await LibService.GetLocalTrackCountAsync(ct);
            ErrorMessage = localCount == 0
                ? SL["Search_NoLocalFiles"]
                : SL["Search_NoResults"];
        }

        Log.Info($"[Search] Local: {filtered.Count} results");
    }

    private async Task HandleDirectUrlAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        var tracks = track != null ? [track] : new List<TrackInfo>();
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (track != null && LibService.Settings.AutoPlayOnUrlPaste)
            _ = Task.Run(async () => await Audio.PlayTrackAsync(track), ct);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandlePlaylistAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        IsFetchingFromNetwork = true;
        var playlist = await _youtube.GetPlaylistAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        var tracks = playlist?.Tracks ?? [];
        IsFetchingFromNetwork = false;
        await InitializeItemsAsync(tracks, canFetchMore: false);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandleSearchAsync(CancellationToken ct, bool forceNetwork)
    {
        if (_isDisposed) return;

        var sw = Stopwatch.StartNew();
        var cacheSource = SourceToSearchSource();
        bool useCache = !forceNetwork && LibService.Settings.EnableSearchCache;

        if (useCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, cacheSource, minCount: 20);
            ct.ThrowIfCancellationRequested();
            if (_isDisposed) return;

            if (cached is { Count: >= 20 })
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

        var (tracks, session) = await _youtube.SearchWithSessionAsync(
            _currentQuery, InitialBatchSize, MaxResults, GetSearchFilter(), ct);

        if (_isDisposed) return;

        _searchSession = session;

        if (Source == ContentSource.YouTubeMusic)
            foreach (var t in tracks) t.IsMusic = true;

        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

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
        Log.Info($"[Search] {tracks.Count} results in {sw.ElapsedMilliseconds}ms, hasMore={hasMore}");
    }

    #endregion

    #region History

    private void AddToHistory(string query)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(query)) return;
        RecentSearches.Remove(query);
        RecentSearches.Insert(0, query);
        while (RecentSearches.Count > 10) RecentSearches.RemoveAt(RecentSearches.Count - 1);
        UpdateHistoryStorage();
    }

    private void UpdateHistoryStorage()
    {
        if (_isDisposed) return;
        LibService.UpdateSettings(s => s.SearchHistory = [.. RecentSearches]);
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _isDisposed = true;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}