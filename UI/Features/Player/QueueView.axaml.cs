using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LMP.UI.Features.Player;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}