using System.Reactive;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

public sealed class CreatePlaylistDialogViewModel : ViewModelBase
{
    public PlaylistEditorViewModel Editor { get; }

    /// <summary>
    /// Синхронизировать с YouTube Music.
    /// </summary>
    [Reactive] public bool SyncToCloud { get; set; }

    /// <summary>
    /// Показывать ли переключатель синхронизации.
    /// </summary>
    public bool ShowSyncToggle { get; }

    public ReactiveCommand<Unit, CreatePlaylistResult?> ConfirmCommand { get; }
    public ReactiveCommand<Unit, CreatePlaylistResult?> CancelCommand { get; }

    public CreatePlaylistDialogViewModel(bool isAuthenticated = false)
    {
        Editor = PlaylistEditorViewModel.ForCreate();
        ShowSyncToggle = isAuthenticated;
        SyncToCloud = isAuthenticated; // По умолчанию вкл. если аутентифицирован

        ConfirmCommand = CreateCommand(ReactiveCommand.Create(
            () => (CreatePlaylistResult?)new CreatePlaylistResult(
                Name: Editor.ToResult().Name,
                SyncToCloud: SyncToCloud),
            Editor.CanSave));

        CancelCommand = CreateCommand(ReactiveCommand.Create(
            () => (CreatePlaylistResult?)null));
    }
}

public sealed record CreatePlaylistResult(in string Name, in bool SyncToCloud);