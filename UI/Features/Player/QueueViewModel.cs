using LMP.UI.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;

using System.Reactive.Linq;

namespace LMP.UI.Features.Player;

/// <summary>
/// ViewModel панели очереди воспроизведения.
/// Не наследует TrackListPaginatedViewModel — очередь управляется AudioEngine,
/// а не загружается постранично. Имеет собственный VM-кэш изолированный от factory.
///
/// <para><b>Smart Parent:</b> реализован напрямую через _vmCache[id],
/// а не через TrackViewModelFactory.TryGet — очередные VM создаются через
/// factory.CreateForQueue и намеренно не попадают в общий factory-кэш.</para>
/// </summary>
public sealed class QueueViewModel : ViewModelBase, IFilterable
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly TrackViewModelFactory _vmFactory;
    private readonly DialogService _dialog;
    private readonly MusicLibraryManager _manager;
    private readonly LibraryService _library;

    private readonly Dictionary<string, TrackItemViewModel> _vmCache = [];
    private List<TrackInfo> _masterQueue = [];

    private TrackItemViewModel? _currentActiveVm;
    private bool _isMovingInternally;
    private volatile bool _isSuspended;
    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>True когда очередь пуста (нет треков вообще).</summary>
    [Reactive] public bool IsEmpty { get; private set; } = true;

    /// <summary>True когда очередь не пуста, но фильтр не нашёл совпадений.</summary>
    [Reactive] public bool IsFilterEmpty { get; private set; }

    [Reactive] public bool CanReorderItems { get; private set; } = true;
    [Reactive] public string FilterQuery { get; set; } = string.Empty;

    /// <summary>
    /// BatchObservableCollection: атомарная замена через ReplaceAll даёт
    /// 1 Reset-событие вместо N Add/Remove при фильтрации большой очереди.
    /// </summary>
    public BatchObservableCollection<TrackItemViewModel> QueueItems { get; } = [];

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> ClearQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<TrackItemViewModel, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<(int oldIndex, int newIndex), Unit> MoveItemCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveQueueToPlaylistCommand { get; }

    #endregion

    #region Constructor

    public QueueViewModel(
        AudioEngine audio,
        DownloadService downloads,
        DialogService dialog,
        MusicLibraryManager manager,
        LibraryService library,
        TrackViewModelFactory vmFactory)
    {
        _audio = audio;
        _downloads = downloads;
        _dialog = dialog;
        _manager = manager;
        _library = library;
        _vmFactory = vmFactory;

        ClearQueueCommand = CreateCommand(
            ReactiveCommand.Create(() => _audio.ClearQueue()));

        ShuffleQueueCommand = CreateCommand(
            ReactiveCommand.Create(() => _audio.ShuffleQueue()));

        DownloadAllCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            foreach (var item in QueueItems.Where(static x => !x.IsDownloading))
                _downloads.StartDownload(item.Track);
        }));

        RemoveTrackCommand = CreateCommand(
            ReactiveCommand.Create<TrackItemViewModel>(item => _audio.RemoveFromQueue(item.Track)));

        MoveItemCommand = CreateCommand(
            ReactiveCommand.Create<(int oldIndex, int newIndex)>(
                t => { if (CanReorderItems) MoveItem(t.oldIndex, t.newIndex); }));

        SaveQueueToPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            SaveQueueToPlaylistAsync,
            this.WhenAnyValue(x => x.IsEmpty, static empty => !empty)));

        SubscribeToAudioEngine();
        SubscribeToDownloadService();
        SubscribeToFilter();

        RefreshFromAudioEngine();
    }

    #endregion

    #region Smart Parent Subscriptions

    private void SubscribeToAudioEngine()
    {
        Observable.FromEvent(
                h => _audio.OnQueueChanged += h,
                h => _audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(80))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!_isMovingInternally)
                    RefreshFromAudioEngine();
            })
            .DisposeWith(Disposables);

        // Идентично TrackListReorderableViewModel: только SetActive, никакого Rebuild.
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(t =>
            {
                if (!_isSuspended) UpdatePlaybackState(t, _audio.IsPlaying);
            })
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<bool, bool>, (bool isPlaying, bool isPaused)>(
                h => (a, b) => h((a, b)),
                h => _audio.OnPlaybackStateChanged += h,
                h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (!_isSuspended) UpdatePlaybackState(_audio.CurrentTrack, x.isPlaying);
            })
            .DisposeWith(Disposables);
    }

    private void SubscribeToDownloadService()
    {
        Observable.FromEvent<Action<string, float>, (string id, float progress)>(
                h => (id, p) => h((id, p)),
                h => _downloads.OnProgress += h,
                h => _downloads.OnProgress -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (!_isSuspended) _vmCache.GetValueOrDefault(x.id)?.SetDownloadState(true, x.progress);
            })
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<string, bool, string?>, (string id, bool ok, string? path)>(
                h => (id, ok, path) => h((id, ok, path)),
                h => _downloads.OnCompleted += h,
                h => _downloads.OnCompleted -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (!_isSuspended) _vmCache.GetValueOrDefault(x.id)?.SetDownloadState(false, 0f);
            })
            .DisposeWith(Disposables);
    }

    private void SubscribeToFilter()
    {
        this.WhenAnyValue(x => x.FilterQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery);
                RebuildVisibleItems();
            })
            .DisposeWith(Disposables);
    }

    #endregion

    #region Smart Parent Updates — O(1)

    /// <summary>
    /// O(1): lookup через _vmCache по ID.
    /// Идентично паттерну в TrackListReorderableViewModel.UpdatePlaybackState.
    /// Единственная точка управления SetActive — никакого дублирования в Rebuild.
    /// </summary>
    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        if (_currentActiveVm != null && _currentActiveVm.Id != currentTrack?.Id)
        {
            _currentActiveVm.SetActive(false, false);
            _currentActiveVm = null;
        }

        if (currentTrack is null) return;

        _currentActiveVm ??= _vmCache.GetValueOrDefault(currentTrack.Id);
        _currentActiveVm?.SetActive(true, isPlaying);
    }

    #endregion

    #region Queue Management

    /// <summary>
    /// Обновляет очередь из AudioEngine только если изменился состав треков.
    /// Guard по ID-списку предотвращает полный rebuild при смене трека/паузе —
    /// в этих случаях AudioEngine может стрельнуть OnQueueChanged, но состав не меняется.
    /// </summary>
    private void RefreshFromAudioEngine()
    {
        var newQueue = _audio.Queue.ToList();

        bool contentChanged = newQueue.Count != _masterQueue.Count;
        if (!contentChanged)
        {
            for (int i = 0; i < newQueue.Count; i++)
            {
                if (newQueue[i].Id != _masterQueue[i].Id)
                {
                    contentChanged = true;
                    break;
                }
            }
        }

        if (!contentChanged) return;

        _masterQueue = newQueue;
        RebuildVisibleItems();
    }

    /// <summary>
    /// Перестраивает видимый список с учётом фильтра.
    /// SetActive намеренно отсутствует — активное состояние управляется исключительно
    /// через UpdatePlaybackState (O(1) lookup), как в TrackListReorderableViewModel.
    /// </summary>
    private void RebuildVisibleItems()
    {
        var query = FilterQuery;
        bool hasFilter = !string.IsNullOrWhiteSpace(query);

        var newItems = new List<TrackItemViewModel>(_masterQueue.Count);
        var usedIds = new HashSet<string>(_masterQueue.Count, StringComparer.Ordinal);

        foreach (var track in _masterQueue)
        {
            if (hasFilter && !TrackFilters.MatchesTitleOrAuthor(track, query))
                continue;

            if (!_vmCache.TryGetValue(track.Id, out var vm))
            {
                vm = _vmFactory.CreateForQueue(track, PlayFromQueue);
                _vmCache[track.Id] = vm;
            }

            newItems.Add(vm);
            usedIds.Add(track.Id);
        }

        if (usedIds.Count < _vmCache.Count)
        {
            List<string>? toRemove = null;
            foreach (var kvp in _vmCache)
            {
                if (!usedIds.Contains(kvp.Key))
                {
                    toRemove ??= new List<string>(_vmCache.Count - usedIds.Count);
                    toRemove.Add(kvp.Key);
                }
            }

            if (toRemove is not null)
            {
                foreach (var key in toRemove)
                {
                    _vmCache[key].Dispose();
                    _vmCache.Remove(key);
                }
            }
        }

        QueueItems.ReplaceAll(newItems);

        IsEmpty = _masterQueue.Count == 0;
        IsFilterEmpty = !IsEmpty && hasFilter && QueueItems.Count == 0;

        // После rebuild восстанавливаем активное состояние через единственный путь.
        UpdatePlaybackState(_audio.CurrentTrack, _audio.IsPlaying);
    }

    private void MoveItem(int oldIdx, int newIdx)
    {
        if (oldIdx == newIdx
            || oldIdx < 0 || oldIdx >= QueueItems.Count
            || newIdx < 0 || newIdx >= QueueItems.Count)
            return;

        if (!string.IsNullOrWhiteSpace(FilterQuery))
        {
            Log.Warn("[Queue] Cannot reorder with active filter");
            return;
        }

        var movingTrack = QueueItems[oldIdx].Track;

        try
        {
            _isMovingInternally = true;
            QueueItems.Move(oldIdx, newIdx);
            _masterQueue.RemoveAt(oldIdx);
            _masterQueue.Insert(newIdx, movingTrack);
            _audio.MoveQueueItem(oldIdx, newIdx);
        }
        finally
        {
            _isMovingInternally = false;
        }
    }

    private void PlayFromQueue(TrackInfo track) => _ = _audio.PlayTrackAsync(track);

    #endregion

    #region Lifecycle (Suspend/Resume)

    protected override void OnSuspend()
    {
        _isSuspended = true;
        Log.Debug("[QueueVM] Suspended");
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        RefreshFromAudioEngine();
        Log.Debug("[QueueVM] Resumed");
    }

    #endregion

    #region Commands Implementation

    private async Task SaveQueueToPlaylistAsync()
    {
        if (_masterQueue.Count == 0) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result is null || string.IsNullOrWhiteSpace(result.Name)) return;

        var playlist = await _library.CreatePlaylistAsync(result.Name.Trim());

        foreach (var track in _masterQueue)
            await _manager.AddTrackToPlaylistAsync(playlist.Id, track);

        Log.Info($"[Queue] Saved {_masterQueue.Count} tracks to playlist '{result.Name}'");
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[QueueVM] Disposing");

            foreach (var vm in _vmCache.Values)
                vm.Dispose();

            _vmCache.Clear();
            QueueItems.Clear();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}