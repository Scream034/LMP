// Features/Home/HomeViewModel.cs
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace LMP.Features.Home;

public sealed class HomeViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    #region Constants
    private const int DefaultBatchSize = 30;
    private const int DefaultPrefetch = 20;
    #endregion

    #region Fields
    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly TrackViewModelFactory _vmFactory;
    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentQuery = "";
    private int _fetchOffset = 0;
    private CancellationTokenSource? _categoryCts;
    private bool _isDisposed;
    #endregion

    #region Properties
    protected override int BatchSize => DefaultBatchSize;
    protected override int PrefetchThreshold => DefaultPrefetch;

    [Reactive] public string Greeting { get; private set; } = string.Empty;
    [Reactive] public bool ShowDebugInfo { get; set; }
    [Reactive] public CategoryItem? SelectedCategory { get; set; }

    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public DebugStats Stats { get; } = new();

    public ReadOnlyObservableCollection<TrackItemViewModel> ActiveTracks => Items;
    #endregion

    #region Commands
    public ReactiveCommand<Unit, bool> ToggleDebugCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    #endregion

    #region Constructor

    public HomeViewModel(
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

        UpdateGreeting();

        _languageChangedHandler = (_, _) => InitializeCategories();
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        InitializeCategories();

        ToggleDebugCommand = ReactiveCommand.Create(() => ShowDebugInfo = !ShowDebugInfo);
        RefreshCommand = ReactiveCommand.CreateFromTask(async () => await LoadTracksAsync(force: true));

        this.WhenAnyValue(x => x.SelectedCategory)
            .WhereNotNull()
            .Skip(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await LoadTracksAsync());

        _ = LoadTracksAsync();
    }

    #endregion

    #region Overrides & Filter Implementation

    /// <summary>
    /// Фильтрация только по тексту (без типа контента).
    /// </summary>
    protected override bool FilterItem(TrackInfo item, string query)
        => TrackFilters.MatchesTitleOrAuthor(item, query);

    /// <summary>
    /// Создаёт ViewModel для трека.
    /// TrackViewModelFactory автоматически получает канонический экземпляр через TrackRegistry.
    /// </summary>
    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        // Фабрика сама вызывает TrackRegistry.RegisterOrUpdate() внутри!
        // Больше не нужна ручная синхронизация IsLiked/IsDownloaded
        return _vmFactory.GetOrCreate(track, PlayWithContext);
    }

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (SelectedCategory?.IsSpecial == true) return [];

        _fetchOffset += 50;

        var newTracks = await _youtube.SearchAsync(_currentQuery, _fetchOffset + 50);

        if (ct.IsCancellationRequested) return [];

        var result = newTracks.Skip(TotalCount).ToList();

        if (result.Count > 0)
        {
            _ = _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, [.. GetItemsSnapshot(), .. result]);

            var imageUrls = result.Take(10).Select(static t => t.ThumbnailUrl).Where(static u => !string.IsNullOrEmpty(u));
            _ = _imageCache.PrefetchAsync(imageUrls, ct);
        }
        UpdateStats();
        return result;
    }
    #endregion

    #region Private Methods

    private async Task LoadTracksAsync(bool force = false)
    {
        var category = SelectedCategory;
        if (category == null) return;

        _categoryCts?.Cancel();
        _categoryCts?.Dispose();
        _categoryCts = new CancellationTokenSource();
        var ct = _categoryCts.Token;

        ClearItems();
        _fetchOffset = 0;

        try
        {
            await Task.Delay(50, ct);

            List<TrackInfo> tracks;
            if (category.IsSpecial)
            {
                tracks = await LibService.GetRecentlyPlayedAsync(100);
                if (ct.IsCancellationRequested) return;
                await InitializeItemsAsync(tracks, canFetchMore: false);
            }
            else
            {
                _currentQuery = category.Query;

                var cached = await _searchCache.GetAsync(_currentQuery, SearchSource.YouTube, 30);

                if (cached != null && cached.Count > 0 && !force)
                {
                    tracks = cached;
                    if (ct.IsCancellationRequested) return;
                    _ = RefreshCacheInBackgroundAsync(ct);
                }
                else
                {
                    tracks = await _youtube.SearchAsync(_currentQuery, 100);

                    if (ct.IsCancellationRequested) return;

                    if (tracks.Count > 0)
                    {
                        _ = _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, tracks);
                    }
                }

                var imageUrls = tracks.Take(20).Select(static t => t.ThumbnailUrl).Where(static u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                if (ct.IsCancellationRequested) return;

                await InitializeItemsAsync(tracks, canFetchMore: true);
            }
            UpdateStats();
        }
        catch (Exception ex)
        {
            Log.Info($"Load error: {ex.Message}");
        }
    }

    private async Task RefreshCacheInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(3000, ct);
            var fresh = await _youtube.SearchAsync(_currentQuery, 100);
            if (ct.IsCancellationRequested) return;

            if (fresh.Count > 0)
            {
                await _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, fresh);
            }
        }
        catch { }
    }

    private void PlayWithContext(TrackInfo track)
    {
        Task.Run(async () => await _audio.PlayTrackAsync(track));
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    private void UpdateGreeting()
    {
        var hour = DateTime.Now.Hour;
        var key = hour switch
        {
            < 12 => "Home_Greeting_Morning",
            < 18 => "Home_Greeting_Afternoon",
            _ => "Home_Greeting_Evening"
        };
        Greeting = SL[key];
    }

    private void InitializeCategories()
    {
        var currentQuery = SelectedCategory?.Query;
        var wasSpecial = SelectedCategory?.IsSpecial == true;

        Categories.Clear();

        AddCat("Category_RecentlyPlayed", "Recently Played", special: true);
        AddCat("Category_Trending", "Trending", "trending music 2024");
        AddCat("Category_Pop", "Pop", "pop hits 2024");
        AddCat("Category_HipHop", "Hip-Hop", "hip hop 2024");
        AddCat("Category_Electronic", "Electronic", "electronic music");
        AddCat("Category_LoFi", "Lo-Fi", "lofi hip hop chill beats");
        AddCat("Category_Rock", "Rock", "rock music");

        if (wasSpecial) SelectedCategory = Categories.FirstOrDefault(c => c.IsSpecial);
        else if (!string.IsNullOrEmpty(currentQuery)) SelectedCategory = Categories.FirstOrDefault(c => c.Query == currentQuery);

        if (SelectedCategory == null) SelectedCategory = Categories.FirstOrDefault();
    }

    private void AddCat(string key, string fallback, string query = "", bool special = false)
    {
        var name = SL[key];
        if (string.IsNullOrEmpty(name) || name == key) name = fallback;

        Categories.Add(new CategoryItem
        {
            Name = name,
            Query = query,
            IsSpecial = special,
            LocKey = key
        });
    }

    private void UpdateStats()
    {
        var cacheStats = _searchCache.GetStats();
        Stats.TotalTracks = TotalCount;
        Stats.DisplayedTracks = Items.Count;
        Stats.CachedTracks = cacheStats.DiskItems;
        Stats.MemoryUsage = $"{GC.GetTotalMemory(false) / 1024 / 1024} MB";
    }

    #endregion

    #region IDisposable Implementation

    public new void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;

        _categoryCts?.Cancel();
        _categoryCts?.Dispose();
        CancelLoading();

        foreach (var item in Items)
        {
            item.Dispose();
        }

        base.Dispose();
    }

    #endregion
}

public class CategoryItem
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public bool IsSpecial { get; set; }
    public string LocKey { get; set; } = "";
}

public class DebugStats : ReactiveObject
{
    [Reactive] public int TotalTracks { get; set; }
    [Reactive] public int DisplayedTracks { get; set; }
    [Reactive] public int CachedTracks { get; set; }
    [Reactive] public string MemoryUsage { get; set; } = "0 MB";
}