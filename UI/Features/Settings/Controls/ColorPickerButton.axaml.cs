using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace LMP.UI.Features.Settings.Controls;

/// <summary>
/// Кастомная кнопка выбора цвета с popup-диалогом.
/// Заменяет нестабильный стандартный ColorPicker Avalonia.
/// </summary>
public partial class ColorPickerButton : UserControl
{
    #region Styled Properties

    /// <summary>
    /// Выбранный цвет
    /// </summary>
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPickerButton, Color>(nameof(Color), Colors.White);

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    #endregion

    private Popup? _popup;
    private ColorPickerPopup? _pickerContent;

    public ColorPickerButton()
    {
        InitializeComponent();
        // НЕ устанавливаем DataContext = this!
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        ShowPopup();
    }

    private void ShowPopup()
    {
        // Создаем popup при первом открытии
        if (_popup == null)
        {
            _pickerContent = new ColorPickerPopup
            {
                SelectedColor = Color
            };

            _popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.BottomEdgeAlignedRight,
                IsLightDismissEnabled = true,
                Child = _pickerContent
            };

            // Подписываемся на выбор цвета
            _pickerContent.ColorConfirmed += OnColorConfirmed;
            _pickerContent.ColorCancelled += OnColorCancelled;
        }
        else
        {
            _pickerContent!.SelectedColor = Color;
        }

        _popup.IsOpen = true;
    }

    private void OnColorConfirmed(object? sender, Color color)
    {
        Color = color;
        _popup?.Close();
    }

    private void OnColorCancelled(object? sender, EventArgs e)
    {
        _popup?.Close();
    }
}