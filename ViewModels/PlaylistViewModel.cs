using Avalonia.Controls;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class PlaylistViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly IDialogService _dialog;
    private readonly TrackViewModelFactory _vmFactory;

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public string FormattedDuration { get; set; } = "";

    [Reactive] public bool CanEdit { get; private set; }
    [Reactive] public bool IsCloud { get; private set; }
    [Reactive] public bool IsReadOnly { get; private set; }

    [Reactive] public bool IsPlayingThisPlaylist { get; private set; }
    [Reactive] public bool IsShuffleActive { get; private set; }
    [Reactive] public bool IsDownloadingActive { get; private set; }

    // Свойство для биндинга высоты RowDefinition
    private GridLength _headerHeight;
    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            this.RaiseAndSetIfChanged(ref _headerHeight, value);

            // Сохраняем только если значение валидное и в пикселях
            if (value.IsAbsolute && value.Value > 50)
            {
                if (Math.Abs(_library.Data.PlaylistHeaderHeight - value.Value) > 1)
                {
                    _library.Data.PlaylistHeaderHeight = value.Value;
                    _library.Save();
                }
            }
        }
    }

    private string _currentPlaylistId = "";
    private IDisposable? _librarySubscription;
    private IDisposable? _audioStateSub;
    private IDisposable? _trackChangeSub;

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DeletePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadToCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlinkFromCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MergePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public PlaylistViewModel(
      LibraryService library,
      AudioEngine audio,
      DownloadService downloads,
      MusicLibraryManager manager,
      IDialogService dialog,
      TrackViewModelFactory vmFactory)
    {
        _library = library;
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _dialog = dialog;
        _vmFactory = vmFactory;

        LocalizationService.Instance.LanguageChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));

        // Инициализация состояния Shuffle
        IsShuffleActive = _audio.ShuffleEnabled;

        // Загружаем сохраненную высоту
        _headerHeight = new GridLength(_library.Data.PlaylistHeaderHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = ReactiveCommand.Create(PlayAll, hasTracks);

        DeletePlaylistCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var confirmTitle = L["Dialog_Confirm_Title"];
            var confirmMsg = string.Format(L["Playlist_DeleteConfirm"], PlaylistName);

            if (await _dialog.ConfirmAsync(confirmTitle, confirmMsg))
            {
                await _manager.DeletePlaylistAsync(_currentPlaylistId);
            }
        });

        UploadToCloudCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.UploadPlaylistToAccountAsync(_currentPlaylistId);
            LoadPlaylist(_currentPlaylistId);
        });

        UnlinkFromCloudCommand = ReactiveCommand.Create(() =>
        {
            _manager.ConvertToLocal(_currentPlaylistId);
            LoadPlaylist(_currentPlaylistId);
        });

        RefreshPlaylistCommand = ReactiveCommand.Create(() => LoadPlaylist(_currentPlaylistId));

        ShufflePlayCommand = ReactiveCommand.Create(ToggleShuffle, hasTracks);
        DownloadAllCommand = ReactiveCommand.Create(DownloadAll, hasTracks);
        MergePlaylistCommand = ReactiveCommand.CreateFromTask(MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit));

        AddToQueueCommand = ReactiveCommand.Create(() =>
        {
            _audio.EnqueueRange(AllItems);
        }, hasTracks);

        // ПОДПИСКИ НА ОБНОВЛЕНИЯ БИБЛИОТЕКИ
        _librarySubscription = Observable.FromEvent(
                h => _library.OnDataChanged += h,
                h => _library.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!string.IsNullOrEmpty(_currentPlaylistId))
                {
                    LoadPlaylist(_currentPlaylistId);
                }
            });

        // ПОДПИСКИ НА АУДИО ДВИЖОК ДЛЯ ОБНОВЛЕНИЯ UI
        _audioStateSub = Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => CheckPlaybackState());

        _trackChangeSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => CheckPlaybackState());
    }

    private void CheckPlaybackState()
    {
        // Логика: если играет трек, который есть в этом плейлисте - считаем, что плейлист активен
        // Это упрощенная логика, но достаточная для UX
        if (_audio.CurrentTrack != null && _audio.IsPlaying)
        {
            IsPlayingThisPlaylist = _library.IsTrackInPlaylist(_audio.CurrentTrack.Id, _currentPlaylistId);
        }
        else
        {
            IsPlayingThisPlaylist = false;
        }
    }

    private void PlayAll()
    {
        if (AllItems.Count == 0) return;

        // Если плейлист уже играет - это пауза/возобновление
        if (IsPlayingThisPlaylist)
        {
            _ = _audio.SetPlaybackStateAsync(false); // Пауза
            return;
        }

        // Если трек из плейлиста на паузе - возобновляем
        if (_audio.CurrentTrack != null &&
            _audio.IsPaused &&
            _library.IsTrackInPlaylist(_audio.CurrentTrack.Id, _currentPlaylistId))
        {
            _ = _audio.SetPlaybackStateAsync(true); // Play
            return;
        }

        // Иначе запускаем плейлист с начала
        _audio.ClearQueue();
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;

        _audio.EnqueueRange(AllItems);
        _ = _audio.PlayTrackAsync(AllItems[0]);
    }

    private void ToggleShuffle()
    {
        if (AllItems.Count == 0) return;

        // 1. Очищаем очередь и добавляем все треки плейлиста
        _audio.ClearQueue();
        _audio.EnqueueRange(AllItems);

        // 2. Перемешиваем очередь
        _audio.ShuffleQueue();

        // 3. Запускаем воспроизведение (первый трек после перемешивания)
        var queue = _audio.Queue;
        if (queue.Count > 0)
        {
            _ = _audio.PlayTrackAsync(queue[0]);
        }

        // 4. Визуальная обратная связь
        IsShuffleActive = true;
        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false);
    }


    private void DownloadAll()
    {
        IsDownloadingActive = true;

        foreach (var track in AllItems.Where(t => !t.IsDownloaded))
        {
            _downloads.StartDownload(track);
        }

        // Сбрасываем визуальную активность кнопки через пару секунд
        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false);
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        return _vmFactory.GetOrCreate(track, PlayFromPlaylist);
    }

    public async void LoadPlaylist(string playlistId)
    {
        _currentPlaylistId = playlistId;
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsEditable;
        IsCloud = playlist.IsFromAccount;
        IsReadOnly = !playlist.IsEditable;

        var tracks = _library.GetPlaylistTracks(playlistId);
        TrackCount = tracks.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        CalculateTotalDuration(tracks);

        await InitializeItemsAsync(tracks, canFetchMore: false);
        CheckPlaybackState(); // Проверяем состояние после загрузки
    }

    private void CalculateTotalDuration(List<TrackInfo> tracks)
    {
        var totalSec = tracks.Sum(t => t.Duration.TotalSeconds);
        TotalDuration = TimeSpan.FromSeconds(totalSec);

        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;

        if (totalHours > 0)
            FormattedDuration = $"{totalHours}h {minutes}m";
        else
            FormattedDuration = $"{minutes}m {TotalDuration.Seconds}s";
    }

    private void PlayFromPlaylist(TrackInfo track)
    {
        // Вместо Clear -> Play -> EnqueueRange (что создавало дубль),
        // используем атомарную установку очереди
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;

        // Передаем ВЕСЬ список (Items - это только загруженные, AllItems - все известные)
        // Важно: PaginatedViewModel обычно держит загруженные в Items. 
        // Если плейлист огромный, здесь может потребоваться логика подгрузки, 
        // но для Lite версии берем Items или загружаем все.

        // В текущей реализации Items - это ViewModel, нам нужны модели
        var tracks = Items.Select(vm => vm.Track).ToList();

        _ = _audio.StartQueueAsync(tracks, track);

        _library.AddToRecentlyPlayed(track);
    }

    private async Task MergePlaylistAsync()
    {
        var otherPlaylists = _library.GetAllPlaylists()
            .Where(p => p.Id != _currentPlaylistId && p.IsLocal)
            .ToList();

        if (otherPlaylists.Count == 0)
        {
            await _dialog.ShowInfoAsync(
                L["Dialog_Merge_NoTarget_Title"],
                L["Dialog_Merge_NoTarget_Msg"]);
            return;
        }

        var targetId = otherPlaylists.First().Id;
        if (!string.IsNullOrEmpty(targetId))
        {
            if (_library.MergePlaylists(_currentPlaylistId, targetId))
                await _dialog.ShowInfoAsync(L["Dialog_Success"], L["Merge_Success_Msg"]);
            else
                await _dialog.ShowInfoAsync(L["Dialog_Error"], L["Merge_Error_Msg"]);
        }
    }

    public void Dispose()
    {
        _librarySubscription?.Dispose();
        _audioStateSub?.Dispose();
        _trackChangeSub?.Dispose();
        CancelLoading();
        GC.SuppressFinalize(this);
    }
}