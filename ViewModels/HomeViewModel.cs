// ViewModels/HomeViewModel.cs
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly GoogleAuthService _auth;

    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public string Greeting { get; private set; } = string.Empty;

    public ObservableCollection<TrackItemViewModel> RecentTracks { get; } = new();
    public ObservableCollection<TrackItemViewModel> Recommendations { get; } = new();
    public ObservableCollection<TrackItemViewModel> Trending { get; } = new();

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

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

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadDataAsync);
        RefreshCommand.Execute().Subscribe();
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

    private async Task LoadDataAsync()
    {
        IsLoading = true;

        try
        {
            // Load recent tracks
            RecentTracks.Clear();
            var recent = _library.GetRecentlyPlayed(10);
            foreach (var track in recent)
            {
                RecentTracks.Add(CreateTrackVM(track));
            }

            // Load recommendations or trending
            Recommendations.Clear();
            var recs = _auth.IsAuthenticated
                ? await _youtube.GetPersonalRecommendationsAsync(10)
                : await _youtube.GetTrendingAsync(10);

            foreach (var track in recs)
            {
                Recommendations.Add(CreateTrackVM(track));
            }

            // Load trending
            Trending.Clear();
            var trending = await _youtube.GetTrendingAsync(10);
            foreach (var track in trending)
            {
                Trending.Add(CreateTrackVM(track));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Home load error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private TrackItemViewModel CreateTrackVM(TrackInfo track)
    {
        return new TrackItemViewModel(track, _audio, _library, _downloads,
            onPlay: t =>
            {
                _audio.PlayTrack(t);
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