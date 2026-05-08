using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LMP.Core.Helpers;
using LMP.Core.Services;
using Material.Icons;
using Material.Icons.Avalonia;

namespace LMP.UI.Controls;

/// <summary>
/// Универсальная кнопка копирования ссылки.
/// Использует единый глобальный toast через <see cref="CopyHintService"/>.
/// Визуально показывает краткое состояние Success/Error через fade-смену иконки.
/// </summary>
public partial class CopyLinkButton : UserControl
{
    private CancellationTokenSource? _stateCts;
    private MaterialIcon? _icon;

    private const int FadeDurationMs = 150;
    private const int StateDurationMs = 1200;

    private static readonly MaterialIconKind IdleKind = MaterialIconKind.LinkVariant;
    private static readonly MaterialIconKind SuccessKind = MaterialIconKind.Check;
    private static readonly MaterialIconKind ErrorKind = MaterialIconKind.Close;

    #region Styled Properties

    /// <summary>Определяет, нужно ли показывать кольцо при наведении (hover-ring).</summary>
    public static readonly StyledProperty<bool> ShowHoverRingProperty =
        AvaloniaProperty.Register<CopyLinkButton, bool>(nameof(ShowHoverRing), true);

    public bool ShowHoverRing
    {
        get => GetValue(ShowHoverRingProperty);
        set => SetValue(ShowHoverRingProperty, value);
    }

    public static readonly StyledProperty<string?> CopyUrlProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(CopyUrl));

    /// <summary>URL для копирования.</summary>
    public string? CopyUrl
    {
        get => GetValue(CopyUrlProperty);
        set => SetValue(CopyUrlProperty, value);
    }

    public static readonly StyledProperty<string?> SuccessTextProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(SuccessText));

    /// <summary>Текст toast при успешном копировании.</summary>
    public string? SuccessText
    {
        get => GetValue(SuccessTextProperty);
        set => SetValue(SuccessTextProperty, value);
    }

    public static readonly StyledProperty<string?> ToolTipTextProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(ToolTipText));

    /// <summary>Текст tooltip.</summary>
    public string? ToolTipText
    {
        get => GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<CopyLinkButton, double>(nameof(IconSize), 16.0);

    /// <summary>Размер иконки.</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly StyledProperty<double> ButtonSizeProperty =
        AvaloniaProperty.Register<CopyLinkButton, double>(nameof(ButtonSize), 36.0);

    /// <summary>Размер круглой кнопки.</summary>
    public double ButtonSize
    {
        get => GetValue(ButtonSizeProperty);
        set => SetValue(ButtonSizeProperty, value);
    }

    #endregion

    #region Direct Properties

    public static readonly DirectProperty<CopyLinkButton, MaterialIconKind> IconKindProperty =
        AvaloniaProperty.RegisterDirect<CopyLinkButton, MaterialIconKind>(
            nameof(IconKind), static o => o.IconKind);

    /// <summary>Текущий вид иконки.</summary>
    public MaterialIconKind IconKind
    {
        get;
        private set => SetAndRaise(IconKindProperty, ref field, value);
    } = IdleKind;

    public static readonly DirectProperty<CopyLinkButton, IBrush?> IconForegroundProperty =
        AvaloniaProperty.RegisterDirect<CopyLinkButton, IBrush?>(
            nameof(IconForeground), static o => o.IconForeground);

    /// <summary>Текущий цвет иконки.</summary>
    public IBrush? IconForeground
    {
        get;
        private set => SetAndRaise(IconForegroundProperty, ref field, value);
    }

    #endregion

    public CopyLinkButton()
    {
        InitializeComponent();
        IconForeground = GetBrush("TextSecondaryBrush") ?? GetBrush("TextMutedBrush");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _icon = this.FindControl<MaterialIcon>("LinkIcon");
    }

    private async void OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CopyUrl))
            return;

        try
        {
            await Clipboard.SetTextAsync(CopyUrl);

            CopyHintService.Instance.Show(
                SuccessText ?? "Copied!",
                CopyHintKind.Success,
                MousePositionHelper.Position);

            await AnimateStateAsync(success: true);
        }
        catch
        {
            CopyHintService.Instance.Show(
                "Copy failed",
                CopyHintKind.Error,
                MousePositionHelper.Position);

            await AnimateStateAsync(success: false);
        }
    }

    /// <summary>
    /// Плавно меняет состояние иконки:
    /// fade-out → смена вида → fade-in → пауза → возврат к исходной скрепке.
    /// </summary>
    private async Task AnimateStateAsync(bool success)
    {
        _stateCts?.Cancel();
        _stateCts?.Dispose();
        var cts = new CancellationTokenSource();
        _stateCts = cts;

        try
        {
            if (_icon != null)
                _icon.Opacity = 0;

            await Task.Delay(FadeDurationMs, cts.Token);

            IconKind = success ? SuccessKind : ErrorKind;
            IconForeground = GetBrush(success ? "AccentBrush" : "SystemErrorRedBrush");

            if (_icon != null)
                _icon.Opacity = 1;

            await Task.Delay(StateDurationMs, cts.Token);

            if (_icon != null)
                _icon.Opacity = 0;

            await Task.Delay(FadeDurationMs, cts.Token);

            IconKind = IdleKind;
            IconForeground = GetBrush("TextSecondaryBrush") ?? GetBrush("TextMutedBrush");

            if (_icon != null)
                _icon.Opacity = 1;
        }
        catch (OperationCanceledException)
        {
            IconKind = IdleKind;
            IconForeground = GetBrush("TextSecondaryBrush") ?? GetBrush("TextMutedBrush");

            if (_icon != null)
                _icon.Opacity = 1;
        }
    }

    private static IBrush? GetBrush(string key)
    {
        var app = Application.Current;
        if (app == null)
            return null;

        return app.Resources.TryGetResource(key, app.ActualThemeVariant, out var resource)
            && resource is IBrush brush
            ? brush
            : null;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _stateCts?.Cancel();
        _stateCts?.Dispose();
        _stateCts = null;
        _icon = null;
        base.OnUnloaded(e);
    }
}