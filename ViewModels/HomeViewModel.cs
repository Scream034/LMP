using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.ViewModels;

public class CategoryItem
{
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public bool IsSpecial { get; set; }
}

public class HomeViewModel : ViewModelBase
{
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly GoogleAuthService _auth;

    // ===== КЭШИРОВАНИЕ =====
    private List<TrackInfo> _cachedTracks = new();      // Все загруженные треки
    private int _displayedCount = 0;                     // Сколько уже показано
    private string _currentCategoryKey = "";             // Ключ текущей категории
    private readonly HashSet<string> _loadedTrackIds = new(); // Для фильтрации дубликатов

    // Для отмены фоновой загрузки при смене категории
    private CancellationTokenSource? _prefetchCts;

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsLoadingMore { get; private set; } // Отдельный флаг для Load More
    [Reactive] public bool HasMoreItems { get; private set; } = true;
    [Reactive] public string Greeting { get; private set; } = string.Empty;
    [Reactive] public string LoadingStatus { get; private set; } = ""; // Для отладки

    public ObservableCollection<CategoryItem> Categories { get; } = new();
    [Reactive] public CategoryItem? SelectedCategory { get; set; }
    public ObservableCollection<TrackItemViewModel> ActiveTracks { get; } = new();

    public ReactiveCommand<CategoryItem, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    // Константы
    private const int PREFETCH_COUNT = 60;  // Сколько загружать за раз с YouTube
    private const int PREFETCH_THRESHOLD = 15; // Когда начинать подгрузку в фоне

    public HomeViewModel(
        YoutubeProvider youtube,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        GoogleAuthService auth)
    {
        _youtube = youtube;
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _auth = auth;

        UpdateGreeting();
        InitializeCategories();

        RefreshCommand = ReactiveCommand.CreateFromTask<CategoryItem>(async cat =>
        {
            if (cat != null)
            {
                SelectedCategory = cat;
                await LoadCategoryDataAsync(cat, reset: true);
            }
        });

        // Load More — теперь моментальный!
        LoadMoreCommand = ReactiveCommand.CreateFromTask(
            async () => await ShowNextBatchAsync(),
            this.WhenAnyValue(x => x.IsLoading, x => x.IsLoadingMore, x => x.HasMoreItems,
                (loading, loadingMore, hasMore) => !loading && !loadingMore && hasMore));

        // Реакция на смену категории
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
        Categories.Add(new CategoryItem { Name = "My Mix", Query = "My Supermix" });
        Categories.Add(new CategoryItem { Name = "Lo-Fi", Query = "lofi hip hop radio beats" });
        Categories.Add(new CategoryItem { Name = "Phonk", Query = "best phonk music drift" });
        Categories.Add(new CategoryItem { Name = "Rock", Query = "rock hits classic" });
        Categories.Add(new CategoryItem { Name = "Jazz", Query = "jazz relaxing smooth" });
    }

    private void UpdateGreeting()
    {
        var h = DateTime.Now.Hour;
        Greeting = h switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
    }

