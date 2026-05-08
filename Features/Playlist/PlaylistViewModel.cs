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

namespace LMP.Features.Playlist;

public sealed class PlaylistViewModel : ReorderableViewModel<TrackInfo, TrackItemViewModel>
{
    #region Fields

    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly DialogService _dialog;
    private readonly TrackViewModelFactory _vmFactory;
    private readonly DominantColorService _dominantColor;
    private readonly MainWindowViewModel _mainWindow;
    private readonly PlaylistEditService _editService;
    private readonly PlaylistSyncService _syncService;
    private readonly PlayerControlService _playerControl;
    private readonly CookieAuthService _auth;

    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentPlaylistId = "";

    private readonly IDisposable? _librarySubscription;

    private DateTime _lastMoveTime = DateTime.MinValue;
    private const int MoveDebounceMs = 1000;

    private List<TrackInfo>? _allTracksCache;
    private bool _allTracksCacheValid;

    private volatile bool _isSuspended;

    /// <summary>
    /// Текущий объект плейлиста, загруженный из БД.
    /// Хранится для доступа к ComputedColor/EffectiveColor при градиенте.
    /// </summary>
    private Core.Models.Playlist? _currentPlaylist;

    private TrackItemViewModel? _currentActiveVm;

    #endregion

    #region Properties

