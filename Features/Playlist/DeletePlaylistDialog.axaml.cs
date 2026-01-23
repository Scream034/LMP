using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyLiteMusicPlayer.Features.Playlist;

public partial class DeletePlaylistDialog : Window
{
    private IDisposable? _confirmSub;
    private IDisposable? _cancelSub;

    public DeletePlaylistDialog()
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
        _cancelSub?.Dispose();

        if (DataContext is DeletePlaylistDialogViewModel vm)
        {
            _confirmSub = vm.ConfirmCommand.Subscribe(result =>
            {
                if (IsLoaded) Close(result);
            });
            _cancelSub = vm.CancelCommand.Subscribe(_ =>
            {
                if (IsLoaded) Close(null);
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _confirmSub?.Dispose();
        _cancelSub?.Dispose();
        base.OnClosed(e);
    }
}

