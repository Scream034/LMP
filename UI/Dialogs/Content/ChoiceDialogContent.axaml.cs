using Avalonia.Controls;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Контент overlay-диалога с произвольным набором кнопок и опциональным чекбоксом.
/// DataContext устанавливается через DataTemplate в DialogHost.
/// </summary>
public partial class ChoiceDialogContent : UserControl
{
    public ChoiceDialogContent()
    {
        InitializeComponent();
    }
}