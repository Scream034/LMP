using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class HomeViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly TrackViewModelFactory _vmFactory;

    private string _currentQuery = "";
    private int _fetchOffset = 0;

    protected override int BatchSize => 30;
    protected override int LoadDelayMs => 150;
    protected override int PrefetchThreshold => 20;

    [Reactive] public string Greeting { get; private set; } = string.Empty;
    [Reactive] public bool ShowDebugInfo { get; set; }
    [Reactive] public CategoryItem? SelectedCategory { get; set; }

    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public DebugStats Stats { get; } = new();
    public Avalonia.Collections.AvaloniaList<TrackItemViewModel> ActiveTracks => Items;

    public ReactiveCommand<Unit, bool> ToggleDebugCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public HomeViewModel(
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

        UpdateGreeting();
        InitializeCategories();

        ToggleDebugCommand = ReactiveCommand.Create(() => ShowDebugInfo = !ShowDebugInfo);
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadTracksAsync);

        this.WhenAnyValue(x => x.SelectedCategory)
            .WhereNotNull()
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await LoadTracksAsync());

        _ = LoadTracksAsync();
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        if (_library.HasTrack(track.Id))
        {
            var existing = _library.GetTrack(track.Id);
            if (existing != null)
            {
                track.IsDownloaded = existing.IsDownloaded;
                track.LocalPath = existing.LocalPath;
                track.IsLiked = existing.IsLiked;
            }
        }

        // Use Factory
        return _vmFactory.GetOrCreate(track, PlayWithContext);
    }
    
    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (SelectedCategory?.IsSpecial == true) return [];
        _fetchOffset += 50;
        var newTracks = await _youtube.SearchAsync(_currentQuery, _fetchOffset + 50);
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

    private async Task LoadTracksAsync()
    {
        var category = SelectedCategory;
        if (category == null) return;

        IsLoading = true;
        ClearItems();
        _fetchOffset = 0;

        try
        {
            List<TrackInfo> tracks;
            if (category.IsSpecial && category.Name == "Recently Played")
            {
                tracks = _library.GetRecentlyPlayed(100);
                await InitializeItemsAsync(tracks, canFetchMore: false);
            }
            else
            {
                _currentQuery = category.Query;
                var cached = await _searchCache.GetAsync(_currentQuery, 30);

                if (cached != null && cached.Count > 0)
                {
                    tracks = cached;
                    _ = RefreshCacheInBackgroundAsync();
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
            UpdateStats();
        }
        catch (Exception ex)
        {
            Log.Info($"Load error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            IsFetchingFromNetwork = false;
        }
    }

    private async Task RefreshCacheInBackgroundAsync()
    {
        try
        {
            await Task.Delay(3000, LoadCancellationToken);
            var fresh = await _youtube.SearchAsync(_currentQuery, 100);
            if (fresh.Count > 0)
            {
                AppendItems(fresh);
                await _searchCache.SetAsync(_currentQuery, AllItems.ToList());
            }
        }
        catch { }
    }

    private void PlayWithContext(TrackInfo track)
    {
        _audio.ClearQueue();
        _ = _audio.PlayTrackAsync(track);
        
        // Add all current items to queue, but efficiently
        var tracks = Items.Select(x => x.Track).ToList();
        _audio.EnqueueRange(tracks);
        
        _library.AddToRecentlyPlayed(track);
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
        Categories.Add(new CategoryItem { Name = "Recently Played", IsSpecial = true });
        Categories.Add(new CategoryItem { Name = "Trending", Query = "trending music 2024" });
        Categories.Add(new CategoryItem { Name = "Pop", Query = "pop hits 2024" });
        Categories.Add(new CategoryItem { Name = "Hip-Hop", Query = "hip hop 2024" });
        Categories.Add(new CategoryItem { Name = "Electronic", Query = "electronic music" });
        Categories.Add(new CategoryItem { Name = "Lo-Fi", Query = "lofi hip hop chill beats" });
        Categories.Add(new CategoryItem { Name = "Rock", Query = "rock music" });
        SelectedCategory = Categories.FirstOrDefault();
    }

    private void UpdateStats()
    {
        var cacheStats = _searchCache.GetStats();
        Stats.TotalTracks = TotalCount;
        Stats.DisplayedTracks = Items.Count;
        Stats.CachedTracks = cacheStats.DiskItems;
        Stats.MemoryUsage = $"{GC.GetTotalMemory(false) / 1024 / 1024} MB";
    }

    public void Dispose()
    {
        CancelLoading();
        GC.SuppressFinalize(this);
    }
}

public class CategoryItem
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public bool IsSpecial { get; set; }
}

public class DebugStats : ReactiveObject
{
    [Reactive] public int TotalTracks { get; set; }
    [Reactive] public int DisplayedTracks { get; set; }
    [Reactive] public int CachedTracks { get; set; }
    [Reactive] public string MemoryUsage { get; set; } = "0 MB";
}