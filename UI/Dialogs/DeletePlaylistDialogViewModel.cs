using LMP.Core.Models;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;

namespace LMP.UI.Dialogs;

/// <summary>
/// Результат диалога удаления
/// </summary>
public record DeletePlaylistResult(bool DeleteFromCloud);

public class DeletePlaylistDialogViewModel : ViewModelBase
{
    public string PlaylistName { get; }
    public bool CanDeleteFromCloud { get; }

    [Reactive] public bool IsLocalOnly { get; set; } = true;
    [Reactive] public bool IsDeleteEverywhere { get; set; }

    public ReactiveCommand<Unit, DeletePlaylistResult> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public DeletePlaylistDialogViewModel(Core.Models.Playlist playlist, bool isAuthenticated)
    {
        PlaylistName = playlist.Name;

        // Можно удалить из облака только если:
        // 1. Пользователь авторизован
        // 2. Плейлист синхронизирован с YouTube
        CanDeleteFromCloud = isAuthenticated &&
                             playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
                             !string.IsNullOrEmpty(playlist.YoutubeId);

        ConfirmCommand = ReactiveCommand.Create(() =>
            new DeletePlaylistResult(DeleteFromCloud: IsDeleteEverywhere));

        CancelCommand = ReactiveCommand.Create(() => { });
    }
}


