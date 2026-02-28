using System.Reactive;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

public record CreatePlaylistResult(string Name, bool SyncToCloud);

public class CreatePlaylistDialogViewModel : ViewModelBase
{
    [Reactive] public string PlaylistName { get; set; } = string.Empty;
    [Reactive] public bool SyncToCloud { get; set; }

    public ReactiveCommand<Unit, CreatePlaylistResult?> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public CreatePlaylistDialogViewModel()
    {
        var canCreate = this.WhenAnyValue(
            x => x.PlaylistName, 
            name => !string.IsNullOrWhiteSpace(name));
        
        ConfirmCommand = CreateCommand(ReactiveCommand.Create<Unit, CreatePlaylistResult?>(
            _ => new CreatePlaylistResult(PlaylistName.Trim(), SyncToCloud),
            canCreate));

        CancelCommand = CreateCommand(ReactiveCommand.Create(() => { }));
    }
}