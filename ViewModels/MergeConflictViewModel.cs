using MyLiteMusicPlayer.Services;
using ReactiveUI;
using System.Reactive;

namespace MyLiteMusicPlayer.ViewModels;

// Теперь текст сообщения формируется через локализацию
public class MergeConflictViewModel(string playlistName) : ViewModelBase
{
    public string Message { get; } = string.Format(LocalizationService.Instance["Conflict_Message"], playlistName);

    public ReactiveCommand<Unit, string> MergeCommand { get; } = ReactiveCommand.Create(() => L["Dialog_Merge_Merge"]);
    public ReactiveCommand<Unit, string> DuplicateCommand { get; } = ReactiveCommand.Create(() => L["Dialog_Merge_Duplicate"]);
    public ReactiveCommand<Unit, string> SkipCommand { get; } = ReactiveCommand.Create(() => L["Dialog_Merge_Skip"]);
}