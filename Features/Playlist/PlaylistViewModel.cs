using Avalonia.Controls;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;

namespace LMP.Features.Playlist;

/// <summary>
/// ViewModel для отображения содержимого плейлиста и управления им.
/// </summary>
public sealed class PlaylistViewModel : PaginatedViewModel<TrackInfo, TrackItemViewModel>, IDisposable
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly IDialogService _dialog;
    private readonly TrackViewModelFactory _vmFactory;

    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentPlaylistId = "";
    private List<string> _allTrackIds = [];
    private int _loadedOffset = 0;

    private readonly IDisposable? _librarySubscription;
    private readonly IDisposable? _audioStateSub;
    private readonly IDisposable? _trackChangeSub;

    private bool _isMovingInternally; // Флаг для подавления полной перезагрузки
    private bool _isDisposed;

    #endregion

    #region Properties

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

    [Reactive] public bool CanReorderItems { get; private set; }

    private GridLength _headerHeight;
    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            this.RaiseAndSetIfChanged(ref _headerHeight, value);
            if (value.IsAbsolute && value.Value > 50)
            {
                if (Math.Abs(LibService.Settings.PlaylistHeaderHeight - value.Value) > 1)
                {
                    LibService.UpdateSettings(s => s.PlaylistHeaderHeight = value.Value);
                }
            }
        }
    }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DeletePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadToCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlinkFromCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MergePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    public ReactiveCommand<(int oldIndex, int newIndex), Unit> MoveItemCommand { get; }

    #endregion

    #region Constructor

    public PlaylistViewModel(
      AudioEngine audio,
      DownloadService downloads,
      MusicLibraryManager manager,
      IDialogService dialog,
      TrackViewModelFactory vmFactory)
    {
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _dialog = dialog;
        _vmFactory = vmFactory;

        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        IsShuffleActive = _audio.ShuffleEnabled;
        _headerHeight = new GridLength(LibService.Settings.PlaylistHeaderHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = ReactiveCommand.CreateFromTask(PlayAllAsync, hasTracks);

        DeletePlaylistCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var confirmTitle = SL["Dialog_Confirm_Title"];
            var confirmMsg = string.Format(SL["Playlist_DeleteConfirm"], PlaylistName);

            if (await _dialog.ConfirmAsync(confirmTitle, confirmMsg))
            {
                await _manager.DeletePlaylistAsync(_currentPlaylistId);
            }
        });

        UploadToCloudCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.UploadPlaylistToAccountAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        });

        UnlinkFromCloudCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ConvertToLocalAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        });

        RefreshPlaylistCommand = ReactiveCommand.CreateFromTask(() => LoadPlaylistAsync(_currentPlaylistId));

        ShufflePlayCommand = ReactiveCommand.Create(ToggleShuffle, hasTracks);
        DownloadAllCommand = ReactiveCommand.Create(DownloadAll, hasTracks);
        MergePlaylistCommand = ReactiveCommand.CreateFromTask(MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit));

        AddToQueueCommand = ReactiveCommand.Create(() =>
        {
            // Заменено небезопасное `AllItems` на `GetItemsSnapshot()`
            _audio.EnqueueRange(GetItemsSnapshot());
        }, hasTracks);

        MoveItemCommand = ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(async tuple =>
        {
            var (oldIdx, newIdx) = tuple;
            if (!CanReorderItems || oldIdx == newIdx) return;

            try
            {
                _isMovingInternally = true;

                // 1. Оптимистичное (мгновенное) обновление UI
                MoveSourceItem(oldIdx, newIdx);

                // 2. Сохранение изменений в сервисе
                await _manager.MovePlaylistTrackAsync(_currentPlaylistId, oldIdx, newIdx);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to move track in playlist: {ex.Message}");
                // В случае ошибки, принудительно перезагружаем плейлист, чтобы отменить
                // оптимистичное обновление
                _isMovingInternally = false;
                await LoadPlaylistAsync(_currentPlaylistId);
            }
            finally
            {
                _isMovingInternally = false;
            }
        });

        this.WhenAnyValue(x => x.CanEdit, x => x.FilterQuery, x => x.FilterType)
            .Subscribe(_ =>
            {
                CanReorderItems = CanEdit &&
                                  string.IsNullOrWhiteSpace(FilterQuery) &&
                                  FilterType == ContentFilterType.All;
            });

        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if (_isMovingInternally) return; // Игнорируем событие, если оно вызвано нами же
                if (!string.IsNullOrEmpty(_currentPlaylistId))
                {
                    await LoadPlaylistAsync(_currentPlaylistId);
                }
            });

        _audioStateSub = Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await CheckPlaybackStateAsync());

        _trackChangeSub = Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await CheckPlaybackStateAsync());
    }

    #endregion

    #region Methods

    private async Task CheckPlaybackStateAsync()
    {
        if (_audio.CurrentTrack != null && _audio.IsPlaying)
        {
            IsPlayingThisPlaylist = await LibService.IsTrackInPlaylistAsync(_audio.CurrentTrack.Id, _currentPlaylistId);
        }
        else
        {
            IsPlayingThisPlaylist = false;
        }
    }

    private async Task PlayAllAsync()
    {
        if (TrackCount == 0) return;

        if (IsPlayingThisPlaylist)
        {
            _ = _audio.SetPlaybackStateAsync(false);
            return;
        }

        if (_audio.CurrentTrack != null &&
            _audio.IsPaused &&
            await LibService.IsTrackInPlaylistAsync(_audio.CurrentTrack.Id, _currentPlaylistId))
        {
            _ = _audio.SetPlaybackStateAsync(true);
            return;
        }

        // Получаем треки (без лишних копий)
        var allTracks = await GetAllPlaylistTracksAsync();
        if (allTracks.Count == 0) return;

        _audio.ClearQueue();
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;

        // EnqueueRange теперь не делает лишних копий
        _audio.EnqueueRange(allTracks);

        // Запускаем воспроизведение в фоне
        _ = Task.Run(async () => await _audio.PlayTrackAsync(allTracks[0]));
    }

    private async void ToggleShuffle()
    {
        if (TrackCount == 0) return;

        var allTracks = await GetAllPlaylistTracksAsync();
        if (allTracks.Count == 0) return;

        _audio.ClearQueue();
        _audio.EnqueueRange(allTracks);
        _audio.ShuffleQueue();

        var queue = _audio.Queue;
        if (queue.Count > 0)
        {
            _ = _audio.PlayTrackAsync(queue[0]);
        }

        IsShuffleActive = true;
        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false);
    }

    private async void DownloadAll()
    {
        IsDownloadingActive = true;

        var allTracks = await GetAllPlaylistTracksAsync();
        foreach (var track in allTracks.Where(t => !t.IsDownloaded))
        {
            _downloads.StartDownload(track);
        }

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false);
    }

    protected override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        var vm = _vmFactory.GetOrCreate(track, PlayFromPlaylist);

        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async (t) =>
        {
            if (CanEdit)
                await _manager.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
        };

        vm.StartRadioAction = (t) => Log.Info($"Start radio requested for {t.Title}");

        return vm;
    }

    /// <summary>
    /// Загружает плейлист с поддержкой пагинации.
    /// </summary>
    public async Task LoadPlaylistAsync(string playlistId)
    {
        _currentPlaylistId = playlistId;
        _loadedOffset = 0;

        var playlist = await LibService.GetPlaylistAsync(playlistId);
        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsEditable;
        IsCloud = playlist.IsFromAccount;
        IsReadOnly = !playlist.IsEditable;

        // Получаем все ID треков для подсчёта
        _allTrackIds = await LibService.GetPlaylistTrackIdsAsync(playlistId);
        TrackCount = _allTrackIds.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        // ★ ИСПРАВЛЕНИЕ: Получаем суммарную длительность для ВСЕХ треков из БД
        TotalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId);
        FormatDuration();

        // Загружаем первую порцию треков для отображения
        var initialTracks = await LibService.GetPlaylistTracksAsync(
            playlistId,
            limit: BatchSize,
            offset: 0);

        _loadedOffset = initialTracks.Count;

        bool canFetchMore = _allTrackIds.Count > _loadedOffset;
        await InitializeItemsAsync(initialTracks, canFetchMore: canFetchMore);

        await CheckPlaybackStateAsync();
    }

    // ★ Переименовать и упростить метод форматирования
    private void FormatDuration()
    {
        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;
        int seconds = TotalDuration.Seconds;

        if (totalHours > 0)
            FormattedDuration = $"{totalHours}h {minutes}m";
        else if (minutes > 0)
            FormattedDuration = $"{minutes}m {seconds}s";
        else
            FormattedDuration = $"{seconds}s";
    }

    /// <summary>
    /// Подгрузка следующей порции треков.
    /// </summary>
    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        // Проверяем, есть ли ещё треки для загрузки
        if (_loadedOffset >= _allTrackIds.Count)
        {
            SetCanFetchMore(false);
            return [];
        }

        try
        {
            var tracks = await LibService.GetPlaylistTracksAsync(
                _currentPlaylistId,
                limit: BatchSize,
                offset: _loadedOffset,
                ct);

            if (ct.IsCancellationRequested) return [];

            _loadedOffset += tracks.Count;

            // Обновляем расчёт длительности
            if (tracks.Count > 0)
            {
                var allLoaded = GetItemsSnapshot();
                allLoaded.AddRange(tracks);
            }

            // Проверяем, загрузили ли всё
            if (_loadedOffset >= _allTrackIds.Count)
            {
                SetCanFetchMore(false);
            }

            return tracks;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] FetchMore error: {ex.Message}");
            SetCanFetchMore(false);
            return [];
        }
    }

    /// <summary>
    /// Получает все треки плейлиста для воспроизведения.
    /// </summary>
    private async Task<List<TrackInfo>> GetAllPlaylistTracksAsync()
    {
        var loadedTracks = GetItemsSnapshot();
        if (loadedTracks.Count >= TrackCount)
            return loadedTracks;

        return await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId,
            limit: TrackCount,
            offset: 0);
    }

    /// <summary>
    /// Воспроизводит трек из плейлиста, заменяя очередь ВСЕМИ треками плейлиста.
    /// </summary>
    private async void PlayFromPlaylist(TrackInfo track)
    {
        _audio.ShuffleEnabled = false;
        IsShuffleActive = false;

        // КЛЮЧЕВОЕ ИЗМЕНЕНИЕ: Всегда заменяем очередь всеми треками плейлиста
        var allTracks = await GetAllPlaylistTracksAsync();

        // Запускаем очередь с выбранного трека
        await _audio.StartQueueAsync(allTracks, track);

        _ = LibService.AddToRecentlyPlayedAsync(track);
    }
    private async Task MergePlaylistAsync()
    {
        var otherPlaylists = (await LibService.GetAllPlaylistsAsync())
            .Where(p => p.Id != _currentPlaylistId && p.IsLocal)
            .ToList();

        if (otherPlaylists.Count == 0)
        {
            await _dialog.ShowInfoAsync(
                SL["Dialog_Merge_NoTarget_Title"],
                SL["Dialog_Merge_NoTarget_Msg"]);
            return;
        }

        var targetId = otherPlaylists.First().Id;
        if (!string.IsNullOrEmpty(targetId))
        {
            if (await _manager.MergePlaylistsAsync(_currentPlaylistId, targetId))
                await _dialog.ShowInfoAsync(SL["Dialog_Success"], SL["Merge_Success_Msg"]);
            else
                await _dialog.ShowInfoAsync(SL["Dialog_Error"], SL["Merge_Error_Msg"]);
        }
    }

    #endregion

    #region Filter Implementation

    protected override bool FilterItem(TrackInfo item, string query, ContentFilterType filterType)
    {
        // 1. Text search
        if (!string.IsNullOrWhiteSpace(query))
        {
            bool matchesText = item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               item.Author.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!matchesText) return false;
        }

        // 2. Type filter
        // Логика:
        // - Music: всё, кроме явных видеоклипов от официальных каналов
        // - Video: только явные видеоклипы (официальный канал артиста + НЕ помечен как Song)
        return filterType switch
        {
            ContentFilterType.All => true,
            ContentFilterType.Music => !item.IsExplicitVideoClip,
            ContentFilterType.Video => item.IsExplicitVideoClip,
            _ => true
        };
    }

    #endregion

    #region IDisposable Implementation

    public new void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;

        _librarySubscription?.Dispose();
        _audioStateSub?.Dispose();
        _trackChangeSub?.Dispose();
        CancelLoading();

        // --- ИСПРАВЛЕНИЕ: Принудительно очищаем ViewModels треков ---
        // Items - это коллекция из базового PaginatedViewModel
        if (Items != null)
        {
            foreach (var item in Items)
            {
                item.Dispose();
            }
        }
        // -----------------------------------------------------------

        base.Dispose();
    }

    #endregion
}