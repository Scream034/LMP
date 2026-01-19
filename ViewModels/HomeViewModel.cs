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
    public string Query { get; set; } = ""; // Пусто для спец. категорий (Recent)
    public bool IsSpecial { get; set; }
}

public class HomeViewModel : ViewModelBase
{
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly GoogleAuthService _auth;

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public string Greeting { get; private set; } = string.Empty;

    // Категории
    public ObservableCollection<CategoryItem> Categories { get; } = new();
    [Reactive] public CategoryItem? SelectedCategory { get; set; }

    // Контент (Используем ObservableCollection для UI)
    public ObservableCollection<TrackItemViewModel> ActiveTracks { get; } = new();

    // Команды
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

        // Команда обновления (смена категории)
        RefreshCommand = ReactiveCommand.CreateFromTask<CategoryItem>(async (cat) =>
        {
            if (cat != null)
            {
                SelectedCategory = cat; 
            }
            await Task.CompletedTask;
        });

        // Команда подгрузки (Infinite Scroll)
        LoadMoreCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!IsLoading && SelectedCategory != null && !SelectedCategory.IsSpecial)
                await LoadCategoryDataAsync(SelectedCategory, reset: false);
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
        Categories.Add(new CategoryItem { Name = "My Mix", Query = "My Supermix" });
        Categories.Add(new CategoryItem { Name = "Lo-Fi", Query = "lofi hip hop radio" });
        Categories.Add(new CategoryItem { Name = "Rock", Query = "best rock music" });
        Categories.Add(new CategoryItem { Name = "Electronic", Query = "electronic dance music" });
        Categories.Add(new CategoryItem { Name = "Focus", Query = "deep focus music" });
        Categories.Add(new CategoryItem { Name = "Sleep", Query = "sleep music rain" });
        Categories.Add(new CategoryItem { Name = "Phonk", Query = "best phonk music" });
        Categories.Add(new CategoryItem { Name = "Jazz", Query = "relaxing jazz" });
    }

    private void UpdateGreeting()
    {
        var hour = DateTime.Now.Hour;
        Greeting = hour switch
        {
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };
    }

    private async Task LoadCategoryDataAsync(CategoryItem category, bool reset)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            if (reset) ActiveTracks.Clear();

            List<TrackInfo> newTracks = new();

            if (category.Name == "Recently Played")
            {
                if (reset)
                {
                    var recent = _library.GetRecentlyPlayed(100);
                    newTracks.AddRange(recent);
                }
            }
            else if (category.Name == "My Mix" && _auth.IsAuthenticated)
            {
                var recs = await _youtube.GetPersonalRecommendationsAsync(20);
                newTracks.AddRange(recs);
            }
            else
            {
                string query = string.IsNullOrEmpty(category.Query) ? category.Name : category.Query;

                if (reset)
                {
                    if (category.Name == "Trending")
                        newTracks = await _youtube.GetTrendingAsync(25);
                    else
                        newTracks = await _youtube.SearchAsync(query, 25);
                }
                else
                {
                    if (ActiveTracks.Count > 0)
                    {
                        int skip = Math.Max(0, ActiveTracks.Count - 5);
                        var seed = ActiveTracks[skip + Random.Shared.Next(Math.Min(5, ActiveTracks.Count - skip))].Track;
                        var related = await _youtube.GetRadioAsync(seed, 15);

                        foreach (var r in related)
                        {
                            if (!ActiveTracks.Any(at => at.Track.Id == r.Id))
                                newTracks.Add(r);
                        }
                    }
                }
            }

            foreach (var track in newTracks)
            {
                if (!ActiveTracks.Any(x => x.Track.Id == track.Id))
                {
                    ActiveTracks.Add(CreateTrackVM(track));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading category {category.Name}: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
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
                _audio.PlayTrack(t);

                bool found = false;
                foreach (var vm in ActiveTracks)
                {
                    if (found) _audio.Enqueue(vm.Track);
                    if (vm.Track.Id == t.Id) found = true;
                }

                _library.AddToRecentlyPlayed(t);
            },
            onRadio: async t =>
            {
                var radio = await _youtube.GetRadioAsync(t);
                _audio.ClearQueue();
                _audio.EnqueueRange(radio);
            });
    }
}