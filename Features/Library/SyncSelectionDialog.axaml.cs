using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyLiteMusicPlayer.Features.Library;

public partial class SyncSelectionDialog : Window
{
    private IDisposable? _syncSub;
    private IDisposable? _cancelSub;

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
        
        // Отписываемся от старых подписок
        _syncSub?.Dispose();
        _cancelSub?.Dispose();
        
        if (DataContext is SyncSelectionViewModel vm)
        {
            _syncSub = vm.SyncCommand.Subscribe(result => 
            {
                if (IsLoaded) Close(result);
            });
            _cancelSub = vm.CancelCommand.Subscribe(result => 
            {
                if (IsLoaded) Close(result);
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _syncSub?.Dispose();
        _cancelSub?.Dispose();
        base.OnClosed(e);
    }
}

