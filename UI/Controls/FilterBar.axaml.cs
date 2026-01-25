using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyLiteMusicPlayer.UI.Controls;

public partial class FilterBar : UserControl
{
    // Свойство для уникальности групп радиокнопок между разными экранами
    public static readonly StyledProperty<string> GroupNameProperty =
        AvaloniaProperty.Register<FilterBar, string>(nameof(GroupName), "GlobalFilterGroup");

    public string GroupName
    {
        get => GetValue(GroupNameProperty);
        set => SetValue(GroupNameProperty, value);
    }

    public FilterBar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}