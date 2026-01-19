// Views/Dialogs/InfoDialog.axaml.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using System.Reactive;

namespace MyLiteMusicPlayer.Views.Dialogs;

public partial class InfoDialog : Window
{
    public static readonly StyledProperty<string> DialogTitleProperty =
        AvaloniaProperty.Register<InfoDialog, string>(nameof(DialogTitle), "Info");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<InfoDialog, string>(nameof(Message), "");

    public static readonly StyledProperty<string> ButtonTextProperty =
        AvaloniaProperty.Register<InfoDialog, string>(nameof(ButtonText), "OK");

    public string DialogTitle
    {
        get => GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string ButtonText
    {
        get => GetValue(ButtonTextProperty);
        set => SetValue(ButtonTextProperty, value);
    }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public InfoDialog()
    {
        InitializeComponent();
        CloseCommand = ReactiveCommand.Create(() => Close());
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}