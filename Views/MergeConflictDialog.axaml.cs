using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Views;

public partial class MergeConflictDialog : Window
{
    public MergeConflictDialog()
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
        
        if (DataContext is MergeConflictViewModel vm)
        {
            // Подписываемся на команды ViewModel, чтобы закрыть окно и вернуть результат
            vm.MergeCommand.Subscribe(result => Close(result));
            vm.DuplicateCommand.Subscribe(result => Close(result));
            vm.SkipCommand.Subscribe(result => Close(result));
        }
    }
}