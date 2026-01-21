using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;

namespace MyLiteMusicPlayer.ViewModels;

public enum MergeAction { Skip, Merge, Duplicate }

public class MergeDecision(string playlistName, MergeAction action)
{
    public string PlaylistName { get; } = playlistName;
    public MergeAction Action { get; set; } = action;
}

public class MergeConflictItemViewModel : ViewModelBase
{
    public string PlaylistName { get; }

    [Reactive] public bool IsSkip { get; set; } = true;
    [Reactive] public bool IsMerge { get; set; }
    [Reactive] public bool IsDuplicate { get; set; }

    public MergeConflictItemViewModel(string name)
    {
        PlaylistName = name;
        // Подписка для обеспечения radio-button поведения
        this.WhenAnyValue(x => x.IsSkip).Subscribe(v => { if (v) { IsMerge = false; IsDuplicate = false; } });
        this.WhenAnyValue(x => x.IsMerge).Subscribe(v => { if (v) { IsSkip = false; IsDuplicate = false; } });
        this.WhenAnyValue(x => x.IsDuplicate).Subscribe(v => { if (v) { IsSkip = false; IsMerge = false; } });
    }

    public MergeAction GetDecision()
    {
        if (IsMerge) return MergeAction.Merge;
        if (IsDuplicate) return MergeAction.Duplicate;
        return MergeAction.Skip;
    }

    public void SetDecision(MergeAction action)
    {
        IsSkip = action == MergeAction.Skip;
        IsMerge = action == MergeAction.Merge;
        IsDuplicate = action == MergeAction.Duplicate;
    }
}

public class MergeConflictResolutionViewModel : ViewModelBase
{
    public ObservableCollection<MergeConflictItemViewModel> Conflicts { get; } = [];

    public ReactiveCommand<Unit, Unit> SetAllMergeCommand { get; }
    public ReactiveCommand<Unit, Unit> SetAllDuplicateCommand { get; }
    public ReactiveCommand<Unit, Unit> SetAllSkipCommand { get; }

    public ReactiveCommand<Unit, List<MergeDecision>> ConfirmCommand { get; }

    public MergeConflictResolutionViewModel(List<string> playlistNames)
    {
        foreach (var name in playlistNames)
        {
            Conflicts.Add(new MergeConflictItemViewModel(name));
        }

        SetAllMergeCommand = ReactiveCommand.Create(() => SetAll(MergeAction.Merge));
        SetAllDuplicateCommand = ReactiveCommand.Create(() => SetAll(MergeAction.Duplicate));
        SetAllSkipCommand = ReactiveCommand.Create(() => SetAll(MergeAction.Skip));

        ConfirmCommand = ReactiveCommand.Create(() =>
            Conflicts.Select(c => new MergeDecision(c.PlaylistName, c.GetDecision())).ToList());
    }

    private void SetAll(MergeAction action)
    {
        foreach (var item in Conflicts) item.SetDecision(action);
    }
}