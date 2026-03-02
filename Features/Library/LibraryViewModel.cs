using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Shell;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using LMP.Core.Youtube.Search;
using System.Reactive.Disposables;
using LMP.UI.Dialogs;

namespace LMP.Features.Library;

/// <summary>
/// ViewModel страницы «Библиотека» — управление плейлистами пользователя.
/// 
/// <para><b>Ключевые возможности:</b></para>
/// <list type="bullet">
///   <item>Отображение карточек плейлистов с сортировкой (Liked → Local → Cloud)</item>
///   <item>Инкрементальные обновления при добавлении/удалении/изменении плейлистов</item>
///   <item>Анимированная статистика с интерполяцией значений</item>
///   <item>Создание плейлистов (локально и в YouTube Music)</item>
///   <item>Синхронизация плейлистов с YouTube-аккаунтом</item>
///   <item>Редактирование через централизованный <see cref="PlaylistEditService"/></item>
/// </list>
/// 
/// <para><b>Архитектура обновлений:</b></para>
/// <para>
/// Вместо полной перезагрузки при каждом <c>OnDataChanged</c>,
/// используются детализированные события:
/// </para>
/// <list type="bullet">
///   <item><c>OnPlaylistChanged</c> → точечное обновление/добавление карточки</item>
///   <item><c>OnPlaylistRemoved</c> → удаление одной карточки</item>
///   <item><c>OnDataChanged</c> → обновление только статистики (throttled 500ms)</item>
/// </list>
/// <para>Полная перезагрузка (<see cref="LoadPlaylistsAsync"/>) происходит только
/// при начальной загрузке и после массовой синхронизации.</para>
/// </summary>
public sealed class LibraryViewModel : ViewModelBase
{
    #region Зависимости

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly MainWindowViewModel _mainWindow;
    private readonly MusicLibraryManager _manager;
    private readonly PlaylistEditService _editService;
    private readonly NotificationService _notifications;

    #endregion

    #region Внутреннее состояние

    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _staggerCts;
    private CancellationTokenSource? _statsAnimCts;
    private bool _isDisposed;
    private bool _eventsSubscribed;

    /// <summary>Задержка между анимациями появления карточек (мс).</summary>
    private const int StaggerDelayMs = 40;

    /// <summary>
    /// Предыдущие значения статистики для плавной интерполяции.
    /// При добавлении/удалении плейлиста числа анимируются от старого к новому значению.
    /// </summary>
    private int _prevPlaylistCount;
    private int _prevTrackCount;

    #endregion

    #region Reactive-свойства

    /// <summary>Контент готов к отображению (после первой загрузки).</summary>
    [Reactive] public bool IsContentReady { get; private set; }

    /// <summary>Идёт загрузка данных.</summary>
    [Reactive] public bool IsLoading { get; private set; }

    /// <summary>Идёт синхронизация с YouTube.</summary>
    [Reactive] public bool IsSyncing { get; private set; }

    /// <summary>Прогресс синхронизации (0.0 — 1.0).</summary>
    [Reactive] public double SyncProgress { get; private set; }

    /// <summary>Текстовый статус синхронизации.</summary>
    [Reactive] public string SyncStatus { get; private set; } = "";

    /// <summary>Пользователь авторизован в YouTube.</summary>
    [Reactive] public bool IsAuthenticated { get; private set; }

    #endregion

    #region Статистика

    /// <summary>Видимость блока статистики (скрывается во время синхронизации).</summary>
    [Reactive] public bool IsStatsVisible { get; private set; }

    /// <summary>Текст количества плейлистов (с правильным склонением).</summary>
    [Reactive] public string PlaylistCountText { get; private set; } = "";

    /// <summary>Текст общего количества треков.</summary>
    [Reactive] public string TotalTracksText { get; private set; } = "";

    /// <summary>Текст общей длительности.</summary>
    [Reactive] public string TotalDurationText { get; private set; } = "";

    /// <summary>Средняя длительность одного трека.</summary>
    [Reactive] public string AvgTrackDurationText { get; private set; } = "";

    /// <summary>Средняя длительность одного плейлиста.</summary>
    [Reactive] public string AvgPlaylistDurationText { get; private set; } = "";

    #endregion

    #region Коллекция и команды

    /// <summary>
    /// Коллекция карточек плейлистов для UI.
    /// <see cref="ObservableCollection{T}"/> обеспечивает автоматическое обновление UI
    /// при Add/Remove/Move без пересоздания всего списка.
    /// </summary>
    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = [];

    /// <summary>Полная перезагрузка списка плейлистов.</summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Открыть диалог создания нового плейлиста.</summary>
    public ReactiveCommand<Unit, Unit> OpenCreateCommand { get; }

    /// <summary>Запустить синхронизацию с YouTube-аккаунтом.</summary>
    public ReactiveCommand<Unit, Unit> SyncAccountPlaylistsCommand { get; }

    /// <summary>Отменить текущую синхронизацию.</summary>
    public ReactiveCommand<Unit, Unit> CancelSyncCommand { get; }

    #endregion

