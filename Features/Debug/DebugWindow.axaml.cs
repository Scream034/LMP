using Avalonia.Controls;

namespace LMP.Features.Debug;

public partial class DebugWindow : Window
{
    public DebugWindow()
    {
        InitializeComponent();
        DataContext = new DebugViewModel();
    }
}