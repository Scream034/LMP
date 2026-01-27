using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LMP.Core.Services;

namespace LMP.UI.Dialogs;

public partial class InputDialog : Window
{
    private static readonly LocalizationService L = LocalizationService.Instance;

    public static readonly StyledProperty<string> DialogTitleProperty =
        AvaloniaProperty.Register<InputDialog, string>(nameof(DialogTitle), "Input");

    public static readonly StyledProperty<string> PromptMessageProperty =
        AvaloniaProperty.Register<InputDialog, string>(nameof(PromptMessage), "");

    public static readonly StyledProperty<string> InputValueProperty =
        AvaloniaProperty.Register<InputDialog, string>(nameof(InputValue), "");

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<InputDialog, string>(nameof(Watermark), "");

    public static readonly StyledProperty<string> ConfirmTextProperty =
        AvaloniaProperty.Register<InputDialog, string>(nameof(ConfirmText), "OK");

    public static readonly StyledProperty<string> CancelTextProperty =
        AvaloniaProperty.Register<InputDialog, string>(nameof(CancelText), "Cancel");

    public string DialogTitle
    {
        get => GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public string PromptMessage
    {
        get => GetValue(PromptMessageProperty);
        set => SetValue(PromptMessageProperty, value);
    }

    public string InputValue
    {
        get => GetValue(InputValueProperty);
        set => SetValue(InputValueProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public string ConfirmText
    {
        get => GetValue(ConfirmTextProperty);
        set => SetValue(ConfirmTextProperty, value);
    }

    public string CancelText
    {
        get => GetValue(CancelTextProperty);
        set => SetValue(CancelTextProperty, value);
    }

    public InputDialog()
    {
        InitializeComponent();
        DataContext = this;

        // Локализация
        ConfirmText = L["Common_OK"];
        CancelText = L["Common_Cancel"];
        Watermark = L["Input_Watermark"];
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Close(InputValue);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}