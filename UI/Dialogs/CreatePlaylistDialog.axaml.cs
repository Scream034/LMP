using Avalonia.Controls;

namespace LMP.UI.Dialogs;

public partial class CreatePlaylistDialog : Window
{
    public CreatePlaylistDialog()
    {
        InitializeComponent();
    }

    public CreatePlaylistDialog(CreatePlaylistDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;

        viewModel.ConfirmCommand
            .Subscribe(Close);

        viewModel.CancelCommand
            .Subscribe(_ => Close(null));
    }
}