    /// <summary>
    /// Загрузка категории. При reset=true — полная перезагрузка с сервера.
    /// </summary>
    private async Task LoadCategoryDataAsync(CategoryItem category, bool reset)
    {
        if (IsLoading) return;

        var sw = Stopwatch.StartNew();
        var categoryKey = category.Query ?? category.Name;

        // Если это та же категория и не reset — просто показываем следующую порцию
        if (!reset && categoryKey == _currentCategoryKey && _cachedTracks.Count > _displayedCount)
        {
            await ShowNextBatchAsync();
            return;
        }

        // ===== ПОЛНАЯ ПЕРЕЗАГРУЗКА =====
        IsLoading = true;
        LoadingStatus = "Connecting to YouTube...";

        // Отменяем предыдущую фоновую загрузку
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();

        try
        {
            // Очистка
            ActiveTracks.Clear();
            _loadedTrackIds.Clear();
            _cachedTracks.Clear();
            _displayedCount = 0;
            _currentCategoryKey = categoryKey;
            HasMoreItems = true;

            Debug.WriteLine($"[Home] Loading category: '{category.Name}' (query: '{categoryKey}')");
            LoadingStatus = $"Searching: {category.Name}...";

            // ===== ОДИН БОЛЬШОЙ ЗАПРОС (предзагрузка) =====
            int prefetchCount = category.Name == "Recently Played"
                ? 100  // Локальные данные — можно больше
                : PREFETCH_COUNT;

            if (category.Name == "Recently Played")
            {
                _cachedTracks = _library.GetRecentlyPlayed(prefetchCount);
                Debug.WriteLine($"[Home] Loaded {_cachedTracks.Count} recent tracks from library");
            }
            else if (category.Name == "Trending")
            {
                _cachedTracks = await _youtube.GetTrendingAsync(prefetchCount);
                Debug.WriteLine($"[Home] Loaded {_cachedTracks.Count} trending tracks");
            }
            else
            {
                _cachedTracks = await _youtube.SearchAsync(categoryKey, prefetchCount);
                Debug.WriteLine($"[Home] Loaded {_cachedTracks.Count} search results");
            }

            sw.Stop();
            Debug.WriteLine($"[Home] Fetch completed in {sw.ElapsedMilliseconds}ms");
            LoadingStatus = $"Loaded {_cachedTracks.Count} tracks in {sw.ElapsedMilliseconds}ms";

            // Показываем первую порцию
            if (_cachedTracks.Count > 0)
            {
                IsLoading = false; // Скрываем главный спиннер
                await ShowNextBatchAsync();
            }
            else
            {
                HasMoreItems = false;
                LoadingStatus = "No tracks found";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Home] Load ERROR: {ex.Message}");
            LoadingStatus = $"Error: {ex.Message}";
            HasMoreItems = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Показать следующую порцию из кэша (МОМЕНТАЛЬНО!)
    /// </summary>
    private async Task ShowNextBatchAsync()
    {
        if (_displayedCount >= _cachedTracks.Count)
        {
            HasMoreItems = false;
            return;
        }

        IsLoadingMore = true;
        var sw = Stopwatch.StartNew();

        try
        {
            int batchSize = _library.Data.LoadBatchSize;
            if (batchSize < 5) batchSize = 10;
            if (batchSize > 50) batchSize = 50;

            bool smoothLoading = _library.Data.EnableSmoothLoading;

            // Берём следующую порцию из кэша
            var nextBatch = _cachedTracks
                .Skip(_displayedCount)
                .Take(batchSize)
                .Where(t => !_loadedTrackIds.Contains(t.Id))
                .ToList();

            Debug.WriteLine($"[Home] Showing batch: {nextBatch.Count} tracks (from cache position {_displayedCount})");

            foreach (var track in nextBatch)
            {
                _loadedTrackIds.Add(track.Id);
                var vm = CreateTrackVM(track);
                ActiveTracks.Add(vm);

                if (smoothLoading)
                {
                    await Task.Delay(20); // Плавное появление
                }
            }

            _displayedCount += batchSize; // Увеличиваем даже если часть была отфильтрована

            // Проверяем, нужно ли подгружать ещё
            int remaining = _cachedTracks.Count - _displayedCount;
            HasMoreItems = remaining > 0;

            Debug.WriteLine($"[Home] Batch shown in {sw.ElapsedMilliseconds}ms. Remaining in cache: {remaining}");

            // ===== ФОНОВАЯ ПОДГРУЗКА =====
            // Если осталось мало и это не локальная категория — подгружаем ещё
            if (remaining < PREFETCH_THRESHOLD &&
                SelectedCategory?.Name != "Recently Played" &&
                _prefetchCts != null && !_prefetchCts.IsCancellationRequested)
            {
                _ = PrefetchMoreAsync(_prefetchCts.Token);
            }
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    /// <summary>
    /// Фоновая подгрузка дополнительных треков
    /// </summary>
    private async Task PrefetchMoreAsync(CancellationToken ct)
    {
        try
        {
            Debug.WriteLine($"[Home] Prefetching more tracks in background...");

            // Делаем запрос с бо́льшим offset
            int newFetchCount = _cachedTracks.Count + PREFETCH_COUNT;

            List<TrackInfo> moreTracks;

            if (SelectedCategory?.Name == "Trending")
            {
                moreTracks = await _youtube.GetTrendingAsync(newFetchCount);
            }
            else
            {
                moreTracks = await _youtube.SearchAsync(_currentCategoryKey, newFetchCount);
            }

            if (ct.IsCancellationRequested) return;

            // Добавляем только новые
            var existingIds = new HashSet<string>(_cachedTracks.Select(t => t.Id));
            var newTracks = moreTracks.Where(t => !existingIds.Contains(t.Id)).ToList();

            if (newTracks.Count > 0)
            {
                _cachedTracks.AddRange(newTracks);
                HasMoreItems = true;
                Debug.WriteLine($"[Home] Prefetched {newTracks.Count} new tracks. Cache now: {_cachedTracks.Count}");
            }
            else
            {
                Debug.WriteLine($"[Home] Prefetch returned 0 new tracks");
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[Home] Prefetch cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Home] Prefetch error: {ex.Message}");
        }
    }

    private TrackItemViewModel CreateTrackVM(TrackInfo track)
    {
        // Синхронизация с библиотекой
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
                _audio.PlayTrack(t);

                // Добавляем остальные в очередь
                bool found = false;
                foreach (var vm in ActiveTracks)
                {
                    if (found) _audio.Enqueue(vm.Track);
                    if (vm.Track.Id == t.Id) found = true;
                }
                _library.AddToRecentlyPlayed(t);
            });
    }
}