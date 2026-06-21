using System.Reactive;
using ReactiveUI;


namespace LMP.UI.Dialogs;

public sealed partial class CreatePlaylistDialogViewModel : ViewModelBase
{
    public PlaylistEditorViewModel Editor { get; }

    [Reactive] public partial bool SyncToCloud { get; set; }
    public bool ShowSyncToggle { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<CreatePlaylistResult?>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public CreatePlaylistDialogViewModel(bool isAuthenticated = false)
    {
        Editor = PlaylistEditorViewModel.ForCreate();
        ShowSyncToggle = isAuthenticated;
        SyncToCloud = isAuthenticated;

        ConfirmCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var result = new CreatePlaylistResult(
                Name: Editor.ToResult().Name,
                SyncToCloud: SyncToCloud);
            OnResult?.Invoke(result);
        }, Editor.CanSave));

        CancelCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            OnResult?.Invoke(null);
        }));
    }
}

public sealed record CreatePlaylistResult(string Name, bool SyncToCloud);
