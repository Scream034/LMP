using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LMP.Core.Models;
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

    Task<List<PlaylistSearchResult>> ShowSyncSelectionAsync(IEnumerable<PlaylistSearchResult> items);
    Task<List<MergeDecision>> ShowMergeConflictResolutionDialogAsync(List<string> playlistNames);
    Task<DeletePlaylistResult?> ShowDeletePlaylistDialogAsync(Playlist playlist, bool isAuthenticated);
}

public class DialogService : IDialogService
{
    private static readonly LocalizationService L = LocalizationService.Instance;

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

    // --- REALIZATION OF NEW METHOD ---
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
            if (!string.IsNullOrEmpty(startPath) && System.IO.Directory.Exists(startPath))
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

    public async Task<List<PlaylistSearchResult>> ShowSyncSelectionAsync(IEnumerable<PlaylistSearchResult> items)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return [];
            var vm = new SyncSelectionViewModel(items);
            var dialog = new SyncSelectionDialog { DataContext = vm };
            var result = await ShowDialogSafeAsync<List<PlaylistSearchResult>>(dialog, window);
            return result ?? [];
        });
    }

    public async Task<List<MergeDecision>> ShowMergeConflictResolutionDialogAsync(List<string> playlistNames)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return [.. playlistNames.Select(n => new MergeDecision(n, MergeAction.Skip))];
            var vm = new MergeConflictResolutionViewModel(playlistNames);
            var dialog = new MergeConflictResolutionDialog { DataContext = vm };
            var result = await ShowDialogSafeAsync<List<MergeDecision>>(dialog, window);
            return result ?? [.. playlistNames.Select(n => new MergeDecision(n, MergeAction.Skip))];
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
}