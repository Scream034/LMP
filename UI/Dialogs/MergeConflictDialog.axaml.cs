using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LMP.UI.Dialogs;

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
            vm.MergeCommand.Subscribe(Close);
            vm.DuplicateCommand.Subscribe(Close);
            vm.SkipCommand.Subscribe(Close);
        }
    }
}

