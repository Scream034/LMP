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
        if (DataContext is PlayerBarViewModel vm) vm.StartSeek();
    }

    private void OnSeekEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is PlayerBarViewModel vm) vm.EndSeek();
    }
}