using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.ViewModels;

public class SearchViewModel : ViewModelBase
{
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private CancellationTokenSource? _searchCts;

    [Reactive] public string SearchQuery { get; set; } = string.Empty;
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool HasResults { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }
    [Reactive] public string? DetectedType { get; private set; }

    public ObservableCollection<TrackItemViewModel> Results { get; } = new();

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }

    public SearchViewModel(
        YoutubeProvider youtube,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads)
    {
        _youtube = youtube;
        _audio = audio;
        _library = library;
        _downloads = downloads;

        // Инициализация команд сразу
        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteSearchAsync);

        ClearCommand = ReactiveCommand.Create(() =>
        {
            SearchQuery = string.Empty;
            Results.Clear();
            HasResults = false;
            ErrorMessage = null;
        });

        var hasResults = this.WhenAnyValue(x => x.HasResults);
        PlayAllCommand = ReactiveCommand.Create(() =>
        {
            if (Results.Count > 0)
            {
                _audio.ClearQueue();
                foreach (var item in Results)
                {
                    _audio.Enqueue(item.Track);
                }
            }
        }, hasResults);

        // Подписка на изменение текста поиска
        this.WhenAnyValue(x => x.SearchQuery)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Where(q => !string.IsNullOrWhiteSpace(q) && q.Length >= 2)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SearchCommand.Execute().Subscribe());
    }

    private async Task ExecuteSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        IsLoading = true;
        ErrorMessage = null;
        Results.Clear();

        try
        {
            var queryType = _youtube.DetectQueryType(SearchQuery);
            DetectedType = queryType.ToString();

            List<TrackInfo> tracks;

            switch (queryType)
            {
                case QueryType.DirectUrl:
                    var singleTrack = await _youtube.GetTrackByUrlAsync(SearchQuery);
                    tracks = singleTrack != null ? new List<TrackInfo> { singleTrack } : new List<TrackInfo>();
                    break;
                case QueryType.Playlist:
                    var playlistResult = await _youtube.GetPlaylistAsync(SearchQuery);
                    tracks = playlistResult?.Tracks ?? new List<TrackInfo>();
                    break;
                case QueryType.Search:
                    tracks = await _youtube.SearchAsync(SearchQuery);
                    break;
                default:
                    tracks = new List<TrackInfo>();
                    break;
            }

            foreach (var track in tracks)
            {
                // Sync with library
                if (_library.HasTrack(track.Id))
                {
                    var existing = _library.GetTrack(track.Id);
                    if (existing != null)
                    {
                        track.IsLiked = existing.IsLiked;
                        track.IsDownloaded = existing.IsDownloaded;
                    }
                }

                Results.Add(new TrackItemViewModel(
                    track, _audio, _library, _downloads,
                    onPlay: t => PlayTrackWithContext(t),
                    onRadio: t => StartRadio(t)));
            }

            HasResults = Results.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void PlayTrackWithContext(TrackInfo track)
    {
        _audio.ClearQueue();
        _audio.PlayTrack(track);

        // Add remaining tracks to queue
        bool found = false;
        foreach (var item in Results)
        {
            if (found)
                _audio.Enqueue(item.Track);
            if (item.Track.Id == track.Id)
                found = true;
        }

        _library.AddToRecentlyPlayed(track);
    }

    private async void StartRadio(TrackInfo track)
    {
        var radioTracks = await _youtube.GetRadioAsync(track);
        if (radioTracks.Count > 0)
        {
            _audio.ClearQueue();
            _audio.EnqueueRange(radioTracks);
        }
    }
}