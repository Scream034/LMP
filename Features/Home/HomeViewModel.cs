using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;

using System.Reactive.Linq;

namespace LMP.Features.Home;

/// <summary>
/// ViewModel главного экрана. Категории + поиск через YouTube с кэшированием.
/// Smart Parent (трек-активность, прогресс загрузки) унаследован от TrackListPaginatedViewModel.
/// </summary>
public sealed class HomeViewModel : TrackListPaginatedViewModel
{
    #region Constants

    private const int DefaultBatchSize = 30;
    private const int DefaultPrefetch = 20;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentQuery = "";
    private int _fetchOffset;
    private CancellationTokenSource? _categoryCts;
    private bool _isDisposed;

    #endregion

    #region Properties

    protected override int BatchSize => DefaultBatchSize;
    protected override int PrefetchThreshold => DefaultPrefetch;

    [Reactive] public bool IsContentReady { get; private set; }
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
        DownloadService downloads,
        TrackViewModelFactory vmFactory)
        : base(audio, downloads, vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        UpdateGreeting();

        _languageChangedHandler = (_, _) =>
        {
            UpdateGreeting();
            InitializeCategories();
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        InitializeCategories();

        ToggleDebugCommand = CreateCommand(
            ReactiveCommand.Create(() => ShowDebugInfo = !ShowDebugInfo));

        RefreshCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(async () => await LoadTracksAsync(force: true)));

        this.WhenAnyValue(x => x.SelectedCategory)
            .WhereNotNull()
            .Skip(1)
            .Where(_ => IsContentReady)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await LoadTracksAsync())
            .DisposeWith(Disposables);
    }

    #endregion

    #region Navigation

    /// <summary>
    /// Вызывается из MainWindowViewModel после CrossFade-анимации.
    /// Первая тяжёлая загрузка запускается здесь, не в конструкторе.
    /// </summary>
    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        await LoadTracksAsync();
        IsContentReady = true;
    }

    #endregion

    #region TrackListPaginatedViewModel Implementation

    protected override void OnPlay(TrackInfo track)
    {
        Task.Run(async () => await Audio.PlayTrackAsync(track));
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (SelectedCategory?.IsSpecial == true) return [];

        _fetchOffset += 50;
        var newTracks = await _youtube.SearchAsync(_currentQuery, _fetchOffset + 50);
        if (ct.IsCancellationRequested) return [];

        var result = newTracks.Skip(TotalCount).ToList();

        if (result.Count > 0)
        {
            var snapshot = GetItemsSnapshot();
            var all = new List<TrackInfo>(snapshot.Count + result.Count);
            all.AddRange(snapshot);
            all.AddRange(result);
            _ = _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, all);

            var imageUrls = result.Take(10)
                .Select(static t => t.ThumbnailUrl)
                .Where(static u => !string.IsNullOrEmpty(u));
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
        if (category is null) return;

        _categoryCts?.Cancel();
        _categoryCts?.Dispose();
        _categoryCts = new CancellationTokenSource();
        var ct = _categoryCts.Token;

        ClearItems();
        _fetchOffset = 0;

        try
        {
            await Task.Delay(50, ct);

            if (category.IsSpecial)
            {
                var recent = await LibService.GetRecentlyPlayedAsync(100);
                if (ct.IsCancellationRequested) return;
                await InitializeItemsAsync(recent, canFetchMore: false);
            }
            else
            {
                _currentQuery = category.Query;

                var cached = !force
                    ? await _searchCache.GetAsync(_currentQuery, SearchSource.YouTube, 30)
                    : null;

                List<TrackInfo> tracks;

                if (cached is { Count: > 0 })
                {
                    tracks = cached;
                    _ = RefreshCacheInBackgroundAsync(ct);
                }
                else
                {
                    tracks = await _youtube.SearchAsync(_currentQuery, 100);
                    if (ct.IsCancellationRequested) return;

                    if (tracks.Count > 0)
                        _ = _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, tracks);
                }

                var imageUrls = tracks.Take(20)
                    .Select(static t => t.ThumbnailUrl)
                    .Where(static u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                if (ct.IsCancellationRequested) return;
                await InitializeItemsAsync(tracks, canFetchMore: true);
            }

            UpdateStats();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[HomeVM] Load error: {ex.Message}");
        }
    }

    private async Task RefreshCacheInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(3000, ct);
            var fresh = await _youtube.SearchAsync(_currentQuery, 100);
            if (ct.IsCancellationRequested || fresh.Count == 0) return;
            await _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, fresh);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[HomeVM] Background cache refresh failed: {ex.Message}");
        }
    }

    private void UpdateGreeting()
    {
        var key = DateTime.Now.Hour switch
        {
            < 12 => "Home_Greeting_Morning",
            < 18 => "Home_Greeting_Afternoon",
            _ => "Home_Greeting_Evening"
        };
        Greeting = SL[key];
    }

    /// <summary>
    /// In-place обновление имён категорий.
    /// Clear() + Add() даёт 14 CollectionChanged → сброс SelectedCategory → лишний Load.
    /// In-place: только PropertyChanged на отдельных полях, порядок и ссылки сохранены.
    /// </summary>
    private void InitializeCategories()
    {
        ReadOnlySpan<(string key, string fallback, string query, bool special)> defs =
        [
            ("Category_RecentlyPlayed", "Recently Played", "",                         true),
            ("Category_Trending",       "Trending",        "trending music 2024",      false),
            ("Category_Pop",            "Pop",             "pop hits 2024",            false),
            ("Category_HipHop",         "Hip-Hop",         "hip hop 2024",             false),
            ("Category_Electronic",     "Electronic",      "electronic music",         false),
            ("Category_LoFi",           "Lo-Fi",           "lofi hip hop chill beats", false),
            ("Category_Rock",           "Rock",            "rock music",               false),
        ];

        for (int i = Categories.Count; i < defs.Length; i++)
            Categories.Add(new CategoryItem());

        for (int i = 0; i < defs.Length; i++)
        {
            var (key, fallback, query, special) = defs[i];
            var name = SL[key];
            if (string.IsNullOrEmpty(name) || name == key) name = fallback;

            var cat = Categories[i];
            cat.Name = name;
            cat.Query = query;
            cat.IsSpecial = special;
            cat.LocKey = key;
        }

        while (Categories.Count > defs.Length)
            Categories.RemoveAt(Categories.Count - 1);

        SelectedCategory ??= Categories.FirstOrDefault();
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

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[HomeVM] Disposing");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _categoryCts?.Cancel();
            _categoryCts?.Dispose();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}

public sealed class CategoryItem : ReactiveObject
{
    [Reactive] public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public bool IsSpecial { get; set; }
    public string LocKey { get; set; } = "";
}

public sealed class DebugStats : ReactiveObject
{
    [Reactive] public int TotalTracks { get; set; }
    [Reactive] public int DisplayedTracks { get; set; }
    [Reactive] public int CachedTracks { get; set; }
    [Reactive] public string MemoryUsage { get; set; } = "0 MB";
}