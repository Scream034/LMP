using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
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

    // Для фильтрации дубликатов (храним ID всех загруженных треков)
    private readonly HashSet<string> _loadedTrackIds = new();

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool HasMoreItems { get; private set; } = true; // Скрыть кнопку, если пусто
    [Reactive] public string Greeting { get; private set; } = string.Empty;

    public ObservableCollection<CategoryItem> Categories { get; } = new();
    [Reactive] public CategoryItem? SelectedCategory { get; set; }
    public ObservableCollection<TrackItemViewModel> ActiveTracks { get; } = new();

    public ReactiveCommand<CategoryItem, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

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

        RefreshCommand = ReactiveCommand.CreateFromTask<CategoryItem>(async (cat) =>
        {
            if (cat != null) SelectedCategory = cat;
            await Task.CompletedTask;
        });

        LoadMoreCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!IsLoading && HasMoreItems)
                await LoadCategoryDataAsync(SelectedCategory!, reset: false);
        });

        // Реакция на смену категории
        this.WhenAnyValue(x => x.SelectedCategory)
            .Skip(1)
            .Subscribe(async cat =>
            {
                if (cat != null) await LoadCategoryDataAsync(cat, reset: true);
            });

        SelectedCategory = Categories.FirstOrDefault();
    }

    private void InitializeCategories()
    {
        Categories.Add(new CategoryItem { Name = "Recently Played", IsSpecial = true });
        Categories.Add(new CategoryItem { Name = "Trending", Query = "trending music" });
        Categories.Add(new CategoryItem { Name = "My Mix", Query = "My Supermix" }); // Youtube Mix
        Categories.Add(new CategoryItem { Name = "Lo-Fi", Query = "lofi hip hop radio" });
        Categories.Add(new CategoryItem { Name = "Phonk", Query = "best phonk music" });
        Categories.Add(new CategoryItem { Name = "Rock", Query = "rock hits" });
        Categories.Add(new CategoryItem { Name = "Jazz", Query = "jazz relaxing" });
    }

    private void UpdateGreeting()
    {
        var h = DateTime.Now.Hour;
        Greeting = h switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
    }

    private async Task LoadCategoryDataAsync(CategoryItem category, bool reset)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            if (reset)
            {
                ActiveTracks.Clear();
                _loadedTrackIds.Clear();
                HasMoreItems = true;
            }

            List<TrackInfo> rawTracks = new();

            // 1. ПОЛУЧАЕМ НАСТРОЙКИ
            int loadCount = _library.Data.LoadBatchSize; // Берем из настроек
            bool useSmoothLoading = _library.Data.EnableSmoothLoading; // Берем из настроек

            // Защита от странных значений
            if (loadCount < 5) loadCount = 5;
            if (loadCount > 100) loadCount = 100;

            if (category.Name == "Recently Played")
            {
                if (reset)
                {
                    // Для локальной истории грузим x2 от батча, чтобы заполнить экран
                    rawTracks = _library.GetRecentlyPlayed(loadCount * 2);
                    HasMoreItems = false;
                }
            }
            else
            {
                string query = string.IsNullOrEmpty(category.Query) ? category.Name : category.Query;

                if (reset)
                {
                    if (category.Name == "Trending")
                        rawTracks = await _youtube.GetTrendingAsync(loadCount);
                    else
                        rawTracks = await _youtube.SearchAsync(query, loadCount);
                }
                else
                {
                    if (ActiveTracks.Count > 0)
                    {
                        var seedTrack = ActiveTracks[Random.Shared.Next(Math.Max(0, ActiveTracks.Count - 5), ActiveTracks.Count)].Track;
                        rawTracks = await _youtube.GetRadioAsync(seedTrack, loadCount);
                    }
                }
            }

            // Фильтрация дубликатов
            var uniqueNewTracks = new List<TrackInfo>();
            foreach (var t in rawTracks)
            {
                if (!_loadedTrackIds.Contains(t.Id))
                {
                    _loadedTrackIds.Add(t.Id);
                    uniqueNewTracks.Add(t);
                }
            }

            if (uniqueNewTracks.Count == 0 && !reset)
            {
                HasMoreItems = false;
            }
            else
            {
                // 2. ИСПОЛЬЗУЕМ НАСТРОЙКУ АНИМАЦИИ
                if (useSmoothLoading)
                {
                    // Плавная загрузка
                    foreach (var track in uniqueNewTracks)
                    {
                        var vm = CreateTrackVM(track);
                        ActiveTracks.Add(vm);
                        await Task.Delay(reset ? 10 : 25); // Задержка для эффекта
                    }
                }
                else
                {
                    // Моментальная загрузка
                    // Лучше использовать AddRange, если коллекция поддерживает, или цикл без await
                    foreach (var track in uniqueNewTracks)
                    {
                        var vm = CreateTrackVM(track);
                        ActiveTracks.Add(vm);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load Error: {ex.Message}");
            HasMoreItems = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private TrackItemViewModel CreateTrackVM(TrackInfo track)
    {
        // Синхронизация с библиотекой (лайки, скачано)
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
                // При клике на трек в Home - очищаем очередь и играем этот трек,
                // а остальные из списка добавляем в очередь
                _audio.ClearQueue();
                _audio.PlayTrack(t);

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