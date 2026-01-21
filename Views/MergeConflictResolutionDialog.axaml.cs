using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Views;

public partial class MergeConflictResolutionDialog : Window
{
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
        if (DataContext is MergeConflictResolutionViewModel vm)
        {
            vm.ConfirmCommand.Subscribe(result => Close(result));
        }
    }
}