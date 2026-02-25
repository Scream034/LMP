using Avalonia;
using Avalonia.Controls;

namespace LMP.UI.Controls;

public partial class AppIcon : UserControl
{
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<AppIcon, double>(nameof(IconSize), 24);

    public static readonly StyledProperty<bool> ShowGlowProperty =
        AvaloniaProperty.Register<AppIcon, bool>(nameof(ShowGlow), false);

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool ShowGlow
    {
        get => GetValue(ShowGlowProperty);
        set => SetValue(ShowGlowProperty, value);
    }

    public AppIcon()
    {
        InitializeComponent();
        
        // Обновляем размеры при изменении
        this.GetObservable(IconSizeProperty).Subscribe(_ => UpdateLayoutIcon());
        this.GetObservable(ShowGlowProperty).Subscribe(_ => UpdateLayoutIcon());
        this.GetObservable(BoundsProperty).Subscribe(_ => UpdateLayoutIcon());
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateLayoutIcon();
    }

    private void UpdateLayoutIcon()
    {
        var size = IconSize > 0 ? IconSize : Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) size = 24;

        // Root Grid size
        RootGrid.Width = size;
        RootGrid.Height = size;
        // 430A-DCC8-41A0-4631
        // Glow
        GlowBorder.IsVisible = ShowGlow;
        GlowBorder.CornerRadius = new CornerRadius(size * 0.17);

        // Play triangle
        PlayPath.Width = size * 0.5;
        PlayPath.Height = size * 0.5;
        PlayPath.Margin = new Thickness(size * 0.17, 0, 0, 0);

        // Chevrons
        var chevronWidth = size * 0.2;
        var chevronHeight = size * 0.3;
        var chevronThickness = Math.Max(1.5, size * 0.1);
        
        ChevronsPanel.Spacing = -size * 0.08;
        ChevronsPanel.Margin = new Thickness(0, 0, size * 0.04, size * 0.12);

        Chevron1.Width = chevronWidth;
        Chevron1.Height = chevronHeight;
        Chevron1.StrokeThickness = chevronThickness;

        Chevron2.Width = chevronWidth;
        Chevron2.Height = chevronHeight;
        Chevron2.StrokeThickness = chevronThickness;
    }
}