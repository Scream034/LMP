using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LMP.Core.Models;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos;
using LMP.UI.Dialogs;
using LMP.Core.Youtube.Search;

namespace LMP.Core.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);
    Task<string?> SelectFolderAsync(string? startPath = null);
    Task ShowInfoAsync(string title, string message);
    Task ShowInfoAsync(string title, string message, string buttonText);
    Task<string?> ShowInputAsync(string title, string prompt, string? watermark = null);
    Task<DeletePlaylistResult?> ShowDeletePlaylistDialogAsync(Playlist playlist, bool isAuthenticated);

    /// <summary>
    /// Объединённый диалог синхронизации: выбор плейлистов + разрешение конфликтов.
    /// Возвращает список решений (Skip-элементы исключены).
    /// </summary>
    Task<List<SyncDecision>> ShowSyncSelectionAsync(
        IEnumerable<PlaylistSearchResult> items,
        ISet<string> existingLocalNames);

    /// <summary>
    /// Диалог «Добавить трек в плейлист» — список плейлистов с CheckBox'ами.
    /// Возвращает список ID плейлистов, в которые нужно добавить трек. Пустой = отмена.
    /// </summary>
    Task<List<string>> ShowAddToPlaylistDialogAsync(TrackInfo track);

    /// <summary>
    /// Диалог редактирования плейлиста: название, обложка, цвет, синхронизация.
    /// Возвращает null при отмене.
    /// </summary>
    Task<EditPlaylistResult?> ShowEditPlaylistDialogAsync(Playlist playlist);

    /// <summary>
    /// Показывает диалог ожидания bot detection cooldown.
    /// </summary>
    Task ShowBotDetectionCooldownAsync(TimeSpan waitTime);

    /// <summary>
    /// Показывает диалог ошибки недоступности стрима.
    /// </summary>
    Task ShowStreamUnavailableAsync(StreamUnavailableException exception);

    /// <summary>
    /// Показывает диалог общей ошибки воспроизведения.
    /// </summary>
    Task ShowPlaybackErrorAsync(string videoId, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Показывает диалог требования авторизации.
    /// </summary>
    Task ShowLoginRequiredAsync(LoginRequiredException exception);

    Task<CreatePlaylistResult?> ShowCreatePlaylistDialogAsync();
}

public sealed class DialogService : IDialogService
{
    private static readonly LocalizationService L = LocalizationService.Instance;

    private readonly CookieAuthService? _authService;

    // Синглтоны для диалогов, которые не должны дублироваться
    private BotDetectionDialog? _activeBotDetectionDialog;
    private readonly Lock _botDetectionLock = new();

