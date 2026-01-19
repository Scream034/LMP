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

public class CategoryItem
{
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public bool IsSpecial { get; set; }
}

public class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly PipedProvider _piped;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MemoryMonitor _memoryMonitor;

    // Кэширование
    private List<TrackInfo> _cachedTracks = [];
    private int _displayedCount = 0;
    private string _currentCategoryKey = "";
    private readonly HashSet<string> _loadedTrackIds = [];
    private CancellationTokenSource? _loadCts;

    // UI State
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsLoadingMore { get; private set; }
    [Reactive] public bool IsPrefetching { get; private set; }
    [Reactive] public bool HasMoreItems { get; private set; } = true;
    [Reactive] public bool WaitingForMore { get; private set; }
    [Reactive] public string Greeting { get; private set; } = string.Empty;

    // Debug Stats
    [Reactive] public LoadingStats Stats { get; private set; } = new();
    [Reactive] public bool ShowDebugInfo { get; set; } = true;

    public ObservableCollection<CategoryItem> Categories { get; } = [];
    [Reactive] public CategoryItem? SelectedCategory { get; set; }
    public ObservableCollection<TrackItemViewModel> ActiveTracks { get; } = [];

    public ReactiveCommand<CategoryItem, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDebugCommand { get; }

    private const int PREFETCH_COUNT = 60;
    private const int PREFETCH_THRESHOLD = 15;

    public HomeViewModel(
        YoutubeProvider youtube,
        PipedProvider piped,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        MemoryMonitor memoryMonitor)
    {
        _youtube = youtube;
        _piped = piped;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _memoryMonitor = memoryMonitor;

        UpdateGreeting();
        InitializeCategories();

        _memoryMonitor.OnStatsUpdated += _ => UpdateStatsDisplay();

        RefreshCommand = ReactiveCommand.CreateFromTask<CategoryItem>(async cat =>
        {
            if (cat != null) await LoadCategoryDataAsync(cat, reset: true);
        });

        LoadMoreCommand = ReactiveCommand.CreateFromTask(
            LoadMoreAsync,
            this.WhenAnyValue(
                x => x.IsLoading,
                x => x.IsLoadingMore,
                x => x.HasMoreItems,
                x => x.WaitingForMore,
                (l, lm, h, w) => !l && !lm && h && !w));

        // ИСПРАВЛЕНО: Возвращаем Unit, а не bool
        ToggleDebugCommand = ReactiveCommand.Create(() =>
        {
            ShowDebugInfo = !ShowDebugInfo;
        });

        this.WhenAnyValue(x => x.SelectedCategory)
            .Skip(1)
            .Where(cat => cat != null)
            .Subscribe(async cat => await LoadCategoryDataAsync(cat!, reset: true));

        SelectedCategory = Categories.FirstOrDefault();
    }

    private void InitializeCategories()
    {
        Categories.Add(new CategoryItem { Name = "Recently Played", IsSpecial = true });
        Categories.Add(new CategoryItem { Name = "Trending", Query = "trending music 2024" });
        Categories.Add(new CategoryItem { Name = "My Mix", Query = "youtube mix playlist" });
        Categories.Add(new CategoryItem { Name = "Lo-Fi", Query = "lofi hip hop chill beats" });
        Categories.Add(new CategoryItem { Name = "Phonk", Query = "phonk drift music" });
        Categories.Add(new CategoryItem { Name = "Rock", Query = "rock classics hits" });
        Categories.Add(new CategoryItem { Name = "Jazz", Query = "smooth jazz relaxing" });
    }

    private void UpdateGreeting()
    {
        var h = DateTime.Now.Hour;
        Greeting = h switch { < 12 => L["Home_Greeting_Morning"], < 18 => L["Home_Greeting_Afternoon"], _ => L["Home_Greeting_Evening"] };
    }

    private async Task LoadCategoryDataAsync(CategoryItem category, bool reset)
    {
        if (IsLoading) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var sw = Stopwatch.StartNew();
        var categoryKey = category.Query ?? category.Name;

        IsLoading = true;
        WaitingForMore = false;

        try
        {
            ActiveTracks.Clear();
            _loadedTrackIds.Clear();
            _cachedTracks.Clear();
            _displayedCount = 0;
            _currentCategoryKey = categoryKey;
            HasMoreItems = true;

            Debug.WriteLine($"[Home] Loading: '{category.Name}'");
            string source;

            if (category.Name == "Recently Played")
            {
                _cachedTracks = _library.GetRecentlyPlayed(100);
                source = "library";
                HasMoreItems = false;
            }
            else
            {
                // ===== СТРАТЕГИЯ ЗАГРУЗКИ =====

                // 1. Проверяем disk cache
                var cached = await _searchCache.GetAsync(categoryKey, PREFETCH_COUNT / 2);

                if (cached != null && cached.Count > 0)
                {
                    _cachedTracks = cached;
                    source = $"disk-cache ({cached.Count})";
                    Debug.WriteLine($"[Home] Loaded from disk cache");

                    // Обновляем кэш в фоне
                    _ = RefreshCacheInBackgroundAsync(categoryKey, ct);
                }
                else
                {
                    // 2. Пробуем Piped (быстро!)
                    List<TrackInfo> pipedTracks;

                    if (category.Name == "Trending")
                        pipedTracks = await _piped.GetTrendingAsync("US", PREFETCH_COUNT, ct);
                    else
                        pipedTracks = await _piped.SearchAsync(categoryKey, PREFETCH_COUNT, ct);

                    if (pipedTracks.Count > 0)
                    {
                        _cachedTracks = pipedTracks;
                        source = $"piped ({sw.ElapsedMilliseconds}ms)";

                        // Сохраняем в кэш
                        _ = _searchCache.SetAsync(categoryKey, pipedTracks);
                    }
                    else
                    {
                        // 3. Fallback на yt-dlp
                        Debug.WriteLine($"[Home] Piped failed, using yt-dlp");
                        _cachedTracks = await _youtube.SearchAsync(categoryKey, PREFETCH_COUNT);
                        source = $"yt-dlp ({sw.ElapsedMilliseconds}ms)";

                        if (_cachedTracks.Count > 0)
                        {
                            _ = _searchCache.SetAsync(categoryKey, _cachedTracks);
                        }
                    }
                }
            }

            sw.Stop();
            UpdateStats(source, sw.ElapsedMilliseconds, _cachedTracks.Count);

            if (_cachedTracks.Count > 0)
            {
                IsLoading = false;

                // Предзагрузка изображений
                var imageUrls = _cachedTracks.Take(20).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                await ShowNextBatchAsync();
            }
            else
            {
                HasMoreItems = false;
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[Home] Load cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Home] Load error: {ex.Message}");
            HasMoreItems = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_displayedCount < _cachedTracks.Count)
        {
            await ShowNextBatchAsync();
            return;
        }

        if (SelectedCategory?.Name == "Recently Played")
        {
            HasMoreItems = false;
            return;
        }

        WaitingForMore = true;
        Debug.WriteLine($"[Home] Cache empty, fetching more...");

        try
        {
            await FetchMoreTracksAsync();

            if (_displayedCount < _cachedTracks.Count)
            {
                await ShowNextBatchAsync();
            }
            else
            {
                HasMoreItems = false;
            }
        }
        finally
        {
            WaitingForMore = false;
        }
    }

    private async Task ShowNextBatchAsync()
    {
        if (_displayedCount >= _cachedTracks.Count)
        {
            HasMoreItems = _cachedTracks.Count > 0 && SelectedCategory?.Name != "Recently Played";
            return;
        }

        IsLoadingMore = true;
        var sw = Stopwatch.StartNew();

        try
        {
            int batchSize = Math.Clamp(_library.Data.LoadBatchSize, 5, 50);
            bool smooth = _library.Data.EnableSmoothLoading;

            var nextBatch = _cachedTracks
                .Skip(_displayedCount)
                .Take(batchSize)
                .Where(t => !_loadedTrackIds.Contains(t.Id))
                .ToList();

            // Предзагрузка изображений следующего батча
            var nextImageUrls = _cachedTracks
                .Skip(_displayedCount + batchSize)
                .Take(batchSize)
                .Select(t => t.ThumbnailUrl)
                .Where(u => !string.IsNullOrEmpty(u));
            _ = _imageCache.PrefetchAsync(nextImageUrls!);

            foreach (var track in nextBatch)
            {
                _loadedTrackIds.Add(track.Id);
                var vm = CreateTrackVM(track);
                ActiveTracks.Add(vm);

                if (smooth) await Task.Delay(15);
            }

            _displayedCount += batchSize;
            sw.Stop();

            UpdateStats("batch", sw.ElapsedMilliseconds, ActiveTracks.Count);

            int remaining = _cachedTracks.Count - _displayedCount;
            HasMoreItems = remaining > 0 || SelectedCategory?.Name != "Recently Played";

            // Фоновая подгрузка
            if (remaining < PREFETCH_THRESHOLD && !IsPrefetching && SelectedCategory?.Name != "Recently Played")
            {
                _ = FetchMoreTracksAsync();
            }
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task FetchMoreTracksAsync()
    {
        if (IsPrefetching) return;
        IsPrefetching = true;

        try
        {
            Debug.WriteLine($"[Home] Prefetching more tracks...");
            var sw = Stopwatch.StartNew();

            var existingIds = new HashSet<string>(_cachedTracks.Select(t => t.Id));
            List<TrackInfo> newTracks;

            // Пробуем Piped
            var piped = await _piped.SearchAsync(_currentCategoryKey, PREFETCH_COUNT);
            if (piped.Count > 0)
            {
                newTracks = piped.Where(t => !existingIds.Contains(t.Id)).ToList();
            }
            else
            {
                // Fallback на yt-dlp
                var ytdlp = await _youtube.SearchAsync(_currentCategoryKey, _cachedTracks.Count + PREFETCH_COUNT);
                newTracks = ytdlp.Where(t => !existingIds.Contains(t.Id)).ToList();
            }

            if (newTracks.Count > 0)
            {
                _cachedTracks.AddRange(newTracks);
                HasMoreItems = true;
                Debug.WriteLine($"[Home] Prefetched {newTracks.Count} tracks in {sw.ElapsedMilliseconds}ms");

                // Обновляем disk cache
                _ = _searchCache.SetAsync(_currentCategoryKey, _cachedTracks);

                // Предзагрузка изображений
                var imageUrls = newTracks.Take(10).Select(t => t.ThumbnailUrl).Where(u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Home] Prefetch error: {ex.Message}");
        }
        finally
        {
            IsPrefetching = false;
        }
    }

    private async Task RefreshCacheInBackgroundAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(3000, ct); // Ждём 3 сек

            var fresh = await _piped.SearchAsync(query, PREFETCH_COUNT, ct);
            if (fresh.Count > 0)
            {
                var existingIds = new HashSet<string>(_cachedTracks.Select(t => t.Id));
                var newTracks = fresh.Where(t => !existingIds.Contains(t.Id)).ToList();

                if (newTracks.Count > 0)
                {
                    _cachedTracks.AddRange(newTracks);
                    Debug.WriteLine($"[Home] Background refresh: +{newTracks.Count} tracks");
                }

                await _searchCache.SetAsync(query, _cachedTracks);
            }
        }
        catch { }
    }

    private void UpdateStats(string source, long timeMs, int count)
    {
        var cacheStats = _searchCache.GetStats();
        var imgStats = _imageCache.GetStats();

        Stats = new LoadingStats
        {
            TotalTracks = _cachedTracks.Count,
            DisplayedTracks = count,
            CachedTracks = cacheStats.DiskItems,
            LastBatchTimeMs = timeMs,
            Source = source,
            MemoryUsage = _memoryMonitor.GetFormattedStats()
        };
    }

    private void UpdateStatsDisplay()
    {
        Stats = Stats with { MemoryUsage = _memoryMonitor.GetFormattedStats() };
    }

    private TrackItemViewModel CreateTrackVM(TrackInfo track)
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

        return new TrackItemViewModel(track, _audio, _library, _downloads,
            onPlay: t =>
            {
                _audio.ClearQueue();
                _ = _audio.PlayTrackAsync(t);

                bool found = false;
                foreach (var vm in ActiveTracks)
                {
                    if (found) _audio.Enqueue(vm.Track);
                    if (vm.Track.Id == t.Id) found = true;
                }
                _library.AddToRecentlyPlayed(t);
            });
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}