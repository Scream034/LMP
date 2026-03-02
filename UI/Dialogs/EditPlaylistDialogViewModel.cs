using System.Reactive;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using ReactiveUI;

namespace LMP.UI.Dialogs;

public sealed class EditPlaylistDialogViewModel : ViewModelBase
{
    public PlaylistEditorViewModel Editor { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<EditPlaylistResult?>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public EditPlaylistDialogViewModel(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null)
    {
        Editor = PlaylistEditorViewModel.ForEdit(playlist, isAuthenticated, playlistTracks);

        SaveCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var result = Editor.ToResult();

            if (Editor.SyncStateChanged)
                result.SyncToCloud = Editor.IsSyncedToCloud;

            OnResult?.Invoke(result);
        }, Editor.CanSave));

        CancelCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            OnResult?.Invoke(null);
        }));
    }
}