    /// <summary>   
    /// Проксирует настройку плавной загрузки для TrackListControl.
    /// Читается из Settings один раз при открытии плейлиста.
    /// </summary>
    public bool EnableSmoothLoading => LibService.Settings.EnableSmoothLoading;

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }

    /// <summary>
    /// Описание плейлиста. Отображается в хедере под названием.
    /// </summary>
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

    /// <summary>YouTube URL плейлиста для CopyLinkButton. Null если плейлист не привязан.</summary>
    public string? PlaylistYoutubeUrl => _currentPlaylist?.YoutubeId is { Length: > 0 } id
        ? $"https://www.youtube.com/playlist?list={id}"
        : null;

    /// <summary>
    /// True если плейлист имеет ссылку на YouTube (можно скопировать).
    /// </summary>
    [Reactive] public bool HasYoutubeLink { get; private set; }

    /// <summary>
    /// Идёт ли синхронизация/обновление плейлиста.
    /// Используется для блокировки кнопки RefreshPlaylist.
    /// </summary>
    [Reactive] public bool IsSyncing { get; private set; }

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
    {
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _dialog = dialog;
        _vmFactory = vmFactory;
        _dominantColor = dominantColor;
        _mainWindow = mainWindow;
        _syncService = syncService;
        _editService = editService;
        _playerControl = playerControl;
        _auth = auth;

        _languageChangedHandler = (_, _) =>
        {
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        IsShuffleActive = _playerControl.ShuffleEnabled;
        _headerHeight = new GridLength(LibService.Settings.PlaylistHeaderHeight);

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = CreateCommand(ReactiveCommand.CreateFromTask(PlayAllAsync, hasTracks));

        DeletePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            var confirmTitle = SL["Dialog_Confirm_Title"];
            var confirmMsg = string.Format(SL["Playlist_DeleteConfirm"], PlaylistName);

            if (await _dialog.ConfirmAsync(confirmTitle, confirmMsg))
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

        // RefreshPlaylistCommand: canExecute = IsCloud && !IsSyncing
        var canRefresh = this.WhenAnyValue(
            x => x.IsCloud,
            x => x.IsSyncing,
            (isCloud, isSyncing) => isCloud && !isSyncing);

        RefreshPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            RefreshPlaylistAsync, canRefresh));

        ShufflePlayCommand = CreateCommand(ReactiveCommand.CreateFromTask(ShufflePlayAsync, hasTracks));
        DownloadAllCommand = CreateCommand(ReactiveCommand.CreateFromTask(DownloadAllAsync, hasTracks));
        MergePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            MergePlaylistAsync, this.WhenAnyValue(x => x.CanEdit)));

        AddToQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _audio.EnqueueRange(GetLoadedItemsSnapshot());
        }, hasTracks));

        MoveItemCommand = CreateCommand(ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(async tuple =>
        {
            if (!CanReorderItems) return;

            _lastMoveTime = DateTime.Now;
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
            .Subscribe(_ =>
            {
                CanReorderItems = CanEdit && CanReorder;
            })
            .DisposeWith(Disposables);

        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if (_isSuspended) return;

                if ((DateTime.Now - _lastMoveTime).TotalMilliseconds < MoveDebounceMs)
                {
                    Log.Debug("[Playlist] Ignoring OnDataChanged (recent move)");
                    return;
                }

                InvalidateAllTracksCache();

                if (!string.IsNullOrEmpty(_currentPlaylistId))
                {
                    await LoadPlaylistAsync(_currentPlaylistId);
                }
            });

        // 1. Подписка на смену трека (Smart Parent)
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => UpdatePlaybackState(t, _audio.IsPlaying))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
                h => (a, b) => h((a, b)),
                h => _audio.OnPlaybackStateChanged += h,
                h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => UpdatePlaybackState(_audio.CurrentTrack, x.Item1))
            .DisposeWith(Disposables);

        // 2. Подписка на загрузки (Smart Parent)
        Observable.FromEvent<Action<string, float>, (string, float)>(
                h => (id, p) => h((id, p)),
                h => _downloads.OnProgress += h,
                h => _downloads.OnProgress -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => UpdateDownloadState(x.Item1, true, x.Item2))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<string, bool, string?>, (string, bool, string?)>(
                h => (id, ok, path) => h((id, ok, path)),
                h => _downloads.OnCompleted += h,
                h => _downloads.OnCompleted -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => UpdateDownloadState(x.Item1, false, 0))
            .DisposeWith(Disposables);
    }

    #endregion

    #region Lifecycle

    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        if (_currentActiveVm != null && _currentActiveVm.Id != currentTrack?.Id)
        {
            _currentActiveVm.SetActive(false, false);
            _currentActiveVm = null;
        }

        if (currentTrack != null)
        {
            _currentActiveVm ??= GetCachedVm(currentTrack.Id);
            _currentActiveVm?.SetActive(true, isPlaying);
        }
    }

    private void UpdateDownloadState(string trackId, bool isDownloading, float progress)
    {
        var vm = GetCachedVm(trackId);
        vm?.SetDownloadState(isDownloading, progress);
    }

    protected override void OnSuspend()
    {
        _isSuspended = true;
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        InvalidateAllTracksCache();

        // Восстанавливаем состояние без DB query
        UpdatePlaybackState(_audio.CurrentTrack, _audio.IsPlaying);
        UpdateIsPlayingThisPlaylist(_audio.CurrentTrack);

        this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }

    #endregion

    #region Abstract Implementation

    protected override string GetItemId(TrackInfo item) => item.Id;

    protected override TrackItemViewModel CreateViewModel(TrackInfo item)
    {
        var vm = _vmFactory.GetOrCreate(item, PlayFromPlaylistAsync);
        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async (t) =>
        {
            if (CanEdit)
            {
                InvalidateAllTracksCache();
                await _manager.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
            }
        };

        vm.StartRadioAction = (t) => Log.Info($"Start radio requested for {t.Title}");

        return vm;
    }

    protected override bool MatchesFilter(TrackInfo item, string query)
        => TrackFilters.MatchesTitleOrAuthor(item, query);

    protected override async Task<List<TrackInfo>> LoadItemsByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        var idsList = ids.ToList();
        return await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId,
            limit: idsList.Count,
            offset: 0,
            ct);
    }

    protected override async Task SaveMoveAsync(int fromMasterIndex, int toMasterIndex, CancellationToken ct)
    {
        Log.Info($"[Playlist] Saving move {fromMasterIndex}→{toMasterIndex}");
        await _manager.MovePlaylistTrackAsync(_currentPlaylistId, fromMasterIndex, toMasterIndex, ct);
        Log.Info("[Playlist] Move saved");
    }

    #endregion

    #region All Tracks Helper

    private async Task<List<TrackInfo>> GetAllTracksAsync()
    {
        if (_allTracksCacheValid && _allTracksCache != null)
            return _allTracksCache;

        var allIds = GetAllIds();
        _allTracksCache = await LibService.GetPlaylistTracksAsync(
            _currentPlaylistId, limit: allIds.Count, offset: 0);
        _allTracksCacheValid = true;

        return _allTracksCache;
    }

    private void InvalidateAllTracksCache()
    {
        _allTracksCacheValid = false;
        _allTracksCache = null;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Загружает плейлист с паттерном Skeleton-First:
    /// 1. Мгновенно выставляет заголовок и IsLoading=true (UI реагирует за 0мс)
    /// 2. В фоне грузит треки из БД
    /// 3. Снимает IsLoading — скелетоны заменяются треками
    /// </summary>
    public async Task LoadPlaylistAsync(string playlistId)
    {
        _currentPlaylistId = playlistId;
        InvalidateAllTracksCache();

        // ═══ ШАГ 1: Мгновенный отклик UI ═══
        IsLoading = true;

        var playlist = await LibService.GetPlaylistAsync(playlistId);
        if (playlist == null)
        {
            IsLoading = false;
            return;
        }

        _currentPlaylist = playlist;

        // Заголовок и мета-данные — показываем сразу, пока треки грузятся
        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        Description = playlist.Description;
        CanEdit = playlist.IsEditable;
        IsLikedPlaylist = playlistId == LibraryService.LikedPlaylistId;
        IsCloud = playlist.IsFromAccount || (IsLikedPlaylist && _auth.IsAuthenticated);
        HasYoutubeLink = !string.IsNullOrEmpty(playlist.YoutubeId) || (IsLikedPlaylist && _auth.IsAuthenticated);
        IsReadOnly = !playlist.IsEditable;

        // ═══ ШАГ 2: Загрузка данных (треки, длительность) ═══
        var allIds = await LibService.GetPlaylistTrackIdsAsync(playlistId);
        TrackCount = allIds.Count;
        this.RaisePropertyChanged(nameof(FormattedTrackCount));

        TotalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId);
        FormatDuration();

        this.RaisePropertyChanged(nameof(PlaylistYoutubeUrl));

        // InitializeAsync строит Items: загружает все треки одним запросом
        await InitializeAsync(allIds);

        // ═══ ШАГ 3: Снимаем скелетон ═══
        IsLoading = false;

        // Фоновые задачи — не блокируют UI
        _ = LoadHeaderGradientAsync();
        _ = HydrateCacheStatusInBackgroundAsync();

        // Начальное состояние воспроизведения без DB query
        UpdateIsPlayingThisPlaylist(_audio.CurrentTrack);
        UpdatePlaybackState(_audio.CurrentTrack, _audio.IsPlaying);
    }

    #endregion

    #region Edit Playlist — delegated to PlaylistEditService

    /// <summary>
    /// Определяет IsPlayingThisPlaylist без обращения к БД:
    /// трек принадлежит плейлисту если его ID есть в _masterIds (загруженный список).
    /// Fallback к DB query только если список ещё не загружен.
    /// </summary>
    private void UpdateIsPlayingThisPlaylist(TrackInfo? currentTrack)
    {
        if (currentTrack == null || !_audio.IsPlaying)
        {
            IsPlayingThisPlaylist = false;
            return;
        }

        // O(1): проверяем по кэшу VM — если VM есть, трек точно в этом плейлисте
        var cachedVm = GetCachedVm(currentTrack.Id);
        IsPlayingThisPlaylist = cachedVm != null;
    }

    /// <summary>
    /// Edit playlist using centralized PlaylistEditService.
    /// After successful edit, reloads playlist header data.
    /// </summary>
    private async Task EditPlaylistAsync()
    {
        var result = await _editService.EditPlaylistAsync(
            _currentPlaylistId,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);

        if (result is { Changed: true })
        {
            await LoadPlaylistAsync(_currentPlaylistId);
        }
    }

    #endregion

    /// <summary>
    /// Синхронизирует плейлист с YouTube.
    /// Для обычных плейлистов использует PlaylistSyncService (с диалогом).
    /// Для плейлиста "Понравившиеся" вызывает MusicLibraryManager.SyncLikedTracksAsync (без диалога).
    /// </summary>
    private async Task RefreshPlaylistAsync()
    {
        if (IsSyncing || !IsCloud) return;

        IsSyncing = true;
        _mainWindow.LockNavigation(SL["Playlist_SyncInProgress"] ?? "Syncing...");

        try
        {
            // СПЕЦИАЛЬНАЯ ЛОГИКА ДЛЯ ЛАЙКОВ
            if (IsLikedPlaylist)
            {
                await _manager.SyncLikedTracksAsync();

                InvalidateAllTracksCache();
                await LoadPlaylistAsync(_currentPlaylistId);

                var notifications = Microsoft.Extensions.DependencyInjection
                    .ServiceProviderServiceExtensions
                    .GetRequiredService<NotificationService>(Program.Services);

                await notifications.ShowToastAsync(
                    titleKey: "Playlist_SyncComplete_Toast_Title",
                    messageKey: "Sync_Success_Msg_LikedOnly",
                    severity: NotificationSeverity.Success,
                    durationMs: 4000);

                NotificationService.PlaySuccessSound();
            }
            // ОБЫЧНАЯ ЛОГИКА ДЛЯ ПЛЕЙЛИСТОВ
            else
            {
                var result = await _syncService.SyncWithDialogAsync(_currentPlaylistId);

                if (result == null) return;

                if (result.Success)
                {
                    InvalidateAllTracksCache();
                    await LoadPlaylistAsync(_currentPlaylistId);

                    if (result.TracksAddedLocally > 0 || result.TracksAddedToCloud > 0 ||
                        result.TracksRemovedLocally > 0 || result.TracksRemovedFromCloud > 0 ||
                        result.MetadataChanged)
                    {
                        var notifications = Microsoft.Extensions.DependencyInjection
                            .ServiceProviderServiceExtensions
                            .GetRequiredService<NotificationService>(Program.Services);

                        await notifications.ShowToastAsync(
                            titleKey: "Playlist_SyncComplete_Toast_Title",
                            messageKey: "Playlist_SyncSuccess_Details",
                            messageArgs: [
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
        }
    }

    #region Private Methods

    /// <summary>
    /// Копирует ссылку на плейлист и показывает глобальный toast через <see cref="CopyHintService"/>.
    /// Warning-toast отображается когда плейлист не связан с YouTube.
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
    /// Загружает градиент хедера с учётом приоритета цветов:
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
                    {
                        HeaderBackground = DominantColorService.CreateHeaderGradient(effectiveColor);
                    });
                    return;
                }
                catch (FormatException)
                {
                    Log.Warn($"[Playlist] Invalid EffectiveColor: {effectiveColorStr}");
                }
            }

            if (string.IsNullOrEmpty(ThumbnailUrl))
            {
                await SetHeaderBackgroundAsync(null);
                return;
            }

            if (!PlaylistEditorViewModel.IsValidUri(ThumbnailUrl))
            {
                Log.Warn($"[Playlist] Invalid thumbnail URL skipped: {ThumbnailUrl}");
                await SetHeaderBackgroundAsync(null);
                return;
            }

            var dominantColor = await _dominantColor.GetDominantColorAsync(ThumbnailUrl);

            if (dominantColor != null)
            {
                _ = SaveComputedColorAsync(dominantColor.Value);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    HeaderBackground = DominantColorService.CreateHeaderGradient(dominantColor.Value);
                });
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
                string.Format(SL["Playlist_ThumbnailLoadFailed"]
                    ?? "Could not load cover image: {0}", ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Log.Warn($"[Playlist] Invalid image: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                SL["Playlist_ThumbnailInvalidFormat"]
                    ?? "The cover image format is invalid or unsupported.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Gradient error: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
        }
    }

    /// <summary>
    /// Сохраняет вычисленный доминантный цвет в поле ComputedColor плейлиста.
    /// </summary>
    private async Task SaveComputedColorAsync(Color color)
    {
        if (_currentPlaylist == null) return;

        var colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        if (string.Equals(_currentPlaylist.ComputedColor, colorHex, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _currentPlaylist.ComputedColor = colorHex;
            await LibService.AddOrUpdatePlaylistAsync(_currentPlaylist);
            Log.Debug($"[Playlist] ComputedColor saved: {colorHex} for {_currentPlaylistId}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Failed to save ComputedColor: {ex.Message}");
        }
    }

    private async Task SetHeaderBackgroundAsync(IBrush? brush)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => HeaderBackground = brush);
    }

    private async Task HydrateCacheStatusInBackgroundAsync()
    {
        try
        {
            var tracks = GetLoadedItemsSnapshot();
            var audioCache = AudioSourceFactory.GlobalCache;
            if (audioCache == null) return;

            await Task.Run(() => audioCache.HydrateCacheStatus(tracks));
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Cache hydration error: {ex.Message}");
        }
    }

    /// <summary>
    /// Воспроизводит все треки плейлиста по порядку.
    /// Выключает авто-перемешивание через PlayerControlService
    /// чтобы гарантировать синхронизацию с PlayerBar.
    /// </summary>
    private async Task PlayAllAsync()
    {
        if (TrackCount == 0) return;

        if (IsPlayingThisPlaylist)
        {
            await _audio.SetPlaybackStateAsync(false);
            return;
        }

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        // Используем PlayerControlService вместо прямого _audio.ShuffleEnabled
        // чтобы BehaviorSubject оставался синхронизированным с UI
        _playerControl.SetShuffleEnabled(false);
        IsShuffleActive = false;
        await _audio.StartQueueAsync(allTracks, allTracks[0]);
    }

    /// <summary>
    /// Перемешивает треки плейлиста и начинает воспроизведение.
    /// Это НЕ включает авто-перемешивание — просто разовый shuffle.
    /// Выключает авто-перемешивание через PlayerControlService.
    /// </summary>
    private async Task ShufflePlayAsync()
    {
        if (TrackCount == 0) return;

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        var shuffled = new List<TrackInfo>(allTracks);
        var rng = Random.Shared;
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        // Используем PlayerControlService — выключаем авто-shuffle
        // Playlist shuffle — это разовая операция, не авто-режим
        _playerControl.SetShuffleEnabled(false);
        await _audio.StartQueueAsync(shuffled, shuffled[0]);

        IsShuffleActive = true;

        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false)
            .DisposeWith(Disposables);
    }

    private async Task DownloadAllAsync()
    {
        IsDownloadingActive = true;

        var allTracks = await GetAllTracksAsync();

        foreach (var track in allTracks)
        {
            if (!track.IsDownloaded)
                _downloads.StartDownload(track);
        }

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false)
            .DisposeWith(Disposables);
    }

    private void FormatDuration()
    {
        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;
        int seconds = TotalDuration.Seconds;

        if (totalHours > 0)
            FormattedDuration = $"{totalHours}{SL["Duration_Hours"]} {minutes}{SL["Duration_Minutes"]}";
        else if (minutes > 0)
            FormattedDuration = $"{minutes}{SL["Duration_Minutes"]} {seconds}{SL["Duration_Seconds"]}";
        else
            FormattedDuration = $"{seconds}{SL["Duration_Seconds"]}";
    }

    /// <summary>
    /// Запускает воспроизведение конкретного трека из плейлиста.
    /// Выключает авто-перемешивание через PlayerControlService.
    /// </summary>
    private async void PlayFromPlaylistAsync(TrackInfo track)
    {
        try
        {
            // Используем PlayerControlService
            _playerControl.SetShuffleEnabled(false);
            IsShuffleActive = false;

            var allTracks = await GetAllTracksAsync();
            await _audio.StartQueueAsync(allTracks, track);
            _ = LibService.AddToRecentlyPlayedAsync(track);
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] PlayFromPlaylist error: {ex.Message}");
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
            if (await _manager.MergePlaylistsAsync(_currentPlaylistId, targetId))
                await _dialog.ShowInfoAsync(SL["Dialog_Success"], SL["Merge_Success_Msg"]);
            else
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Merge_Error_Msg"]);
        }
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log.Debug($"[PlaylistVM] Disposing playlist {_currentPlaylistId}");

            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _librarySubscription?.Dispose();
            // _audioStateSub и _trackChangeSub удалены — подписки живут в Disposables
            InvalidateAllTracksCache();
            _currentPlaylist = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}