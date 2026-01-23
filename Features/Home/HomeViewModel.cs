using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using MyLiteMusicPlayer.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.Features.Home;

/// <summary>
/// ViewModel для домашней страницы с категориями и треками.
/// </summary>
public sealed class HomeViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    #region Constants

    private const int DefaultBatchSize = 30;
    private const int DefaultLoadDelay = 150;
    private const int DefaultPrefetch = 20;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly TrackViewModelFactory _vmFactory;

    // [FIX] Явный делегат для отписки
    private readonly EventHandler<string> _languageChangedHandler;
    
    private string _currentQuery = "";
    private int _fetchOffset = 0;
    private CancellationTokenSource? _categoryCts;
    private bool _isDisposed;

    #endregion

    #region Properties

    protected override int BatchSize => DefaultBatchSize;
    protected override int LoadDelayMs => DefaultLoadDelay;
    protected override int PrefetchThreshold => DefaultPrefetch;

    [Reactive] public string Greeting { get; private set; } = string.Empty;
    [Reactive] public bool ShowDebugInfo { get; set; }
    [Reactive] public CategoryItem? SelectedCategory { get; set; }

    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public DebugStats Stats { get; } = new();
    public Avalonia.Collections.AvaloniaList<TrackItemViewModel> ActiveTracks => Items;

    #endregion

    #region Commands

    public ReactiveCommand<Unit, bool> ToggleDebugCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    #endregion

    #region Constructors

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

        // [FIX] Инициализация и подписка
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

    #region Overrides

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        if (LibService.HasTrack(track.Id))
        {
            var existing = LibService.GetTrack(track.Id);
            if (existing != null)
            {
                track.IsDownloaded = existing.IsDownloaded;
                track.LocalPath = existing.LocalPath;
                track.IsLiked = existing.IsLiked;
            }
        }
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
            var allTracks = AllItems.Concat(result).ToList();
            _ = _searchCache.SetAsync(_currentQuery, allTracks);
            var imageUrls = result.Take(10).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
            _ = _imageCache.PrefetchAsync(imageUrls!, ct);
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

        // Отмена предыдущей загрузки
        _categoryCts?.Cancel();
        _categoryCts?.Dispose();
        _categoryCts = new CancellationTokenSource();
        var ct = _categoryCts.Token;

        IsLoading = true;
        ClearItems();
        _fetchOffset = 0;

        try
        {
            await Task.Delay(50, ct); // Debounce

            List<TrackInfo> tracks;
            if (category.IsSpecial)
            {
                tracks = LibService.GetRecentlyPlayed(100);
                if (ct.IsCancellationRequested) return;
                await InitializeItemsAsync(tracks, canFetchMore: false);
            }
            else
            {
                _currentQuery = category.Query;
                var cached = await _searchCache.GetAsync(_currentQuery, 30);

                if (cached != null && cached.Count > 0 && !force)
                {
                    tracks = cached;
                    if (ct.IsCancellationRequested) return;
                    _ = RefreshCacheInBackgroundAsync(ct);
                }
                else
                {
                    IsFetchingFromNetwork = true;
                    tracks = await _youtube.SearchAsync(_currentQuery, 100);
                    IsFetchingFromNetwork = false;

                    if (ct.IsCancellationRequested) return;

                    if (tracks.Count > 0) _ = _searchCache.SetAsync(_currentQuery, tracks);
                }

                var imageUrls = tracks.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                if (ct.IsCancellationRequested) return;

                await InitializeItemsAsync(tracks, canFetchMore: true);
            }
            UpdateStats();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Info($"Load error: {ex.Message}");
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

    private async Task RefreshCacheInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(3000, ct);
            var fresh = await _youtube.SearchAsync(_currentQuery, 100);
            if (ct.IsCancellationRequested) return;
            if (fresh.Count > 0) await _searchCache.SetAsync(_currentQuery, fresh);
        }
        catch { }
    }

    private void PlayWithContext(TrackInfo track)
    {
        var tracks = Items.Select(x => x.Track).ToList();
        _ = _audio.StartQueueAsync(tracks, track);
        LibService.AddToRecentlyPlayed(track);
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
        Greeting = L[key];
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
        var name = L[key];
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

        // [FIX] Отписка от событий
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;

        _categoryCts?.Cancel();
        _categoryCts?.Dispose();
        CancelLoading();

        // Вызов базового Dispose для очистки Items
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