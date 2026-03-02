using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LMP.Core.Models;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Search;
using LMP.UI.Dialogs;
using LMP.UI.Dialogs.Content;
using LMP.Features.Shell;

namespace LMP.Core.Services;

/// <summary>
/// Централизованный сервис для показа диалоговых окон.
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item><b>Overlay-диалоги</b> — рендерятся через DialogHost поверх контента,
///     но ПОД TopBar и PlayerBar. TopBar, уведомления и кнопки окна остаются активными.
///     Навигация блокируется автоматически.</item>
///   <item><b>Модальные окна</b> — BotDetection и StreamUnavailable.
///     Блокируют ВСЁ приложение. Используются только для критичных ошибок.</item>
/// </list>
/// 
/// <para><b>Паттерн для overlay-диалогов:</b></para>
/// <code>
/// 1. Создать Content/ViewModel с callback'ом (OnResult / OnClose)
/// 2. Установить callback → tcs.TrySetResult() + dialogHost.CloseDialog()
/// 3. Вызвать dialogHost.ShowAsync() (fire-and-forget)
/// 4. await tcs.Task для получения результата
/// </code>
/// 
/// <para><b>Почему два шага (TCS + ShowAsync):</b></para>
/// <para>ShowAsync ожидает закрытия диалога, но нам нужен типизированный результат.
/// TCS позволяет получить конкретный тип (bool, string?, List и т.д.)
/// независимо от generic-параметра ShowAsync.</para>
/// </summary>
public sealed class DialogService
{
    private static LocalizationService L => LocalizationService.Instance;

    private readonly CookieAuthService _authService;
    private readonly NotificationService _notifications;
    private readonly Func<DialogHostViewModel> _getDialogHost;

    /// <summary>
    /// Активный диалог BotDetection — синглтон для предотвращения дублей.
    /// </summary>
    private BotDetectionDialog? _activeBotDetectionDialog;
    private readonly Lock _botDetectionLock = new();

    /// <param name="authService">Сервис авторизации — для проверки состояния входа в YouTube.</param>
    /// <param name="notifications">Сервис уведомлений — для toast-сообщений внутри диалогов синхронизации.</param>
    /// <param name="clipboard">Сервис буфера обмена — для копирования ссылок в диалогах.</param>
    /// <param name="getDialogHost">
    /// Lazy accessor для DialogHostViewModel.
    /// Используется <c>Func</c> вместо прямой инъекции из-за циклической зависимости:
    /// DialogService создаётся раньше MainWindowViewModel.
    /// </param>
    public DialogService(
        CookieAuthService authService,
        NotificationService notifications,
        Func<DialogHostViewModel> getDialogHost)
    {
        _authService = authService;
        _notifications = notifications;
        _getDialogHost = getDialogHost;
    }

    #region Helpers

