using System.Reactive;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;

namespace LMP.UI.Dialogs;

public sealed class EditPlaylistDialogViewModel : ViewModelBase
{
    public PlaylistEditorViewModel Editor { get; }

    public ReactiveCommand<Unit, EditPlaylistResult?> SaveCommand { get; }
    public ReactiveCommand<Unit, EditPlaylistResult?> CancelCommand { get; }

    public EditPlaylistDialogViewModel(Playlist playlist, bool isAuthenticated)
    {
        Editor = PlaylistEditorViewModel.ForEdit(playlist, isAuthenticated);

        SaveCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var result = Editor.ToResult();

            // Записываем изменение синхронизации только если пользователь реально переключил toggle
            if (Editor.SyncStateChanged)
                result.SyncToCloud = Editor.IsSyncedToCloud;

            return (EditPlaylistResult?)result;
        }, Editor.CanSave));

        CancelCommand = CreateCommand(ReactiveCommand.Create(
            () => (EditPlaylistResult?)null));
    }
}