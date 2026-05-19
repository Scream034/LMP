using Avalonia.Controls;
using Avalonia.Media;
using LMP.Core.Audio;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shell;
using LMP.Features.Shared;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LMP.UI.Dialogs;
using Microsoft.Extensions.DependencyInjection;


namespace LMP.Features.Playlist;

/// <summary>
/// ViewModel экрана плейлиста.
/// Наследует <see cref="TrackListReorderableViewModel"/> — поддерживает drag-and-drop,
/// фиксированный порядок, Smart Parent (активный трек, прогресс загрузки).
///
/// <para><b>Gradient Header:</b> цвет вычисляется из обложки через DominantColorService,
/// кэшируется в <c>ComputedColor</c>. CustomColor имеет приоритет.</para>
///
/// <para><b>Liked Playlist:</b> источник — лайкнутые треки, редактирование отключено.</para>
///
/// <para><b>Cloud Playlist:</b> синхронизация через PlaylistSyncService,
/// защита от двойного запуска через Interlocked gate.</para>
///
/// <para><b>Локальные мутации</b> (move/remove) дебаунсируют OnDataChanged
/// чтобы избежать повторного reload после собственной операции.</para>
/// </summary>
public sealed class PlaylistViewModel : TrackListReorderableViewModel
{
    #region Fields

    private readonly MusicLibraryManager _manager;
    private readonly DialogService _dialog;
    private readonly DominantColorService _dominantColor;
    private readonly MainWindowViewModel _mainWindow;
    private readonly PlaylistEditService _editService;
    private readonly PlaylistSyncService _syncService;
    private readonly PlayerControlService _playerControl;
    private readonly CookieAuthService _auth;

    private readonly EventHandler<string> _languageChangedHandler;
    private readonly IDisposable? _librarySubscription;

    private string _currentPlaylistId = "";
    private Core.Models.Playlist? _currentPlaylist;

    private DateTime _lastLocalMutationTime = DateTime.MinValue;
    private const int LocalMutationDebounceMs = 1500;

    private List<TrackInfo>? _allTracksCache;
    private bool _allTracksCacheValid;

    private volatile bool _isSuspended;
    private int _syncInProgressGate;

    #endregion

    #region Properties