    /// <summary>
    /// Получает главное окно если оно загружено и видимо.
    /// Используется только для модальных диалогов (BotDetection, StreamUnavailable).
    /// </summary>
    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window is { IsLoaded: true, IsVisible: true })
                return window;
        }
        return null;
    }

    /// <summary>
    /// Безопасный показ модального Window-based диалога.
    /// Включает Task.Yield() для корректной работы с Avalonia event loop.
    /// </summary>
    private static async Task<T?> ShowModalSafeAsync<T>(Window dialog, Window owner)
    {
        try
        {
            await Task.Yield();
            return await dialog.ShowDialog<T?>(owner);
        }
        catch (Exception ex)
        {
            Log.Error($"[Dialog] Modal dialog error: {ex.Message}");
            return default;
        }
    }

    #endregion

    #region Basic Dialogs (Overlay)

    /// <summary>
    /// Показывает диалог подтверждения (Yes/No, OK/Cancel).
    /// </summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="message">Текст сообщения.</param>
    /// <param name="confirmText">Текст кнопки подтверждения.</param>
    /// <param name="cancelText">Текст кнопки отмены.</param>
    /// <returns><c>true</c> если подтверждено, <c>false</c> если отменено.</returns>
    public async Task<bool> ConfirmAsync(
        string title, string message,
        string confirmText, string cancelText)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<bool>();

        var content = new ConfirmDialogContent(
            title, message, confirmText, cancelText,
            onResult: result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            });

        _ = host.ShowAsync<object>(content);
        return await tcs.Task;
    }

    /// <summary>
    /// Показывает диалог подтверждения со стандартными кнопками OK / Cancel.
    /// </summary>
    public Task<bool> ConfirmAsync(string title, string message) =>
        ConfirmAsync(title, message, L["Common_OK"], L["Common_Cancel"]);

    /// <summary>
    /// Показывает информационный диалог с одной кнопкой.
    /// </summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="message">Текст сообщения.</param>
    /// <param name="buttonText">Текст кнопки закрытия.</param>
    public async Task ShowInfoAsync(string title, string message, string buttonText)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<object?>();

        var content = new InfoDialogContent(
            title, message, buttonText,
            onClose: () =>
            {
                tcs.TrySetResult(null);
                host.CloseDialog();
            });

        _ = host.ShowAsync<object>(content);
        await tcs.Task;
    }

    /// <summary>
    /// Показывает информационный диалог со стандартной кнопкой OK.
    /// </summary>
    public Task ShowInfoAsync(string title, string message) =>
        ShowInfoAsync(title, message, L["Common_OK"]);

    /// <summary>
    /// Показывает диалог ввода текста.
    /// </summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="prompt">Подсказка над полем ввода.</param>
    /// <param name="watermark">Placeholder в поле ввода.</param>
    /// <returns>Введённый текст, или <c>null</c> при отмене.</returns>
    public async Task<string?> ShowInputAsync(
        string title, string prompt, string? watermark = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<string?>();

        var content = new InputDialogContent(
            title, prompt,
            watermark ?? L["Input_Watermark"],
            onResult: result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            });

        _ = host.ShowAsync<object>(content);
        return await tcs.Task;
    }

    /// <summary>
    /// Показывает системный диалог выбора папки.
    /// Это единственный диалог использующий нативный OS picker.
    /// </summary>
    /// <param name="startPath">Начальный путь (опционально).</param>
    /// <returns>Выбранный путь, или <c>null</c> при отмене.</returns>
    public async Task<string?> SelectFolderAsync(string? startPath = null)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return null;

            var storage = window.StorageProvider;
            IStorageFolder? suggested = null;

            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                suggested = await storage.TryGetFolderFromPathAsync(startPath);

            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L["Dialog_SelectDownloadFolder_Title"],
                SuggestedStartLocation = suggested,
                AllowMultiple = false
            });

            return result.Count > 0 ? result[0].Path.LocalPath : null;
        });
    }

    #endregion

    #region Playlist Dialogs (Overlay)

    /// <summary>
    /// Диалог создания нового плейлиста.
    /// Включает PlaylistEditorControl и опциональный переключатель синхронизации.
    /// </summary>
    /// <returns>Результат создания, или <c>null</c> при отмене.</returns>
    public async Task<CreatePlaylistResult?> ShowCreatePlaylistDialogAsync()
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<CreatePlaylistResult?>();

        var vm = new CreatePlaylistDialogViewModel(_authService.IsAuthenticated)
        {
            OnResult = result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог удаления плейлиста.
    /// Предлагает удалить только локально или также из YouTube.
    /// </summary>
    /// <param name="playlist">Удаляемый плейлист.</param>
    /// <param name="isAuthenticated">Авторизован ли пользователь (влияет на доступность опции «удалить из облака»).</param>
    /// <returns>Выбранный вариант удаления, или <c>null</c> при отмене.</returns>
    public async Task<DeletePlaylistResult?> ShowDeletePlaylistDialogAsync(
        Playlist playlist, bool isAuthenticated)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<DeletePlaylistResult?>();

        var vm = new DeletePlaylistDialogViewModel(playlist, isAuthenticated)
        {
            OnResult = result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог «Добавить трек в плейлист» — список плейлистов с чекбоксами.
    /// </summary>
    /// <param name="track">Трек для добавления.</param>
    /// <returns>Список ID плейлистов. Пустой при отмене.</returns>
    public async Task<List<string>> ShowAddToPlaylistDialogAsync(TrackInfo track)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<List<string>>();

        var library = Microsoft.Extensions.DependencyInjection
            .ServiceProviderServiceExtensions
            .GetRequiredService<LibraryService>(Program.Services);
        var playlists = await library.GetAllPlaylistsAsync();

        var vm = new AddToPlaylistDialogViewModel(track, playlists)
        {
            OnResult = result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог редактирования плейлиста: название, обложка, описание, цвет, синхронизация.
    /// Загружает до 200 треков для CoverPicker (выбор мозаики из обложек треков).
    /// </summary>
    /// <param name="playlist">Редактируемый плейлист.</param>
    /// <returns>Результат редактирования, или <c>null</c> при отмене.</returns>
    public async Task<EditPlaylistResult?> ShowEditPlaylistDialogAsync(Playlist playlist)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<EditPlaylistResult?>();

        // Загружаем треки для CoverPicker (мозаика из обложек)
        IReadOnlyList<TrackInfo>? tracks = null;
        try
        {
            var library = Microsoft.Extensions.DependencyInjection
                .ServiceProviderServiceExtensions
                .GetRequiredService<LibraryService>(Program.Services);
            var loaded = await library.GetPlaylistTracksAsync(playlist.Id, limit: 200);
            if (loaded.Count > 0) tracks = loaded;
        }
        catch (Exception ex)
        {
            Log.Warn($"[DialogService] CoverPicker tracks load failed: {ex.Message}");
        }

        var vm = new EditPlaylistDialogViewModel(
            playlist, _authService.IsAuthenticated, tracks)
        {
            OnResult = result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Sync Dialogs (Overlay)

    /// <summary>
    /// Диалог массовой синхронизации: выбор плейлистов + разрешение конфликтов.
    /// Включает поиск, глобальные шаблоны действий и копирование ссылок.
    /// </summary>
    /// <param name="items">Плейлисты с YouTube для выбора.</param>
    /// <param name="existingLocalNames">Имена локальных плейлистов — для определения конфликтов.</param>
    /// <param name="trackCounts">Словарь {YouTubePlaylistId → кол-во треков} (опционально).</param>
    /// <returns>Список решений (Skip-элементы исключены). Пустой при отмене.</returns>
    public async Task<List<SyncDecision>> ShowSyncSelectionAsync(
        IEnumerable<PlaylistSearchResult> items,
        ISet<string> existingLocalNames,
        IReadOnlyDictionary<string, int>? trackCounts = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<List<SyncDecision>>();

        var vm = new SyncSelectionViewModel(
            _notifications,
            items, existingLocalNames, trackCounts)
        {
            OnResult = result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог синхронизации одного плейлиста: preview различий + выбор стратегии.
    /// </summary>
    /// <param name="preview">Снимок различий между локальным и облачным состоянием.</param>
    /// <returns>Опции синхронизации, или <c>null</c> при отмене.</returns>
    public async Task<PlaylistSyncOptions?> ShowPlaylistSyncDialogAsync(
        PlaylistSyncPreview preview)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<PlaylistSyncOptions?>();

        var vm = new SyncPlaylistDialogViewModel(preview)
        {
            OnResult = result =>
            {
                tcs.TrySetResult(result);
                host.CloseDialog(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Critical Dialogs (Modal Windows)

    /// <summary>
    /// Диалог ожидания bot detection cooldown.
    /// 
    /// <para><b>МОДАЛЬНЫЙ:</b> блокирует ВСЁ окно включая TopBar.</para>
    /// <para>Синглтон — повторные вызовы обновляют существующий диалог.</para>
    /// </summary>
    /// <param name="waitTime">Начальное время ожидания.</param>
    public async Task ShowBotDetectionCooldownAsync(TimeSpan waitTime)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            lock (_botDetectionLock)
            {
                if (_activeBotDetectionDialog is { IsVisible: true })
                {
                    _activeBotDetectionDialog.UpdateCountdown(
                        VideoController.GetRemainingCooldown(),
                        VideoController.CooldownDuration);
                    return;
                }

                _activeBotDetectionDialog = new BotDetectionDialog
                {
                    DialogTitle = L["Dialog_BotDetection_Title"],
                    Message = L["Dialog_BotDetection_Message"],
                    Hint = L["Dialog_BotDetection_Hint"],
                    CloseButtonText = L["Common_OK"]
                };
            }

            _activeBotDetectionDialog.StartCountdown(waitTime);

            try
            {
                await ShowModalSafeAsync<bool?>(_activeBotDetectionDialog, window);
            }
            finally
            {
                lock (_botDetectionLock)
                {
                    _activeBotDetectionDialog = null;
                }
            }
        });
    }

    /// <summary>
    /// Диалог ошибки недоступности стрима.
    /// <para><b>МОДАЛЬНЫЙ:</b> блокирует ВСЁ окно.</para>
    /// </summary>
    /// <param name="exception">Исключение с деталями ошибки.</param>
    public async Task ShowStreamUnavailableAsync(StreamUnavailableException exception)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            var dialog = new StreamUnavailableDialog();
            dialog.ConfigureForException(exception);
            await ShowModalSafeAsync<object>(dialog, window);
        });
    }

    /// <summary>
    /// Диалог общей ошибки воспроизведения.
    /// <para><b>МОДАЛЬНЫЙ:</b> блокирует ВСЁ окно.</para>
    /// </summary>
    public async Task ShowPlaybackErrorAsync(
        string videoId, string errorMessage, Exception? exception = null)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            var dialog = new StreamUnavailableDialog();
            dialog.ConfigureForError(videoId, errorMessage, exception);
            await ShowModalSafeAsync<object>(dialog, window);
        });
    }

    /// <summary>
    /// Диалог требования авторизации.
    /// Показывается как overlay — некритичная ошибка.
    /// </summary>
    /// <param name="exception">Исключение с типом требуемой авторизации.</param>
    public Task ShowLoginRequiredAsync(LoginRequiredException exception) =>
        ShowInfoAsync(
            L["Dialog_Login_Title"],
            L[exception.GetLocalizationKey()],
            L["Common_OK"]);

    #endregion
}