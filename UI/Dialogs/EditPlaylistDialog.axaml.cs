using Avalonia.Controls;
using Avalonia.ReactiveUI;
using LMP.Core.Models;
using ReactiveUI;
using System.Reactive.Disposables;

namespace LMP.UI.Dialogs;

public partial class EditPlaylistDialog : Window
{
    public EditPlaylistDialog()
    {
        InitializeComponent();
    }

    public EditPlaylistDialog(EditPlaylistDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;

        // Подписываемся на команды — закрываем окно с результатом
        viewModel.SaveCommand
            .Subscribe(Close);

        viewModel.CancelCommand
            .Subscribe(_ => Close(null));
    }
}