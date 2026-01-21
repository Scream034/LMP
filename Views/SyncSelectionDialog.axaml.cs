using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Views;

public partial class SyncSelectionDialog : Window
{
    public SyncSelectionDialog()
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
        
        if (DataContext is SyncSelectionViewModel vm)
        {
            // Закрываем окно и возвращаем список (или пустой список при отмене)
            vm.SyncCommand.Subscribe(result => Close(result));
            vm.CancelCommand.Subscribe(result => Close(result));
        }
    }
}