    #region Конструктор

    /// <param name="library">Сервис библиотеки для CRUD операций с плейлистами.</param>
    /// <param name="youtube">YouTube API провайдер.</param>
    /// <param name="auth">Сервис авторизации YouTube.</param>
    /// <param name="mainWindow">Главная VM для навигации и блокировки.</param>
    /// <param name="dialog">Сервис диалоговых окон.</param>
    /// <param name="manager">Менеджер синхронизации библиотеки с YouTube.</param>
    /// <param name="audio">Аудио-движок для воспроизведения.</param>
    /// <param name="editService">Централизованный сервис редактирования плейлистов.</param>
    public LibraryViewModel(
        LibraryService library,
        YoutubeProvider youtube,
        CookieAuthService auth,
        MainWindowViewModel mainWindow,
        DialogService dialog,
        MusicLibraryManager manager,
        AudioEngine audio,
        NotificationService notifications,
        PlaylistEditService editService)
    {
        _audio = audio;
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _dialog = dialog;
        _mainWindow = mainWindow;
        _manager = manager;
        _notifications = notifications;
        _editService = editService;

        IsAuthenticated = _auth.IsAuthenticated;
        _auth.OnAuthStateChanged += OnAuthChanged;

        OpenCreateCommand = CreateCommand(ReactiveCommand.CreateFromTask(OpenCreateDialogAsync));

        // Синхронизация доступна только если не идёт другая синхронизация
        var canSync = this.WhenAnyValue(x => x.IsSyncing, syncing => !syncing);
        SyncAccountPlaylistsCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(SyncAccountPlaylistsAsync, canSync));

        CancelSyncCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _syncCts?.Cancel();
            SyncStatus = SL["Sync_Cancelling"];
        }));

        RefreshCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadPlaylistsAsync));
    }

    #endregion

    #region Навигация

    /// <summary>
    /// Вызывается при переходе на страницу библиотеки.
    /// Подписывается на события (однократно) и загружает плейлисты.
    /// </summary>
    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        if (!_eventsSubscribed)
        {
            _eventsSubscribed = true;
            SubscribeToIncrementalEvents();
        }

        await LoadPlaylistsAsync();
        IsContentReady = true;
    }

    #endregion

    #region Подписки на события

    /// <summary>
    /// Подписывается на детализированные события <see cref="LibraryService"/>
    /// для инкрементальных обновлений UI.
    /// 
    /// <para><b>Три подписки:</b></para>
    /// <list type="bullet">
    ///   <item><c>OnPlaylistChanged</c> → обновить/добавить одну карточку</item>
    ///   <item><c>OnPlaylistRemoved</c> → удалить одну карточку</item>
    ///   <item><c>OnDataChanged</c> → обновить статистику (с throttle 500ms)</item>
    /// </list>
    /// 
    /// <para><b>Почему throttle только на OnDataChanged:</b>
    /// OnPlaylistChanged/OnPlaylistRemoved вызываются редко (по одному на операцию),
    /// а OnDataChanged может вызываться каскадно (плейлист + все его треки).</para>
    /// </summary>
    private void SubscribeToIncrementalEvents()
    {
        // 1. Инкрементальное обновление при изменении конкретного плейлиста
        Observable.FromEvent<Action<Core.Models.Playlist>, Core.Models.Playlist>(
                h => _library.OnPlaylistChanged += h,
                h => _library.OnPlaylistChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => !_isDisposed && !IsSyncing)
            .Subscribe(playlist => OnPlaylistChangedIncremental(playlist))
            .DisposeWith(Disposables);

        // 2. Инкрементальное удаление плейлиста
        Observable.FromEvent<Action<string>, string>(
                h => _library.OnPlaylistRemoved += h,
                h => _library.OnPlaylistRemoved -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => !_isDisposed && !IsSyncing)
            .Subscribe(id => OnPlaylistRemovedIncremental(id))
            .DisposeWith(Disposables);

        // 3. Глобальные изменения → только обновление статистики (не плейлистов!)
        // Throttle предотвращает каскадные обновления при массовых операциях
        Observable.FromEvent(
                h => _library.OnDataChanged += h,
                h => _library.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(__ => !_isDisposed && !IsSyncing)
            .Subscribe(__ => UpdateStatsInBackground())
            .DisposeWith(Disposables);
    }

    /// <summary>
    /// Fire-and-forget обёртка для обновления статистики.
    /// Выделен в отдельный метод чтобы:
    /// 1) Избежать конфликта discard <c>_</c> с параметром лямбды
    /// 2) Обработать исключения (async void + try/catch)
    /// </summary>
    private async void UpdateStatsInBackground()
    {
        try
        {
            await UpdateStatsAnimatedAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Library] Ошибка обновления статистики: {ex.Message}");
        }
    }

    #endregion

    #region Инкрементальные обновления

    /// <summary>
    /// Обрабатывает изменение конкретного плейлиста: обновляет существующую карточку
    /// или добавляет новую с анимацией.
    /// 
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Загружает актуальные данные плейлиста с trackCount (один SQL-запрос)</item>
    ///   <item>Ищет существующую карточку по ID</item>
    ///   <item>Если найдена → обновляет все поля (название, обложка, sync, кол-во треков)</item>
    ///   <item>Если не найдена → создаёт новую карточку и вставляет в правильную позицию</item>
    ///   <item>Статистика обновится отдельно через <c>OnDataChanged</c></item>
    /// </list>
    /// 
    /// <para><b>Fallback:</b> при ошибке выполняет полную перезагрузку.</para>
    /// </summary>
    private async void OnPlaylistChangedIncremental(Core.Models.Playlist playlist)
    {
        if (_isDisposed) return;

        try
        {
            // Загружаем актуальные данные: один запрос вместо GetAllPlaylistsWithCountsAsync
            var result = await _library.GetPlaylistWithCountAsync(playlist.Id);
            if (result == null) return;

            var (freshPlaylist, trackCount) = result.Value;
            var existingVm = Playlists.FirstOrDefault(vm => vm.Id == playlist.Id);

            if (existingVm != null)
            {
                // Обновляем существующую карточку (все поля: название, обложка, sync, треки)
                existingVm.UpdateFrom(freshPlaylist, trackCount);
            }
            else
            {
                // Новый плейлист — создаём карточку и вставляем в правильную позицию
                var vm = CreatePlaylistCardVm(freshPlaylist, trackCount);
                int insertIndex = CalculateInsertIndex(freshPlaylist);

                if (insertIndex >= Playlists.Count)
                    Playlists.Add(vm);
                else
                    Playlists.Insert(insertIndex, vm);

                // Плавная анимация появления (карточка начинает с IsVisible=false)
                await Task.Delay(50);
                vm.Show();
            }

            // Статистика обновится через OnDataChanged (throttled 500ms)
            // Не вызываем здесь чтобы избежать двойного запуска анимации
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Ошибка инкрементального обновления: {ex.Message}");
            await LoadPlaylistsAsync();
        }
    }

    /// <summary>
    /// Инкрементально удаляет карточку плейлиста из коллекции.
    /// Статистика обновится автоматически через <c>OnDataChanged</c>.
    /// </summary>
    /// <param name="playlistId">ID удалённого плейлиста.</param>
    private void OnPlaylistRemovedIncremental(string playlistId)
    {
        if (_isDisposed) return;

        var vm = Playlists.FirstOrDefault(x => x.Id == playlistId);
        if (vm != null)
        {
            vm.Dispose();
            Playlists.Remove(vm);
        }
    }

    /// <summary>
    /// Определяет индекс вставки для нового плейлиста с учётом сортировки.
    /// 
    /// <para><b>Порядок сортировки:</b></para>
    /// <list type="number">
    ///   <item>Liked (всегда первый, index=0)</item>
    ///   <item>Локальные плейлисты (<c>IsLocal=true</c>), по имени ASC</item>
    ///   <item>Облачные плейлисты (<c>IsLocal=false</c>), по имени ASC</item>
    /// </list>
    /// 
    /// <para><b>Сложность:</b> O(n) где n — количество плейлистов в коллекции.</para>
    /// </summary>
    private int CalculateInsertIndex(Core.Models.Playlist playlist)
    {
        if (playlist.Id == LibraryService.LikedPlaylistId) return 0;

        int index = 0;
        foreach (var vm in Playlists)
        {
            // Liked всегда первый — пропускаем
            if (vm.IsLikedPlaylist) { index++; continue; }

            // Локальные плейлисты группируются перед облачными
            if (playlist.IsLocal && !vm.IsLocal) break;
            if (!playlist.IsLocal && vm.IsLocal) { index++; continue; }

            // В пределах одной группы — алфавитный порядок
            if (string.Compare(playlist.Name, vm.Name, StringComparison.Ordinal) < 0) break;
            index++;
        }

        return index;
    }

    #endregion

    #region Создание плейлиста

    /// <summary>
    /// Открывает диалог создания нового плейлиста.
    /// 
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Показывает диалог <see cref="CreatePlaylistDialog"/></item>
    ///   <item>Если пользователь хочет облачный плейлист — создаёт через YouTube API</item>
    ///   <item>При ошибке облака — предлагает создать локально</item>
    ///   <item>Создаёт локальный плейлист в БД</item>
    ///   <item>Сохраняет (триггерит <c>OnPlaylistChanged</c> → инкрементальное обновление)</item>
    /// </list>
    /// </summary>
    private async Task OpenCreateDialogAsync()
    {
        if (_isDisposed) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var trimmedName = result.Name.Trim();
        bool wantsCloud = result.SyncToCloud && _auth.IsAuthenticated;
        string? youtubeId = null;

        // ═══ STEP 1: Создание в облаке (если требуется) ═══
        if (wantsCloud)
        {
            _mainWindow.LockNavigation(SL["Playlist_CreatingCloud"] ?? "Создание в облаке...");
            try
            {
                youtubeId = await _youtube.CreatePlaylistAsync(trimmedName);

                if (string.IsNullOrEmpty(youtubeId))
                {
                    wantsCloud = false;
                    var createLocal = await OfferLocalFallbackAsync(
                        SL["Playlist_CloudCreateFailed_AskLocal"]
                            ?? "Не удалось создать плейлист в YouTube Music. Создать локально?");
                    if (!createLocal) return;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Library] Ошибка создания облачного плейлиста: {ex.Message}");
                wantsCloud = false;

                var createLocal = await OfferLocalFallbackAsync(
                    string.Format(
                        SL["Playlist_CloudError_AskLocal"]
                            ?? "Ошибка YouTube API: {0}\n\nСоздать локально?",
                        ex.Message));
                if (!createLocal) return;
            }
            finally
            {
                _mainWindow.UnlockNavigation();
            }
        }

        // ═══ STEP 2: Создание локального плейлиста ═══
        var playlist = await _library.CreatePlaylistAsync(trimmedName);

        if (wantsCloud && !string.IsNullOrEmpty(youtubeId))
        {
            playlist.YoutubeId = youtubeId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
        }
        else
        {
            playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        }

        // ═══ STEP 3: Сохранение ═══
        // AddOrUpdatePlaylistAsync вызовет OnPlaylistChanged →
        // → OnPlaylistChangedIncremental добавит карточку в UI
        await _library.AddOrUpdatePlaylistAsync(playlist);

        Log.Info($"[Library] Создан плейлист '{trimmedName}' " +
                 $"(Sync={playlist.SyncMode}, YtId={playlist.YoutubeId ?? "none"})");
    }

    /// <summary>
    /// Предлагает пользователю создать плейлист локально при ошибке облака.
    /// </summary>
    /// <param name="message">Текст сообщения для диалога.</param>
    /// <returns><c>true</c> если пользователь согласен создать локально.</returns>
    private async Task<bool> OfferLocalFallbackAsync(string message)
    {
        return await _dialog.ConfirmAsync(
            SL["Dialog_Warning_Title"] ?? "Предупреждение",
            message,
            SL["Playlist_CreateLocal"] ?? "Создать локально",
            SL["Button_Cancel"] ?? "Отмена");
    }

    #endregion

    #region Обработчики событий

    /// <summary>
    /// Обработчик изменения состояния авторизации.
    /// Обновляет свойство <see cref="IsAuthenticated"/> для UI.
    /// </summary>
    private void OnAuthChanged()
    {
        if (_isDisposed) return;
        IsAuthenticated = _auth.IsAuthenticated;
    }

    #endregion

    #region Синхронизация с YouTube

    /// <summary>
    /// Полная синхронизация плейлистов с YouTube-аккаунтом.
    /// 
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Получает список плейлистов (через auth API или публичный канал)</item>
    ///   <item>Показывает диалог выбора плейлистов для импорта</item>
    ///   <item>Параллельно синхронизирует Liked-треки</item>
    ///   <item>Импортирует выбранные плейлисты (merge/duplicate/skip)</item>
    ///   <item>По завершении выполняет полную перезагрузку UI</item>
    /// </list>
    /// 
    /// <para><b>Прогресс:</b></para>
    /// <list type="bullet">
    ///   <item>0.0–0.1: загрузка списка плейлистов</item>
    ///   <item>0.1–0.2: подготовка и показ диалога</item>
    ///   <item>0.2–0.25: старт фоновой синхронизации лайков</item>
    ///   <item>0.25–1.0: импорт плейлистов</item>
    /// </list>
    /// </summary>
    private async Task SyncAccountPlaylistsAsync()
    {
        if (_isDisposed) return;

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        IsSyncing = true;
        IsStatsVisible = false;
        SyncProgress = 0;
        SyncStatus = SL["Sync_FetchingPlaylists"];

        _mainWindow.LockNavigation(SL["Sync_InProgress"]);

        try
        {
            List<PlaylistSearchResult> playlistsToImport = [];
            Dictionary<string, int>? trackCounts = null;

            // ═══ Получение списка плейлистов ═══
            if (_auth.IsAuthenticated)
            {
                try
                {
                    var ytPlaylists = await YoutubeProvider.GetUserPlaylistsByAuthAsync();
                    ct.ThrowIfCancellationRequested();
                    SyncProgress = 0.1;

                    // Фильтруем системные плейлисты YouTube:
                    // LM = Liked Music, VLLM = Video Liked List, RD* = Radio/Mix
                    var filtered = ytPlaylists
                        .Where(p =>
                            !string.IsNullOrEmpty(p.YoutubeId) &&
                            p.YoutubeId != "LM" &&
                            p.YoutubeId != "VLLM" &&
                            !p.YoutubeId.StartsWith("RD"))
                        .ToList();

                    // Конвертируем в PlaylistSearchResult С ОБЛОЖКАМИ
                    // (ранее передавался пустой [] — обложки не отображались в диалоге)
                    playlistsToImport = [.. filtered.Select(p =>
        {
            var pid = new Core.Youtube.Playlists.PlaylistId(p.YoutubeId!);

            // Передаём обложку из Playlist.ThumbnailUrl
            var thumbs = new List<Thumbnail>();
            if (!string.IsNullOrEmpty(p.ThumbnailUrl))
                thumbs.Add(new Thumbnail(
                    p.ThumbnailUrl, new Resolution(0, 0)));

            return new PlaylistSearchResult(pid, p.Name, null, thumbs);
        })];

                    // Собираем словарь кол-ва треков для отображения в диалоге
                    // Playlist.TrackCount заполняется YoutubeUserDataService
                    trackCounts = new Dictionary<string, int>();
                    foreach (var p in filtered)
                    {
                        if (!string.IsNullOrEmpty(p.YoutubeId) && p.TrackCount > 0)
                            trackCounts[p.YoutubeId] = p.TrackCount;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (!_isDisposed)
                        await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"],
                            SL["Sync_Error_API"] + ": " + ex.Message);
                    return;
                }
            }
            else if (_library.HasFakeAccount &&
                     !string.IsNullOrEmpty(_library.Settings.FakeAccountChannelUrl))
            {
                // Режим без авторизации: синхронизация публичных плейлистов канала
                var result = await _youtube.GetChannelPlaylistsForSyncAsync(
                    _library.Settings.FakeAccountChannelUrl, ct);
                ct.ThrowIfCancellationRequested();
                SyncProgress = 0.1;
                if (result != null) playlistsToImport = result.Value.Playlists;
            }
            else
            {
                if (!_isDisposed)
                    await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Sync_ConfigureUrlFirst"]);
                return;
            }

            // ═══ Проверка пустого результата ═══
            if (playlistsToImport.Count == 0)
            {
                if (_auth.IsAuthenticated)
                {
                    var confirmSyncLikes = await _dialog.ConfirmAsync(
                        SL["Sync_ConfirmLikedOnly"] ?? "Плейлисты не найдены",
                        SL["Sync_NoPlaylistsFound_AskLiked"] ?? "Синхронизировать лайки?",
                        SL["Common_Yes"] ?? "Да",
                        SL["Common_No"] ?? "Нет");

                    if (confirmSyncLikes)
                    {
                        SyncStatus = SL["Sync_LikedSongs"];
                        await _manager.SyncLikedTracksAsync();
                        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"],
                            SL["Sync_Success_Msg_LikedOnly"] ?? "Понравившиеся песни синхронизированы.");
                    }
                }
                else
                {
                    if (!_isDisposed)
                        await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Sync_NoPlaylists"]);
                }
                return;
            }

            // ═══ Диалог выбора плейлистов ═══
            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.15;
            SyncStatus = SL["Sync_SelectPlaylists"];

            var allPlaylists = await _library.GetAllPlaylistsAsync(ct);
            var existingLocal = allPlaylists
                .Where(p => p.IsLocal)
                .ToDictionary(p => p.Name, p => p);
            var localNames = new HashSet<string>(existingLocal.Keys, StringComparer.Ordinal);

            var decisions = await _dialog.ShowSyncSelectionAsync(playlistsToImport, localNames);
            if (decisions.Count == 0 || _isDisposed) return;

            // ═══ Параллельная синхронизация лайков ═══
            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.2;

            Task? likedSongsSyncTask = null;
            if (_auth.IsAuthenticated)
            {
                likedSongsSyncTask = Task.Run(async () =>
                {
                    try { await _manager.SyncLikedTracksAsync(); }
                    catch (Exception ex) { Log.Error($"[Sync] Ошибка синхронизации лайков: {ex.Message}"); }
                }, ct);
            }

            // ═══ Импорт выбранных плейлистов ═══
            SyncProgress = 0.25;
            SyncStatus = SL["Sync_ImportingPlaylists"];

            int importedCount = 0, mergedCount = 0, processed = 0;
            int totalToProcess = decisions.Count;

            foreach (var decision in decisions)
            {
                ct.ThrowIfCancellationRequested();
                if (_isDisposed) return;

                SyncStatus = string.Format(SL["Sync_ImportingPlaylist"], decision.Playlist.Title);

                var fullPlaylist = await _youtube.ImportPlaylistAsync(
                    decision.Playlist.Id.Value, _auth.IsAuthenticated, ct);
                if (fullPlaylist == null) { processed++; continue; }

                existingLocal.TryGetValue(decision.Playlist.Title, out var existing);

                if (decision.Action == MergeAction.Merge && existing != null)
                {
                    // Объединение: добавляем только новые треки в существующий плейлист
                    var existingTrackSet = new HashSet<string>(existing.TrackIds);
                    bool changed = false;
                    foreach (var trackId in fullPlaylist.TrackIds)
                    {
                        if (existingTrackSet.Add(trackId))
                        {
                            existing.TrackIds.Add(trackId);
                            changed = true;
                        }

                        var t = await _library.GetTrackAsync(trackId, ct);
                        if (t != null && !t.InPlaylists.Contains(existing.Id))
                        {
                            t.InPlaylists.Add(existing.Id);
                            await _library.AddOrUpdateTrackAsync(t, ct);
                        }
                    }

                    if (changed)
                    {
                        existing.UpdatedAt = DateTime.Now;
                        await _library.AddOrUpdatePlaylistAsync(existing, ct);
                    }

                    mergedCount++;
                }
                else
                {
                    // Импорт как новый плейлист (с возможным переименованием при дубликате)
                    if (decision.Action == MergeAction.Duplicate && existing != null)
                        fullPlaylist.Name = $"{decision.Playlist.Title} ({SL["Sync_DuplicateName"]})";

                    if (_auth.IsAuthenticated)
                    {
                        fullPlaylist.SyncMode = PlaylistSyncMode.TwoWaySync;
                        fullPlaylist.YoutubeId = decision.Playlist.Id.Value;
                    }

                    await _library.AddOrUpdatePlaylistAsync(fullPlaylist, ct);
                    importedCount++;
                }

                processed++;
                SyncProgress = 0.25 + (0.75 * processed / totalToProcess);
            }

            // ═══ Ожидание завершения синхронизации лайков ═══
            if (likedSongsSyncTask != null)
            {
                if (!likedSongsSyncTask.IsCompleted)
                    SyncStatus = SL["Sync_FinalizingLikedSongs"];
                await likedSongsSyncTask;
            }

            SyncProgress = 1.0;
            SyncStatus = SL["Sync_Complete"];

            if (!_isDisposed)
            {
                // Заменили _dialog.ShowInfoAsync на _notifications.ShowToastAsync
                await _notifications.ShowToastAsync(
                    "Sync_Complete_Title",
                    "Sync_Success_Msg", // Формат: Импортировано: {0}, Объединено: {1}
                    Core.Models.NotificationSeverity.Success,
                    durationMs: 5000,
                    messageArgs: [importedCount, mergedCount]);
            }
        }
        catch (OperationCanceledException)
        {
            SyncStatus = SL["Sync_Cancelled"];
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Ошибка синхронизации: {ex.Message}");
            if (!_isDisposed)
            {
                await _notifications.ShowToastAsync(
                    "Dialog_Error_Title",
                    "Sync_Error_API", // Ключ ошибки
                    Core.Models.NotificationSeverity.Error,
                    durationMs: 6000,
                    messageArgs: [ex.Message]);
            }
        }
        finally
        {
            await Task.Delay(300);
            _mainWindow.UnlockNavigation();
            if (!_isDisposed)
            {
                IsSyncing = false;
                SyncProgress = 0;
                SyncStatus = "";
                // После массовой синхронизации — полная перезагрузка
                await LoadPlaylistsAsync();
            }
        }
    }

    #endregion

    #region Статистика

    /// <summary>
    /// Анимирует статистику от предыдущих значений к текущим с плавной интерполяцией.
    /// 
    /// <para><b>Алгоритм интерполяции:</b></para>
    /// <list type="bullet">
    ///   <item>Кривая: cubic ease-out (<c>1 - (1-t)³</c>) — быстрый старт, плавное завершение</item>
    ///   <item>Запоминает предыдущие значения для delta-анимации</item>
    ///   <item>При малой разнице (1-3 шт) анимация короче (15 шагов vs 25)</item>
    ///   <item>Частота обновления: ~60fps (16ms между кадрами)</item>
    /// </list>
    /// 
    /// <para><b>Оптимизация:</b> использует <see cref="LibraryService.GetPlaylistTotalDurationAsync"/>
    /// (SQL-агрегация) вместо загрузки всех треков в память.</para>
    /// 
    /// <para><b>Отмена:</b> при повторном вызове предыдущая анимация отменяется через CTS.</para>
    /// </summary>
    private async Task UpdateStatsAnimatedAsync()
    {
        if (_isDisposed) return;

        // Отменяем предыдущую анимацию
        _statsAnimCts?.Cancel();
        _statsAnimCts?.Dispose();
        _statsAnimCts = new CancellationTokenSource();
        var ct = _statsAnimCts.Token;

        // Целевые значения из текущей коллекции карточек
        var targetPlaylists = Playlists.Count;
        var targetTracks = Playlists.Sum(p => p.TrackCount);

        // Получаем общую длительность через SQL-агрегацию (не загружая треки в память)
        var totalDuration = TimeSpan.Zero;
        foreach (var vm in Playlists)
        {
            try
            {
                var duration = await _library.GetPlaylistTotalDurationAsync(vm.Id);
                totalDuration += duration;
            }
            catch { /* плейлист мог быть удалён между итерациями */ }
        }

        if (ct.IsCancellationRequested || _isDisposed) return;

        // Вычисляем средние значения
        var avgTrack = targetTracks > 0
            ? TimeSpan.FromTicks(totalDuration.Ticks / targetTracks)
            : TimeSpan.Zero;
        var avgPlaylist = targetPlaylists > 0
            ? TimeSpan.FromTicks(totalDuration.Ticks / targetPlaylists)
            : TimeSpan.Zero;

        IsStatsVisible = true;

        // Интерполяция от предыдущих к целевым значениям
        int startPlaylists = _prevPlaylistCount;
        int startTracks = _prevTrackCount;

        // Адаптивное количество шагов: меньше при малой разнице
        int diff = Math.Abs(targetPlaylists - startPlaylists)
                 + Math.Abs(targetTracks - startTracks);
        int steps = diff <= 3 ? 15 : 25;

        for (int i = 1; i <= steps; i++)
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            // Cubic ease-out: быстрый старт, плавное замедление
            double t = (double)i / steps;
            double ease = 1 - Math.Pow(1 - t, 3);

            int currentPlaylists = startPlaylists
                + (int)Math.Round((targetPlaylists - startPlaylists) * ease);
            int currentTracks = startTracks
                + (int)Math.Round((targetTracks - startTracks) * ease);

            PlaylistCountText = SL.GetPlural("Library_PlaylistWord", currentPlaylists);
            TotalTracksText = SL.GetPlural("Library_TrackWord", currentTracks);

            // Длительность показываем сразу (она не анимируется)
            TotalDurationText = FormatDurationLocalized(totalDuration);

            if (i == steps)
            {
                // Финальные точные значения
                PlaylistCountText = SL.GetPlural("Library_PlaylistWord", targetPlaylists);
                TotalTracksText = SL.GetPlural("Library_TrackWord", targetTracks);
                AvgTrackDurationText = $"⌀ {SL["Library_AvgTrack"]}: {FormatDurationShort(avgTrack)}";
                AvgPlaylistDurationText = $"⌀ {SL["Library_AvgPlaylist"]}: {FormatDurationLocalized(avgPlaylist)}";
            }
            else
            {
                AvgTrackDurationText = "";
                AvgPlaylistDurationText = "";
            }

            try { await Task.Delay(16, ct); }
            catch (OperationCanceledException) { break; }
        }

        // Запоминаем для следующей анимации
        _prevPlaylistCount = targetPlaylists;
        _prevTrackCount = targetTracks;
    }

    /// <summary>
    /// Форматирует длительность в локализованный текст.
    /// Примеры: "2 ч 15 мин", "45 мин", "30 сек".
    /// </summary>
    private static string FormatDurationLocalized(TimeSpan ts)
    {
        var h = (int)ts.TotalHours;
        var m = ts.Minutes;
        var s = ts.Seconds;

        if (h > 0) return $"{h} {SL["Time_Hours_Short"]} {m} {SL["Time_Minutes_Short"]}";
        if (m > 0) return $"{m} {SL["Time_Minutes_Short"]}";
        return $"{s} {SL["Time_Seconds_Short"]}";
    }

    /// <summary>
    /// Форматирует длительность в короткий формат: "1:23:45" или "3:45".
    /// Используется для средних значений.
    /// </summary>
    private static string FormatDurationShort(TimeSpan ts)
    {
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    #endregion

    #region Полная загрузка

    /// <summary>
    /// Полная перезагрузка списка плейлистов с diff-алгоритмом.
    /// 
    /// <para><b>Используется:</b></para>
    /// <list type="bullet">
    ///   <item>При начальной загрузке страницы</item>
    ///   <item>После массовой синхронизации</item>
    ///   <item>Как fallback при ошибке инкрементального обновления</item>
    /// </list>
    /// 
    /// <para><b>Алгоритм (diff):</b></para>
    /// <list type="number">
    ///   <item>Загружает все плейлисты из БД с подсчётом треков</item>
    ///   <item>Сортирует: Liked → Local → Cloud, по имени ASC</item>
    ///   <item>Удаляет карточки, которых нет в новых данных</item>
    ///   <item>Обновляет существующие карточки и корректирует их позиции</item>
    ///   <item>Создаёт новые карточки с stagger-анимацией появления</item>
    ///   <item>Запускает анимацию статистики</item>
    /// </list>
    /// </summary>
    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed) return;

        _staggerCts?.Cancel();
        _staggerCts?.Dispose();
        _staggerCts = new CancellationTokenSource();
        var ct = _staggerCts.Token;

        IsStatsVisible = false;

        var allPlaylistsWithCounts = await _library.GetAllPlaylistsWithCountsAsync();
        if (_isDisposed || ct.IsCancellationRequested) return;

        // Сортировка: Liked → Local → Name ASC
        var sorted = allPlaylistsWithCounts
            .OrderByDescending(x => x.Playlist.Id == LibraryService.LikedPlaylistId)
            .ThenByDescending(x => x.Playlist.IsLocal)
            .ThenBy(x => x.Playlist.Name)
            .ToList();

        // ═══ Diff: удаление ═══
        var existingDict = Playlists.ToDictionary(vm => vm.Id);
        var newIdSet = new HashSet<string>(sorted.Select(x => x.Playlist.Id));

        var toRemove = Playlists.Where(vm => !newIdSet.Contains(vm.Id)).ToList();
        foreach (var vm in toRemove)
        {
            vm.Dispose();
            Playlists.Remove(vm);
        }

        // ═══ Diff: обновление существующих + сбор новых ═══
        var newItems = new List<(PlaylistCardViewModel vm, int targetIndex)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (playlist, trackCount) = sorted[i];

            if (existingDict.TryGetValue(playlist.Id, out var existingVm))
            {
                // Обновляем данные существующей карточки
                existingVm.UpdateFrom(playlist, trackCount);

                // Корректируем позицию если нужно
                int currentIndex = Playlists.IndexOf(existingVm);
                if (currentIndex != i && currentIndex >= 0 && i < Playlists.Count)
                {
                    Playlists.Move(currentIndex, Math.Min(i, Playlists.Count - 1));
                }
            }
            else
            {
                var vm = CreatePlaylistCardVm(playlist, trackCount);
                newItems.Add((vm, i));
            }
        }

        // ═══ Вставка новых карточек ═══
        foreach (var (vm, targetIndex) in newItems)
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            if (targetIndex >= Playlists.Count)
                Playlists.Add(vm);
            else
                Playlists.Insert(targetIndex, vm);
        }

        // ═══ Stagger-анимация появления новых карточек ═══
        if (newItems.Count > 0)
        {
            foreach (var (vm, _) in newItems)
            {
                if (ct.IsCancellationRequested || _isDisposed) return;
                vm.Show();
                try { await Task.Delay(StaggerDelayMs, ct); }
                catch (OperationCanceledException)
                {
                    // При отмене — мгновенно показываем все оставшиеся
                    foreach (var (remaining, _) in newItems.Where(x => !x.vm.IsVisible))
                        remaining.Show();
                    break;
                }
            }
        }

        // ═══ Запуск анимации статистики ═══
        if (!_isDisposed && !ct.IsCancellationRequested)
        {
            UpdateStatsInBackground();
        }
    }

    #endregion

    #region Фабрика карточек

    /// <summary>
    /// Создаёт <see cref="PlaylistCardViewModel"/> для плейлиста
    /// с привязкой всех callback-ов (open, play, enqueue, delete, edit).
    /// </summary>
    private PlaylistCardViewModel CreatePlaylistCardVm(
        Core.Models.Playlist playlist, int trackCount)
    {
        return new PlaylistCardViewModel(
            playlist,
            trackCount,
            onOpen: _mainWindow.NavigateToPlaylist,
            addToQueueAction: async (p) =>
            {
                var tracks = await _library.GetPlaylistTracksAsync(p.Id);
                _audio.EnqueueRange(tracks);
            },
            playAction: async (p) =>
            {
                var tracks = await _library.GetPlaylistTracksAsync(p.Id);
                if (tracks.Count > 0)
                    await _audio.StartQueueAsync(tracks, tracks[0]);
            },
            onDelete: DeletePlaylistAsync,
            onEdit: EditPlaylistFromCardAsync);
    }

    #endregion

    #region Редактирование и удаление

    /// <summary>
    /// Редактирование плейлиста из карточки в библиотеке.
    /// Делегирует всю логику в <see cref="PlaylistEditService"/>.
    /// Инкрементальное обновление происходит автоматически через <c>OnPlaylistChanged</c>.
    /// </summary>
    private async Task EditPlaylistFromCardAsync(Core.Models.Playlist playlist)
    {
        if (_isDisposed) return;

        await _editService.EditPlaylistAsync(
            playlist.Id,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);

        // Не нужно вызывать LoadPlaylistsAsync() —
        // PlaylistEditService вызывает AddOrUpdatePlaylistAsync,
        // что триггерит OnPlaylistChanged → OnPlaylistChangedIncremental
    }

    /// <summary>
    /// Удаление плейлиста с подтверждением.
    /// Блокирует навигацию на время удаления.
    /// Инкрементальное удаление карточки через <c>OnPlaylistRemoved</c>.
    /// </summary>
    private async Task DeletePlaylistAsync(string playlistId)
    {
        if (_isDisposed) return;
        var playlist = await _library.GetPlaylistAsync(playlistId);
        if (playlist == null) return;

        // Запрет удаления системного плейлиста "Понравившиеся"
        if (playlistId == "liked")
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Playlist_CannotDeleteLiked"]);
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"],
            string.Format(SL["Playlist_DeleteConfirm"], playlist.Name),
            SL["Button_Delete"], SL["Button_Cancel"]);

        if (!confirmed) return;

        _mainWindow.LockNavigation(SL["Playlist_Deleting"]);
        try
        {
            // MusicLibraryManager удаляет из БД и YouTube,
            // LibraryService.DeletePlaylistAsync вызывает OnPlaylistRemoved
            await _manager.DeletePlaylistAsync(playlistId);
        }
        finally { _mainWindow.UnlockNavigation(); }
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Освобождает ресурсы: отменяет все CTS, очищает коллекции,
    /// отписывается от событий.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            _staggerCts?.Cancel();
            _staggerCts?.Dispose();
            _statsAnimCts?.Cancel();
            _statsAnimCts?.Dispose();
            foreach (var vm in Playlists) vm.Dispose();
            Playlists.Clear();
            _auth.OnAuthStateChanged -= OnAuthChanged;
            _syncCts?.Cancel();
            _syncCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion
}