using System.Reactive;
using System.Reactive.Disposables;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;

namespace LMP.Features.Library;

public enum MergeAction { Skip, Merge, Duplicate }

public class MergeDecision(string playlistName, MergeAction action)
{
    public string PlaylistName { get; } = playlistName;
    public MergeAction Action { get; set; } = action;
}

public sealed class MergeConflictItemViewModel : ViewModelBase
{
    public string PlaylistName { get; }

    [Reactive] public bool IsSkip { get; set; } = true;
    [Reactive] public bool IsMerge { get; set; }
    [Reactive] public bool IsDuplicate { get; set; }

    public MergeConflictItemViewModel(string name)
    {
        PlaylistName = name;
        
        // Use DisposeWith to properly cleanup subscriptions
        this.WhenAnyValue(x => x.IsSkip)
            .Subscribe(v => { if (v) { IsMerge = false; IsDuplicate = false; } })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsMerge)
            .Subscribe(v => { if (v) { IsSkip = false; IsDuplicate = false; } })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsDuplicate)
            .Subscribe(v => { if (v) { IsSkip = false; IsMerge = false; } })
            .DisposeWith(Disposables);
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

public sealed class MergeConflictResolutionViewModel : ViewModelBase
{
    private bool _isDisposed;
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

        // Use CreateCommand wrapper
        SetAllMergeCommand = CreateCommand(ReactiveCommand.Create(() => SetAll(MergeAction.Merge)));
        SetAllDuplicateCommand = CreateCommand(ReactiveCommand.Create(() => SetAll(MergeAction.Duplicate)));
        SetAllSkipCommand = CreateCommand(ReactiveCommand.Create(() => SetAll(MergeAction.Skip)));

        ConfirmCommand = CreateCommand(ReactiveCommand.Create(() =>
            Conflicts.Select(c => new MergeDecision(c.PlaylistName, c.GetDecision())).ToList()));
    }

    private void SetAll(MergeAction action)
    {
        foreach (var item in Conflicts) item.SetDecision(action);
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            foreach (var item in Conflicts)
            {
                item.Dispose();
            }
            Conflicts.Clear();
        }
        base.Dispose(disposing);
        _isDisposed = true;
    }
}