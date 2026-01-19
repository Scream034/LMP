using Avalonia.Controls;
using Avalonia.Input;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Views;

public partial class PlayerBarView : UserControl
{
    public PlayerBarView()
    {
        InitializeComponent();
    }

    private void OnSeekStart(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlayerBarViewModel vm)
        {
            vm.StartSeek();
            
            // Manual implementation of "Move To Point" (Click to Jump)
            if (sender is Slider slider)
            {
                var point = e.GetCurrentPoint(slider);
                if (point.Properties.IsLeftButtonPressed)
                {
                    double width = slider.Bounds.Width;
                    if (width > 0)
                    {
                        var percent = point.Position.X / width;
                        var val = slider.Minimum + (slider.Maximum - slider.Minimum) * percent;
                        // Update visual thumb immediately
                        slider.Value = val;
                    }
                }
            }
        }
    }

    private void OnSeekEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is PlayerBarViewModel vm) vm.EndSeek();
    }
}