    /// <summary>
    /// Проксирует настройку плавной загрузки для TrackListControl.
    /// Читается из Settings один раз при открытии плейлиста.
    /// </summary>
    public bool EnableSmoothLoading => LibService.Settings.EnableSmoothLoading;

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }

    /// <summary>Описание плейлиста. Отображается в хедере под названием.</summary>
    [Reactive] public string? Description { get; private set; }

    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public string FormattedDuration { get; set; } = "";
    [Reactive] public IBrush? HeaderBackground { get; private set; }
    [Reactive] public bool IsLikedPlaylist { get; private set; }
    [Reactive] public bool CanEdit { get; private set; }
    [Reactive] public bool IsCloud { get; private set; }
    [Reactive] public bool IsReadOnly { get; private set; }
    [Reactive] public bool IsPlayingThisPlaylist { get; private set; }
    [Reactive] public bool IsShuffleActive { get; private set; }
    [Reactive] public bool IsDownloadingActive { get; private set; }
    [Reactive] public bool IsSyncing { get; private set; }
    [Reactive] public bool CanReorderItems { get; private set; }

    /// <summary>
    /// YouTube URL плейлиста для CopyLinkButton.
    /// Для Liked Playlist — специальный системный ID "LL".
    /// Null если плейлист не привязан к YouTube.
    /// </summary>
    public string? PlaylistYoutubeUrl =>
        IsLikedPlaylist && _auth.IsAuthenticated
            ? "https://www.youtube.com/playlist?list=LL"
            : _currentPlaylist?.YoutubeId is { Length: > 0 } id
                ? $"https://www.youtube.com/playlist?list={id}"
                : null;

    [Reactive] public bool HasYoutubeLink { get; private set; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    /// <summary>
    /// Высота хедера плейлиста, управляемая GridSplitter (TwoWay).
    /// Зажата в [<see cref="HeaderHeightMin"/>, <see cref="HeaderHeightMax"/>].
    /// Сохраняется в Settings при изменении.
    /// </summary>
    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            if (!value.IsAbsolute) return;
            var clamped = new GridLength(Math.Clamp(value.Value, HeaderHeightMin, HeaderHeightMax));
            this.RaiseAndSetIfChanged(ref _headerHeight, clamped);
            if (Math.Abs(LibService.Settings.PlaylistHeaderHeight - clamped.Value) > 1)
                LibService.UpdateSettings(s => s.PlaylistHeaderHeight = clamped.Value);
        }
    }

    private GridLength _headerHeight;

    /// <summary>
    /// Минимальная высота хедера: padding(48) + title×2(100) + desc×2(44) +
    /// stats(36) + buttons(48) + margins(24) ≈ 300px. Floor 280px с запасом.
    /// </summary>
    private const double HeaderHeightMin = 280;

    /// <summary>
    /// Максимальная высота хедера: предотвращает скрытие треков при случайном растягивании.
    /// </summary>
    private const double HeaderHeightMax = 400;

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
    public ReactiveCommand<Unit, Unit> EditPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPlaylistLinkCommand { get; }

    #endregion

    #region Constructor

    public PlaylistViewModel(
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        DialogService dialog,
        TrackViewModelFactory vmFactory,
        DominantColorService dominantColor,
        MainWindowViewModel mainWindow,
        PlaylistSyncService syncService,
        PlaylistEditService editService,
        PlayerControlService playerControl,
        CookieAuthService auth)
        : base(audio, downloads, vmFactory)
    {
        _manager = manager;
        _dialog = dialog;
        _dominantColor = dominantColor;
        _mainWindow = mainWindow;
        _syncService = syncService;
        _editService = editService;
        _playerControl = playerControl;
        _auth = auth;

        _languageChangedHandler = (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        var savedHeight = Math.Clamp(
            LibService.Settings.PlaylistHeaderHeight,
            HeaderHeightMin,
            HeaderHeightMax);
        _headerHeight = new GridLength(savedHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, static c => c > 0);

        PlayAllCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(PlayAllAsync, hasTracks));

        DeletePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (await _dialog.ConfirmAsync(
                SL["Dialog_Confirm_Title"],
                string.Format(SL["Playlist_DeleteConfirm"], PlaylistName)))
            {
                await _manager.DeletePlaylistAsync(_currentPlaylistId);
            }
        }));

        UploadToCloudCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.UploadPlaylistToAccountAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        }));

        UnlinkFromCloudCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ConvertToLocalAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        }));

        var canRefresh = this.WhenAnyValue(
            x => x.IsCloud, x => x.IsSyncing,
            static (isCloud, isSyncing) => isCloud && !isSyncing);

        RefreshPlaylistCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(RefreshPlaylistAsync, canRefresh));

        ShufflePlayCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(ShufflePlayAsync, hasTracks));

        DownloadAllCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(DownloadAllAsync, hasTracks));

        MergePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            MergePlaylistAsync,
            this.WhenAnyValue(x => x.CanEdit)));

        AddToQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
            Audio.EnqueueRange(GetLoadedItemsSnapshot()), hasTracks));

        MoveItemCommand = CreateCommand(
            ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(async tuple =>
            {
                if (!CanReorderItems) return;
                _lastLocalMutationTime = DateTime.Now;
                InvalidateAllTracksCache();
                await MoveItemAsync(tuple.oldIndex, tuple.newIndex);
            }));

        EditPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            EditPlaylistAsync,
            this.WhenAnyValue(x => x.CanEdit)));

        CopyPlaylistLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            CopyPlaylistLinkAsync,
            this.WhenAnyValue(x => x.HasYoutubeLink)));

        this.WhenAnyValue(x => x.CanEdit, x => x.FilterQuery)
            .Subscribe(_ => CanReorderItems = CanEdit && CanReorder)
            .DisposeWith(Disposables);

        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if (_isSuspended) return;

                // Локальная мутация (move/remove) недавно — состояние уже актуально в UI,
                // полный reload приведёт к визуальному миганию.
                if ((DateTime.Now - _lastLocalMutationTime).TotalMilliseconds < LocalMutationDebounceMs)
                {
                    Log.Debug("[Playlist] Ignoring OnDataChanged (recent local mutation)");
                    return;
                }

                InvalidateAllTracksCache();

                if (!string.IsNullOrEmpty(_currentPlaylistId))
                    await LoadPlaylistAsync(_currentPlaylistId);
            });

        SubscribeToPlaybackSource();
    }

    #endregion

    #region Lifecycle

    protected override void OnSuspend()
    {
        _isSuspended = true;
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        InvalidateAllTracksCache();
        UpdateIsPlayingThisPlaylist();
        this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }

    #endregion

    #region TrackListReorderableViewModel Implementation

    /// <summary>
    /// Переопределяем CreateViewModel для настройки контекстных действий VM:
    /// RemoveFromPlaylistAction, StartRadioAction, SourceContextId, IsPlaylistContext.
    /// Базовая логика (SetActive при создании) вызывается через base.
    /// </summary>
    protected override TrackItemViewModel CreateViewModel(TrackInfo item)
    {
        var vm = base.CreateViewModel(item);

        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async t =>
        {
            if (!CanEdit) return;

            _lastLocalMutationTime = DateTime.Now;
            InvalidateAllTracksCache();

            // O(1) UI: убираем строку без полного re-render.
            RemoveItemLocally(t.Id);

            TrackCount = Math.Max(0, TrackCount - 1);
            this.RaisePropertyChanged(nameof(FormattedTrackCount));

            if (t.Duration > TimeSpan.Zero)
            {
                TotalDuration = TotalDuration > t.Duration
                    ? TotalDuration - t.Duration
                    : TimeSpan.Zero;
                FormatDuration();
            }

            // DB + YouTube sync в фоне. OnDataChanged будет проигнорирован
            // благодаря _lastLocalMutationTime debounce.
            await _manager.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
        };

        vm.StartRadioAction = t => Log.Info($"[Playlist] Start radio requested for {t.Title}");

        return vm;
    }

    /// <summary>
    /// Загружает треки плейлиста по списку ID.
    /// ReorderableViewModel вызывает этот метод при инициализации.
    /// </summary>
    protected override async Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        return await LibService.GetPlaylistTracksAsync(_currentPlaylistId, ct);
    }

    /// <summary>
    /// Сохраняет новый порядок в БД после drag-and-drop.
    /// </summary>
    protected override async Task SaveMoveAsync(
        int fromMasterIndex, int toMasterIndex, CancellationToken ct)
    {
        Log.Info($"[Playlist] Saving move {fromMasterIndex}→{toMasterIndex}");
        await _manager.MovePlaylistTrackAsync(_currentPlaylistId, fromMasterIndex, toMasterIndex, ct);
        Log.Info("[Playlist] Move saved");
    }

    /// <summary>
    /// Запускает воспроизведение конкретного трека из плейлиста.
    /// Формирует полную очередь, выключает авто-shuffle через PlayerControlService.
    /// </summary>
    protected override void OnPlay(TrackInfo track)
    {
        _ = PlayFromPlaylistAsync(track);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Загружает плейлист по паттерну Skeleton-First:
    /// 1. Мгновенно выставляет заголовок и IsLoading=true (0мс отклик UI).
    /// 2. В фоне грузит треки из БД.
    /// 3. Снимает IsLoading — скелетоны заменяются треками.
    /// </summary>
    public async Task LoadPlaylistAsync(string playlistId)
    {
        _currentPlaylistId = playlistId;
        InvalidateAllTracksCache();

        IsLoading = true;

        var playlist = await LibService.GetPlaylistAsync(playlistId);
        if (playlist is null)
        {
            IsLoading = false;
            return;
        }

        _currentPlaylist = playlist;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        Description = playlist.Description;
        CanEdit = playlist.IsEditable;
        IsLikedPlaylist = playlistId == LibraryService.LikedPlaylistId;
        IsCloud = playlist.IsFromAccount || (IsLikedPlaylist && _auth.IsAuthenticated);
        this.RaisePropertyChanged(nameof(PlaylistYoutubeUrl));
        HasYoutubeLink = PlaylistYoutubeUrl is not null;
        IsReadOnly = !playlist.IsEditable;

        var allIds = await LibService.GetPlaylistTrackIdsAsync(playlistId);
        TrackCount = allIds.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        TotalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId);
        FormatDuration();
        this.RaisePropertyChanged(nameof(PlaylistYoutubeUrl));

        await InitializeAsync(allIds);

        IsLoading = false;

        _ = LoadHeaderGradientAsync();
        _ = HydrateCacheStatusInBackgroundAsync();

        UpdateIsPlayingThisPlaylist();
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Реактивная подписка: IsPlayingThisPlaylist обновляется при любом изменении
    /// источника или состояния воспроизведения — без полного перезапуска LoadPlaylistAsync.
    ///
    /// <para><b>Условие:</b> плейлист считается "воспроизводящимся" если:
    /// <list type="bullet">
    ///   <item>Именно он является источником текущей очереди (ActivePlaylistId совпадает)</item>
    ///   <item>И состояние — Playing ИЛИ Paused (не Stopped)</item>
    /// </list></para>
    ///
    /// <para><b>IsPaused = true:</b> кнопка показывает Pause → клик возобновит.
    /// Это корректное поведение — пользователь видит что этот плейлист активен.</para>
    /// </summary>
    private void SubscribeToPlaybackSource()
    {
        _playerControl.ActivePlaylistIdObservable
            .CombineLatest(
                _playerControl.PlaybackStateObservable,
                (id, state) => id == _currentPlaylistId && (state.IsPlaying || state.IsPaused))
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v => IsPlayingThisPlaylist = v)
            .DisposeWith(Disposables);
    }

    /// <summary>
    /// Синхронное обновление IsPlayingThisPlaylist по текущему состоянию сервиса.
    /// Вызывается после LoadPlaylistAsync когда _currentPlaylistId меняется,
    /// т.к. подписка из конструктора использует замкнутое поле — не retriggers автоматически.
    /// </summary>
    private void UpdateIsPlayingThisPlaylist()
    {
        IsPlayingThisPlaylist =
            _playerControl.ActivePlaylistId == _currentPlaylistId
            && (_playerControl.IsPlaying || _playerControl.IsPaused);
    }

    private async Task<List<TrackInfo>> GetAllTracksAsync()
    {
        if (_allTracksCacheValid && _allTracksCache is not null)
            return _allTracksCache;

        _allTracksCache = await LibService.GetPlaylistTracksAsync(_currentPlaylistId);
        _allTracksCacheValid = true;
        return _allTracksCache;
    }

    private void InvalidateAllTracksCache()
    {
        _allTracksCacheValid = false;
        _allTracksCache = null;
    }

    private void FormatDuration()
    {
        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;
        int seconds = TotalDuration.Seconds;

        FormattedDuration = totalHours > 0
            ? $"{totalHours}{SL["Duration_Hours"]} {minutes}{SL["Duration_Minutes"]}"
            : minutes > 0
                ? $"{minutes}{SL["Duration_Minutes"]} {seconds}{SL["Duration_Seconds"]}"
                : $"{seconds}{SL["Duration_Seconds"]}";
    }

    #endregion

    #region Command Implementations

    private async Task PlayAllAsync()
    {
        if (TrackCount == 0) return;

        if (IsPlayingThisPlaylist)
        {
            // Toggle: пауза если играет, продолжение если на паузе
            await _playerControl.PlayPauseAsync();
            return;
        }

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        _playerControl.SetShuffleEnabled(false);
        _playerControl.SetActivePlaylistId(_currentPlaylistId);
        IsShuffleActive = false;
        await Audio.StartQueueAsync(allTracks, allTracks[0]);
    }

    /// <summary>
    /// Разовый shuffle треков плейлиста (не включает авто-shuffle).
    /// Fisher-Yates для честного перемешивания.
    /// </summary>
    private async Task ShufflePlayAsync()
    {
        if (TrackCount == 0) return;

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        var shuffled = new List<TrackInfo>(allTracks);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        _playerControl.SetShuffleEnabled(false);
        _playerControl.SetActivePlaylistId(_currentPlaylistId);
        await Audio.StartQueueAsync(shuffled, shuffled[0]);

        IsShuffleActive = true;
        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false)
            .DisposeWith(Disposables);
    }

    private async Task DownloadAllAsync()
    {
        IsDownloadingActive = true;

        var allTracks = await GetAllTracksAsync();
        foreach (var track in allTracks.Where(static t => !t.IsDownloaded))
            Downloads.StartDownload(track);

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false)
            .DisposeWith(Disposables);
    }

    private async Task PlayFromPlaylistAsync(TrackInfo track)
    {
        try
        {
            _playerControl.SetShuffleEnabled(false);
            _playerControl.SetActivePlaylistId(_currentPlaylistId);
            IsShuffleActive = false;
            var allTracks = await GetAllTracksAsync();
            await Audio.StartQueueAsync(allTracks, track);
            _ = LibService.AddToRecentlyPlayedAsync(track);
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] PlayFromPlaylist error: {ex.Message}");
        }
    }

    private async Task EditPlaylistAsync()
    {
        var result = await _editService.EditPlaylistAsync(
            _currentPlaylistId,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);

        if (result is { Changed: true })
            await LoadPlaylistAsync(_currentPlaylistId);
    }

    /// <summary>
    /// Синхронизация с YouTube. Защита от двойного запуска через Interlocked gate.
    /// Для Liked playlist — SyncLikedTracksAsync.
    /// Для остальных — PlaylistSyncService с диалогом опций.
    /// </summary>
    private async Task RefreshPlaylistAsync()
    {
        if (!IsCloud) return;

        if (Interlocked.Exchange(ref _syncInProgressGate, 1) != 0)
        {
            Log.Debug("[Playlist] Sync ignored: already in progress");
            return;
        }

        IsSyncing = true;
        _mainWindow.LockNavigation(SL["Playlist_SyncInProgress"] ?? "Syncing...");

        try
        {
            var notifications = Program.Services
                .GetRequiredService<NotificationService>();

            if (IsLikedPlaylist)
            {
                await _manager.SyncLikedTracksAsync();
                InvalidateAllTracksCache();
                await LoadPlaylistAsync(_currentPlaylistId);

                await notifications.ShowToastAsync(
                    titleKey: "Playlist_SyncComplete_Toast_Title",
                    messageKey: "Sync_Success_Msg_LikedOnly",
                    severity: NotificationSeverity.Success,
                    durationMs: 4000);

                NotificationService.PlaySuccessSound();
            }
            else
            {
                var result = await _syncService.SyncWithDialogAsync(_currentPlaylistId);
                if (result is null) return;

                if (result.Success)
                {
                    InvalidateAllTracksCache();
                    await LoadPlaylistAsync(_currentPlaylistId);

                    if (result.TracksAddedLocally > 0 || result.TracksAddedToCloud > 0 ||
                        result.TracksRemovedLocally > 0 || result.TracksRemovedFromCloud > 0 ||
                        result.MetadataChanged)
                    {
                        await notifications.ShowToastAsync(
                            titleKey: "Playlist_SyncComplete_Toast_Title",
                            messageKey: "Playlist_SyncSuccess_Details",
                            messageArgs:
                            [
                                result.TracksAddedLocally,
                                result.TracksAddedToCloud,
                                result.TracksRemovedLocally,
                                result.TracksRemovedFromCloud
                            ],
                            severity: NotificationSeverity.Success,
                            durationMs: 4000);

                        NotificationService.PlaySuccessSound();
                    }
                }
                else
                {
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"] ?? "Error",
                        result.ErrorMessage ?? SL["Playlist_SyncFailed"] ?? "Sync failed");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Sync error: {ex.Message}");
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            _mainWindow.UnlockNavigation();
            Interlocked.Exchange(ref _syncInProgressGate, 0);
        }
    }

    /// <summary>
    /// Копирует ссылку на плейлист и показывает toast через CopyHintService.
    /// Warning-toast если плейлист не привязан к YouTube.
    /// </summary>
    private async Task CopyPlaylistLinkAsync()
    {
        if (_currentPlaylist?.YoutubeId is not { Length: > 0 } ytId)
        {
            CopyHintService.Instance.Show(
                SL["Playlist_CopyLink_NotLinked"] ?? "Not linked to YouTube",
                CopyHintKind.Warning);
            return;
        }

        await Clipboard.SetTextAsync($"https://www.youtube.com/playlist?list={ytId}");
        CopyHintService.Instance.Show(
            SL["Playlist_LinkCopied"] ?? "Copied!",
            CopyHintKind.Success);
    }

    /// <summary>
    /// Загружает градиент хедера с приоритетом:
    /// CustomColor → ComputedColor → доминантный из обложки → null.
    /// </summary>
    private async Task LoadHeaderGradientAsync()
    {
        try
        {
            if (_currentPlaylist?.EffectiveColor is { } effectiveColorStr)
            {
                try
                {
                    var effectiveColor = Color.Parse(effectiveColorStr);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        HeaderBackground = DominantColorService.CreateHeaderGradient(effectiveColor));
                    return;
                }
                catch (FormatException)
                {
                    Log.Warn($"[Playlist] Invalid EffectiveColor: {effectiveColorStr}");
                }
            }

            if (string.IsNullOrEmpty(ThumbnailUrl)
                || !PlaylistEditorViewModel.IsValidUri(ThumbnailUrl))
            {
                await SetHeaderBackgroundAsync(null);
                return;
            }

            var dominantColor = await _dominantColor.GetDominantColorAsync(ThumbnailUrl);

            if (dominantColor is not null)
            {
                _ = SaveComputedColorAsync(dominantColor.Value);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    HeaderBackground = DominantColorService.CreateHeaderGradient(dominantColor.Value));
            }
            else
            {
                await SetHeaderBackgroundAsync(null);
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[Playlist] Thumbnail load failed (network): {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                string.Format(SL["Playlist_ThumbnailLoadFailed"] ?? "Could not load cover: {0}",
                    ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Log.Warn($"[Playlist] Invalid image: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                SL["Playlist_ThumbnailInvalidFormat"] ?? "Invalid cover format.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Gradient error: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
        }
    }

    private async Task SaveComputedColorAsync(Color color)
    {
        if (_currentPlaylist is null) return;

        var colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        if (string.Equals(_currentPlaylist.ComputedColor, colorHex, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _currentPlaylist.ComputedColor = colorHex;
            await LibService.AddOrUpdatePlaylistAsync(_currentPlaylist);
            Log.Debug($"[Playlist] ComputedColor saved: {colorHex}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Failed to save ComputedColor: {ex.Message}");
        }
    }

    private async Task SetHeaderBackgroundAsync(IBrush? brush) =>
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => HeaderBackground = brush);

    private async Task HydrateCacheStatusInBackgroundAsync()
    {
        try
        {
            var tracks = GetLoadedItemsSnapshot();
            var audioCache = AudioSourceFactory.GlobalCache;
            if (audioCache is null) return;
            await Task.Run(() => audioCache.HydrateCacheStatus(tracks));
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Cache hydration error: {ex.Message}");
        }
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
            bool ok = await _manager.MergePlaylistsAsync(_currentPlaylistId, targetId);
            await _dialog.ShowInfoAsync(
                ok ? SL["Dialog_Success"] : SL["Dialog_Error_Title"],
                ok ? SL["Merge_Success_Msg"] : SL["Merge_Error_Msg"]);
        }
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log.Debug($"[PlaylistVM] Disposing {_currentPlaylistId}");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _librarySubscription?.Dispose();
            InvalidateAllTracksCache();
            _currentPlaylist = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}