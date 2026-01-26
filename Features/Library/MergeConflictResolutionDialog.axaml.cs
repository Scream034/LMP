using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LMP.Features.Library;

public partial class MergeConflictResolutionDialog : Window
{
    private IDisposable? _confirmSub;

    public MergeConflictResolutionDialog()
    {
        InitializeComponent();
    }
    
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _confirmSub?.Dispose();
        
        if (DataContext is MergeConflictResolutionViewModel vm)
        {
            _confirmSub = vm.ConfirmCommand.Subscribe(result =>
            {
                if (IsLoaded) Close(result);
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _confirmSub?.Dispose();
        base.OnClosed(e);
    }
}

