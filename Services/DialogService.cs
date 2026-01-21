using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MyLiteMusicPlayer.ViewModels;
using MyLiteMusicPlayer.Views;
using MyLiteMusicPlayer.Views.Dialogs;      // Убедитесь, что этот namespace существует
using YoutubeExplode.Search;                // Нужен для PlaylistSearchResult

namespace MyLiteMusicPlayer.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel");
    Task<string?> SelectFolderAsync(string? startPath = null);
    Task ShowInfoAsync(string title, string message, string buttonText = "OK");

    Task<List<PlaylistSearchResult>> ShowSyncSelectionAsync(IEnumerable<PlaylistSearchResult> items);
    Task<string> ShowMergeConflictDialogAsync(string playlistName);
}

public class DialogService : IDialogService
{
    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel")
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

        var result = await dialog.ShowDialog<bool?>(window);
        return result == true;
    }

    public async Task<string?> SelectFolderAsync(string? startPath = null)
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var storage = window.StorageProvider;
        
        IStorageFolder? suggestedStartLocation = null;
        if (!string.IsNullOrEmpty(startPath) && System.IO.Directory.Exists(startPath))
        {
            suggestedStartLocation = await storage.TryGetFolderFromPathAsync(startPath);
        }

        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Download Folder",
            SuggestedStartLocation = suggestedStartLocation,
            AllowMultiple = false
        });

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public async Task ShowInfoAsync(string title, string message, string buttonText = "OK")
    {
        var window = GetMainWindow();
        if (window == null) return;

        var dialog = new InfoDialog
        {
            DialogTitle = title,
            Message = message,
            ButtonText = buttonText
        };

        await dialog.ShowDialog(window);
    }

    // --- РЕАЛИЗАЦИЯ НОВЫХ МЕТОДОВ ---

    public async Task<List<PlaylistSearchResult>> ShowSyncSelectionAsync(IEnumerable<PlaylistSearchResult> items)
    {
        var window = GetMainWindow();
        if (window == null) return [];

        var vm = new SyncSelectionViewModel(items);
        var dialog = new SyncSelectionDialog
        {
            DataContext = vm
        };

        var result = await dialog.ShowDialog<List<PlaylistSearchResult>>(window);
        return result ?? [];
    }

    public async Task<string> ShowMergeConflictDialogAsync(string playlistName)
    {
        var window = GetMainWindow();
        if (window == null) return "Skip";

        var vm = new MergeConflictViewModel(playlistName);
        var dialog = new MergeConflictDialog
        {
            DataContext = vm
        };

        var result = await dialog.ShowDialog<string>(window);
        return result ?? "Skip";
    }
}