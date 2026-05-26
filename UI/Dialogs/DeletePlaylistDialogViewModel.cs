using System.Reactive;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

public sealed class DeletePlaylistDialogViewModel : ViewModelBase
{
    public string PlaylistName { get; }

    [Reactive] public bool IsLocalOnly { get; set; } = true;
    [Reactive] public bool IsDeleteEverywhere { get; set; }

    public bool CanDeleteFromCloud { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<DeletePlaylistResult?>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public DeletePlaylistDialogViewModel(Playlist playlist, bool isAuthenticated)
    {
        PlaylistName = playlist.Name;
        CanDeleteFromCloud = playlist.IsFromAccount && isAuthenticated;

        ConfirmCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var result = new DeletePlaylistResult(
                DeleteLocally: true,
                DeleteFromCloud: IsDeleteEverywhere && CanDeleteFromCloud);
            OnResult?.Invoke(result);
        }));

        CancelCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            OnResult?.Invoke(null);
        }));
    }
}

public sealed record DeletePlaylistResult(bool DeleteLocally, bool DeleteFromCloud);