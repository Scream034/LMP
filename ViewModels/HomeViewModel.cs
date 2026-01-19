// ViewModels/HomeViewModel.cs
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly DownloadService _downloads;

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
    public ObservableCollection<TrackItemViewModel> ActiveTracks => Items;

    public ReactiveCommand<Unit, bool> ToggleDebugCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public HomeViewModel(
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _audio = audio;
        _library = library;
        _downloads = downloads;

        UpdateGreeting();
        InitializeCategories();

        ToggleDebugCommand = ReactiveCommand.Create(() => ShowDebugInfo = !ShowDebugInfo);
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadTracksAsync);

        // Реакция на смену категории
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
        // Синхронизируем с библиотекой
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

        return new TrackItemViewModel(track, _audio, _library, _downloads, PlayWithContext);
    }

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (SelectedCategory?.IsSpecial == true) return [];

        var sw = Stopwatch.StartNew();
        _fetchOffset += 50;

        var newTracks = await _youtube.SearchAsync(_currentQuery, _fetchOffset + 50);

        // Берём только новые (после текущего offset)
        var result = newTracks.Skip(TotalCount).ToList();

        Debug.WriteLine($"[Home] Fetched {result.Count} more tracks in {sw.ElapsedMilliseconds}ms");

        // Обновляем кэш
        if (result.Count > 0)
        {
            var allTracks = AllItems.Concat(result).ToList();
            _ = _searchCache.SetAsync(_currentQuery, allTracks);

            // Предзагрузка изображений
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

        var sw = Stopwatch.StartNew();

        try
        {
            List<TrackInfo> tracks;
            string source;

            if (category.IsSpecial && category.Name == "Recently Played")
            {
                tracks = _library.GetRecentlyPlayed(100);
                source = "library";
                await InitializeItemsAsync(tracks, canFetchMore: false);
            }
            else
            {
                _currentQuery = category.Query;

                // 1. Проверяем кэш
                var cached = await _searchCache.GetAsync(_currentQuery, 30);

                if (cached != null && cached.Count > 0)
                {
                    tracks = cached;
                    source = $"cache ({cached.Count})";
                    Debug.WriteLine($"[Home] Loaded from cache: {cached.Count} tracks");

                    // Обновляем кэш в фоне
                    _ = RefreshCacheInBackgroundAsync();
                }
                else
                {
                    // 2. Загружаем из сети
                    IsFetchingFromNetwork = true;
                    tracks = await _youtube.SearchAsync(_currentQuery, 100);
                    IsFetchingFromNetwork = false;
                    source = $"network ({sw.ElapsedMilliseconds}ms)";

                    // Сохраняем в кэш
                    if (tracks.Count > 0)
                    {
                        _ = _searchCache.SetAsync(_currentQuery, tracks);
                    }
                }

                // Предзагрузка изображений
                var imageUrls = tracks.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!);

                await InitializeItemsAsync(tracks, canFetchMore: true);
            }

            Debug.WriteLine($"[Home] Loaded {tracks.Count} tracks from {source}");
            UpdateStats();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Home] Load error: {ex.Message}");
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
                AppendItems(fresh); // Добавит только новые (дедупликация по ID)
                await _searchCache.SetAsync(_currentQuery, AllItems.ToList());
                Debug.WriteLine($"[Home] Background refresh complete");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Home] Background refresh error: {ex.Message}");
        }
    }

    private void PlayWithContext(TrackInfo track)
    {
        _audio.ClearQueue();
        _ = _audio.PlayTrackAsync(track);

        bool found = false;
        foreach (var item in Items)
        {
            if (found) _audio.Enqueue(item.Track);
            if (item.Track.Id == track.Id) found = true;
        }

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