    public DialogService(CookieAuthService? authService)
    {
        _authService = authService;
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window is { IsLoaded: true, IsVisible: true })
            {
                return window;
            }
        }
        return null;
    }

    private static async Task<T?> ShowDialogSafeAsync<T>(Window dialog, Window owner)
    {
        try
        {
            await Task.Yield();
            return await dialog.ShowDialog<T?>(owner);
        }
        catch (Exception ex)
        {
            Log.Error($"[Dialog] Error showing dialog: {ex.Message}");
            return default;
        }
    }

    #region Basic Dialogs

    public async Task<string?> ShowInputAsync(string title, string prompt, string? watermark = null)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return null;

            var dialog = new InputDialog
            {
                DialogTitle = title,
                PromptMessage = prompt
            };

            if (!string.IsNullOrEmpty(watermark))
                dialog.Watermark = watermark;

            return await ShowDialogSafeAsync<string>(dialog, window);
        });
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return false;

            var dialog = new ConfirmDialog
            {
                DialogTitle = title,
                Message = message,
                ConfirmText = confirmText,
                CancelText = cancelText
            };

            var result = await ShowDialogSafeAsync<bool?>(dialog, window);
            return result == true;
        });
    }

    public async Task<bool> ConfirmAsync(string title, string message) =>
        await ConfirmAsync(title, message, L["Common_OK"], L["Common_Cancel"]);

    public async Task<string?> SelectFolderAsync(string? startPath = null)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return null;
            var storage = window.StorageProvider;
            IStorageFolder? suggestedStartLocation = null;
            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                suggestedStartLocation = await storage.TryGetFolderFromPathAsync(startPath);

            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L["Dialog_SelectDownloadFolder_Title"],
                SuggestedStartLocation = suggestedStartLocation,
                AllowMultiple = false
            });
            return result.Count > 0 ? result[0].Path.LocalPath : null;
        });
    }

    public async Task ShowInfoAsync(string title, string message, string buttonText)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;
            var dialog = new InfoDialog { DialogTitle = title, Message = message, ButtonText = buttonText };
            await ShowDialogSafeAsync<object>(dialog, window);
        });
    }

    public async Task ShowInfoAsync(string title, string message) =>
        await ShowInfoAsync(title, message, L["Common_OK"]);

    #endregion

    #region Sync Dialogs

    public async Task<List<SyncDecision>> ShowSyncSelectionAsync(
        IEnumerable<PlaylistSearchResult> items,
        ISet<string> existingLocalNames)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return new List<SyncDecision>();

            var vm = new SyncSelectionViewModel(items, existingLocalNames);
            var dialog = new SyncSelectionDialog { DataContext = vm };
            var result = await ShowDialogSafeAsync<List<SyncDecision>>(dialog, window);
            return result ?? new List<SyncDecision>();
        });
    }

    public async Task<DeletePlaylistResult?> ShowDeletePlaylistDialogAsync(Playlist playlist, bool isAuthenticated)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return null;
            var vm = new DeletePlaylistDialogViewModel(playlist, isAuthenticated);
            var dialog = new DeletePlaylistDialog { DataContext = vm };
            return await ShowDialogSafeAsync<DeletePlaylistResult?>(dialog, window);
        });
    }

    #endregion

    #region Bot Detection / Stream Errors

    public async Task ShowBotDetectionCooldownAsync(TimeSpan waitTime)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            lock (_botDetectionLock)
            {
                // Если диалог уже открыт — обновляем его, не создаём новый
                if (_activeBotDetectionDialog != null && _activeBotDetectionDialog.IsVisible)
                {
                    // Обновляем таймер существующего диалога
                    _activeBotDetectionDialog.UpdateCountdown(
                        VideoController.GetRemainingCooldown(),
                        VideoController.CooldownDuration);
                    return;
                }

                // Создаём новый диалог
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
                await ShowDialogSafeAsync<bool?>(_activeBotDetectionDialog, window);
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

    public async Task ShowStreamUnavailableAsync(StreamUnavailableException exception)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            var dialog = new StreamUnavailableDialog();
            dialog.ConfigureForException(exception);

            await ShowDialogSafeAsync<object>(dialog, window);
        });
    }

    public async Task ShowPlaybackErrorAsync(string videoId, string errorMessage, Exception? exception = null)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            var dialog = new StreamUnavailableDialog();
            dialog.ConfigureForError(videoId, errorMessage, exception);

            await ShowDialogSafeAsync<object>(dialog, window);
        });
    }

    public async Task ShowLoginRequiredAsync(LoginRequiredException exception)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            var L = LocalizationService.Instance;
            var dialog = new InfoDialog
            {
                DialogTitle = L["Dialog_Login_Title"],
                Message = L[exception.GetLocalizationKey()],
                ButtonText = L["Common_OK"]
            };

            await ShowDialogSafeAsync<object>(dialog, window);
        });
    }

    #endregion

    #region Playlists

    public async Task<CreatePlaylistResult?> ShowCreatePlaylistDialogAsync()
    {
        var owner = GetMainWindow();
        if (owner == null) return null;

        var isAuthenticated = _authService?.IsAuthenticated == true;
        var vm = new CreatePlaylistDialogViewModel(isAuthenticated);
        var dialog = new CreatePlaylistDialog(vm);

        // ShowDialog<T> блокирует до Close(result)
        return await dialog.ShowDialog<CreatePlaylistResult?>(owner);
    }

    public async Task<List<string>> ShowAddToPlaylistDialogAsync(TrackInfo track)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return new List<string>();

            var library = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<LibraryService>(Program.Services);
            var playlists = await library.GetAllPlaylistsAsync();

            var vm = new AddToPlaylistDialogViewModel(track, playlists);
            var dialog = new AddToPlaylistDialog { DataContext = vm };
            var result = await ShowDialogSafeAsync<List<string>>(dialog, window);
            return result ?? new List<string>();
        });
    }

    public async Task<EditPlaylistResult?> ShowEditPlaylistDialogAsync(Playlist playlist)
    {
        var owner = GetMainWindow();
        if (owner == null) return null;

        var isAuthenticated = _authService?.IsAuthenticated == true;
        var vm = new EditPlaylistDialogViewModel(playlist, isAuthenticated);
        var dialog = new EditPlaylistDialog(vm);

        // ShowDialog<T> блокирует до Close(result)
        return await dialog.ShowDialog<EditPlaylistResult?>(owner);
    }

    #endregion
}