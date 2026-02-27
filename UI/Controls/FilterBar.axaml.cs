using Avalonia;
using Avalonia.Controls;
using Material.Icons;

namespace LMP.UI.Controls;

public partial class FilterBar : UserControl
{
    // ═══ FILTER TEXT ═══
    public static readonly StyledProperty<string?> FilterTextProperty =
        AvaloniaProperty.Register<FilterBar, string?>(
            nameof(FilterText),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public string? FilterText
    {
        get => GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    // ═══ WATERMARK ═══
    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<FilterBar, string?>(nameof(Watermark), "Filter...");

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    // ═══ COMPACT MODE ═══
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<FilterBar, bool>(nameof(Compact), false);

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    // ═══ ICON KIND ═══
    public static readonly StyledProperty<MaterialIconKind> IconKindProperty =
        AvaloniaProperty.Register<FilterBar, MaterialIconKind>(
            nameof(IconKind), MaterialIconKind.Magnify);

    public MaterialIconKind IconKind
    {
        get => GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    // ═══ COMPUTED (Direct Properties для уведомления UI) ═══

    public static readonly DirectProperty<FilterBar, double> BarHeightProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, double>(
            nameof(BarHeight), o => o.BarHeight);

    public double BarHeight => Compact ? 32 : 36;

    public static readonly DirectProperty<FilterBar, CornerRadius> BarCornerRadiusProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, CornerRadius>(
            nameof(BarCornerRadius), o => o.BarCornerRadius);

    public CornerRadius BarCornerRadius => Compact ? new CornerRadius(6) : new CornerRadius(8);

    public static readonly DirectProperty<FilterBar, double> TextFontSizeProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, double>(
            nameof(TextFontSize), o => o.TextFontSize);

    public double TextFontSize => Compact ? 12 : 13;

    public static readonly DirectProperty<FilterBar, double> IconSizeProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, double>(
            nameof(IconSize), o => o.IconSize);

    public double IconSize => Compact ? 14 : 16;

    public FilterBar()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CompactProperty)
        {
            RaisePropertyChanged(BarHeightProperty, default, BarHeight);
            RaisePropertyChanged(BarCornerRadiusProperty, default, BarCornerRadius);
            RaisePropertyChanged(TextFontSizeProperty, default, TextFontSize);
            RaisePropertyChanged(IconSizeProperty, default, IconSize);
        }
    }
}