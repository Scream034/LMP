using Avalonia;
using Avalonia.Controls;

namespace LMP.UI.Controls;

/// <summary>
/// Panel, которая распределяет дочерние элементы равномерно по строкам.
/// MaxColumns ограничивает максимальное кол-во элементов в строке.
/// MinColumns — минимальное (при уменьшении окна).
/// Элементы растягиваются на всю доступную ширину.
/// </summary>
public class AdaptiveGridPanel : Panel
{
    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, int>(nameof(MaxColumns), 6);

    public static readonly StyledProperty<int> MinColumnsProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, int>(nameof(MinColumns), 3);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(Spacing), 12);

    /// <summary>Максимум элементов в строке</summary>
    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Минимум элементов в строке</summary>
    public int MinColumns
    {
        get => GetValue(MinColumnsProperty);
        set => SetValue(MinColumnsProperty, value);
    }

    /// <summary>Отступ между элементами</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = CalculateColumns(availableSize.Width);
        double itemWidth = CalculateItemWidth(availableSize.Width, columns);
        double totalHeight = 0;
        double rowHeight = 0;
        int col = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            col++;

            if (col >= columns)
            {
                totalHeight += rowHeight + Spacing;
                rowHeight = 0;
                col = 0;
            }
        }

        // Последняя неполная строка
        if (col > 0)
            totalHeight += rowHeight;
        else if (totalHeight > 0)
            totalHeight -= Spacing; // Убираем лишний spacing после последней полной строки

        return new Size(availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = CalculateColumns(finalSize.Width);
        double itemWidth = CalculateItemWidth(finalSize.Width, columns);
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        int col = 0;

        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, y, itemWidth, child.DesiredSize.Height));

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            col++;
            x += itemWidth + Spacing;

            if (col >= columns)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
                col = 0;
            }
        }

        return finalSize;
    }

    private int CalculateColumns(double width)
    {
        if (width <= 0) return MinColumns;

        // Подбираем кол-во колонок от Max к Min
        for (int cols = MaxColumns; cols >= MinColumns; cols--)
        {
            double itemWidth = CalculateItemWidth(width, cols);
            if (itemWidth >= 120) // Минимальная разумная ширина карточки
                return cols;
        }

        return MinColumns;
    }

    private double CalculateItemWidth(double totalWidth, int columns)
    {
        if (columns <= 0) return totalWidth;
        double totalSpacing = Spacing * (columns - 1);
        return (totalWidth - totalSpacing) / columns;
    }
}