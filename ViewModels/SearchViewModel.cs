using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;

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

    public ObservableCollection<TrackItemViewModel> Results { get; } = [];

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

        // Команда поиска теперь запускается вручную (кнопкой или Enter)
        // Добавлено условие: строка не должна быть пустой
        var canSearch = this.WhenAnyValue(x => x.SearchQuery,
            query => !string.IsNullOrWhiteSpace(query));

        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteSearchAsync, canSearch);

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

        // ВАЖНО: Мы убрали автоматическую подписку с Throttle.
        // Теперь поиск происходит только по явному действию пользователя.
    }

    private async Task ExecuteSearchAsync()
    {
        // Отменяем предыдущий поиск, если он был
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
                    tracks = singleTrack != null ? [singleTrack] : [];
                    break;
                case QueryType.Playlist:
                    var playlistResult = await _youtube.GetPlaylistAsync(SearchQuery);
                    tracks = playlistResult?.Tracks ?? [];
                    break;
                case QueryType.Search:
                    tracks = await _youtube.SearchAsync(SearchQuery);
                    break;
                default:
                    tracks = [];
                    break;
            }

            foreach (var track in tracks)
            {
                // Синхронизация статуса (лайк/скачано) с библиотекой
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
            if (!HasResults)
            {
                ErrorMessage = "Ничего не найдено";
            }
        }
        catch (OperationCanceledException)
        {
            // Поиск отменен, ничего не делаем
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ошибка поиска: {ex.Message}\n{ex.StackTrace}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void PlayTrackWithContext(TrackInfo track)
    {
        _audio.ClearQueue();

        // ВАЖНО: Используем Async версию, чтобы UI не зависал, если ссылка требует обновления
        await Task.Run(async () =>
        {
            await _audio.PlayTrackAsync(track);

            // Добавляем остальные треки из результатов в очередь
            bool found = false;
            foreach (var item in Results)
            {
                if (found)
                    _audio.Enqueue(item.Track);
                if (item.Track.Id == track.Id)
                    found = true;
            }

            _library.AddToRecentlyPlayed(track);
        });
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