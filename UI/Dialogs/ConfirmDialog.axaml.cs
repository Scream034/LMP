using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LMP.Core.Services;
using ReactiveUI;
using System.Reactive;

namespace LMP.UI.Dialogs;

public partial class ConfirmDialog : Window
{
    private readonly static LocalizationService L = LocalizationService.Instance;

    private readonly IDisposable? _confirmSub;
    private readonly IDisposable? _cancelSub;

    public static readonly StyledProperty<string> DialogTitleProperty =
        AvaloniaProperty.Register<ConfirmDialog, string>(nameof(DialogTitle), L["Dialog_Confirm_Title"]);

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<ConfirmDialog, string>(nameof(Message), L["Dialog_Confirm_Title"]);

    public static readonly StyledProperty<string> ConfirmTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string>(nameof(ConfirmText), L["Common_OK"]);

    public static readonly StyledProperty<string> CancelTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string>(nameof(CancelText), L["Common_Cancel"]);

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

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public ConfirmDialog()
    {
        InitializeComponent();

        ConfirmCommand = ReactiveCommand.Create(() => { });
        CancelCommand = ReactiveCommand.Create(() => { });
        
        _confirmSub = ConfirmCommand.Subscribe(_ =>
        {
            if (IsLoaded) Close(true);
        });
        _cancelSub = CancelCommand.Subscribe(_ =>
        {
            if (IsLoaded) Close(false);
        });

        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _confirmSub?.Dispose();
        _cancelSub?.Dispose();
        base.OnClosed(e);
    }
}

