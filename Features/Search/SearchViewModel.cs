// ============================================================================
// Файл: Features/Search/SearchViewModel.cs
// Описание: ViewModel страницы поиска.
// Исправления:
//   - [FIX] Корректное переопределение Dispose.
//   - [FIX] Очистка сессии поиска YouTube (освобождение буферов).
//   - [FIX] Отмена активных задач через CancellationTokenSource.
// ============================================================================

using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using MyLiteMusicPlayer.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Features.Search;

/// <summary>
/// ViewModel для страницы поиска.
/// Поддерживает поиск YouTube, работу с кэшем и историю.
/// </summary>
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

    /// <summary>
    /// Строка запроса.
    /// </summary>
    [Reactive] public string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// Найдены ли результаты.
    /// </summary>
    [Reactive] public bool HasResults { get; private set; }

    /// <summary>
    /// Текст ошибки.
    /// </summary>
    [Reactive] public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Результаты загружены из кэша?
    /// </summary>
    [Reactive] public bool IsFromCache { get; private set; }

    /// <summary>
    /// Видимость кнопки "Принудительный поиск".
    /// </summary>
    public bool ShowForceSearchButton => LibService.Data.EnableSearchCache && IsFromCache && !IsLoading;

    /// <summary>
    /// История поиска.
    /// </summary>
    public ObservableCollection<string> RecentSearches { get; } = [];

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceSearchCommand { get; }
    public ReactiveCommand<string, Unit> HistoryClickCommand { get; }
    public ReactiveCommand<string, Unit> RemoveHistoryCommand { get; }

    #endregion

    #region Constructor

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

        // Загрузка истории
        if (LibService.Data.SearchHistory != null)
        {
            foreach (var item in LibService.Data.SearchHistory)
                RecentSearches.Add(item);
        }

        // Команды
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

        // Обновление свойства кнопки принудительного поиска
        this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowForceSearchButton)));

        // Восстановление последнего запроса
        if (!string.IsNullOrEmpty(LibService.Data.LastSearchQuery))
        {
            SearchQuery = LibService.Data.LastSearchQuery;
            _ = ExecuteSearchAsync(false);
        }
    }

    #endregion

    #region Overrides

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        // Синхронизация состояния с библиотекой (лайки, скачивание)
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

    /// <summary>
    /// Загружает следующую страницу результатов.
    /// </summary>
    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (TotalCount >= MaxResults) return [];

        // Восстановление сессии при необходимости
        if (_searchSession == null && !string.IsNullOrEmpty(_currentQuery))
        {
            var existingIds = AllItems.Select(t => t.Id).ToList();
            _searchSession = _youtube.CreateSearchSession(_currentQuery, MaxResults, existingIds);
        }

        if (_searchSession != null && _searchSession.HasMore)
        {
            try
            {
                var newTracks = await _searchSession.FetchNextBatchAsync(ScrollBatchSize, ct);

                if (ct.IsCancellationRequested) return [];

                if (newTracks.Count > 0 && LibService.Data.EnableSearchCache)
                {
                    // Кэшируем результаты
                    var allTracks = AllItems.Concat(newTracks).ToList();
                    _ = _searchCache.SetAsync(_currentQuery, allTracks);

                    // Предзагрузка картинок
                    var imageUrls = newTracks.Take(10).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                    _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                }

                if (!_searchSession.HasMore)
                {
                    SetCanFetchMore(false);
                }

                return newTracks;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = L["Search_NetworkError"];
                return [];
            }
        }

        return [];
    }

    #endregion

    #region Private Methods

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

        // [FIX] Отмена активных задач
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        // [FIX] Сброс сессии
        _searchSession?.Dispose();
        _searchSession = null;

        CancelLoading();
        IsLoading = true;
        ErrorMessage = null;
        IsFromCache = false;

        // [FIX] Очистка текущих элементов и вызов Dispose для них
        ClearItems();

        HasResults = false;
        _currentQuery = SearchQuery.Trim();
        LibService.Data.LastSearchQuery = _currentQuery;
        AddToHistory(_currentQuery);
        LibService.Save();

        try
        {
            await Task.Delay(50, ct); // UI breathing room

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
            // Ignore
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
        // 1. Кэш
        if (!forceNetwork && LibService.Data.EnableSearchCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, minCount: 20);
            if (ct.IsCancellationRequested) return;

            if (cached != null && cached.Count >= 20)
            {
                IsFromCache = true;
                await InitializeItemsAsync(cached, canFetchMore: cached.Count < MaxResults);
                HasResults = true;
                
                var urls = cached.Take(20).Select(t => t.ThumbnailUrl);
                _ = _imageCache.PrefetchAsync(urls!, ct);
                return;
            }
        }

        // 2. Сеть
        IsFetchingFromNetwork = true;
        IsFromCache = false;

        if (forceNetwork) _searchCache.InvalidateQuery(_currentQuery);

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
            var urls = tracks.Take(20).Select(t => t.ThumbnailUrl);
            _ = _imageCache.PrefetchAsync(urls!, ct);
        }

        bool hasMore = session?.HasMore ?? false;
        await InitializeItemsAsync(tracks, canFetchMore: hasMore);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = L["Search_NoResults"];
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
        LibService.Data.SearchHistory = RecentSearches.ToList();
        LibService.Save();
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        _ = _audio.StartQueueAsync(Items.Select(x => x.Track), track);
        LibService.AddToRecentlyPlayed(track);
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            // [FIX] Отменяем и очищаем всё специфичное для поиска
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;

            _searchSession?.Dispose();
            _searchSession = null;
        }

        // [FIX] Вызов базового Dispose для очистки Items
        base.Dispose(disposing);

        _isDisposed = true;
    }

    #endregion
}