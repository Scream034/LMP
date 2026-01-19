using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MyLiteMusicPlayer.ViewModels;
using System;

namespace MyLiteMusicPlayer.Views;

public partial class PlayerBarView : UserControl
{
    private bool _isDragging = false;

    public PlayerBarView()
    {
        InitializeComponent();
    }

    // Движение мыши: показываем время + (если тянем) обновляем позицию
    private void OnSeekAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (vm.DurationSeconds <= 0) return;

        var point = e.GetCurrentPoint(hitBox);
        double width = hitBox.Bounds.Width;
        if (width <= 0) return;

        // Расчет времени под курсором
        double ratio = Math.Clamp(point.Position.X / width, 0, 1);
        double hoverSeconds = ratio * vm.DurationSeconds;
        var hoverTime = TimeSpan.FromSeconds(hoverSeconds);

        // Обновляем Tooltip
        HoverTooltip.IsVisible = true;
        HoverTimeText.Text = hoverTime.TotalHours >= 1
            ? hoverTime.ToString(@"h\:mm\:ss")
            : hoverTime.ToString(@"m\:ss");

        // Двигаем Tooltip за мышкой (центрируем)
        double tooltipX = point.Position.X - (HoverTooltip.Bounds.Width / 2);
        // Ограничиваем, чтобы не вылезал за края
        tooltipX = Math.Clamp(tooltipX, 0, width - HoverTooltip.Bounds.Width);

        if (HoverTooltip.RenderTransform is TranslateTransform tr)
        {
            tr.X = tooltipX;
        }
        else
        {
            HoverTooltip.RenderTransform = new TranslateTransform(tooltipX, 0);
        }

        // Если зажата кнопка (перетаскивание)
        if (_isDragging)
        {
            vm.PositionSeconds = hoverSeconds;
        }
    }

    // Нажатие: начало перемотки
    private void OnSeekAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (!e.GetCurrentPoint(hitBox).Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        vm.StartSeek(); // Останавливаем таймер в VM

        // Сразу прыгаем в точку клика
        var point = e.GetCurrentPoint(hitBox);
        double width = hitBox.Bounds.Width;
        if (width > 0)
        {
            double ratio = Math.Clamp(point.Position.X / width, 0, 1);
            vm.PositionSeconds = ratio * vm.DurationSeconds;
        }
    }

    // Отпускание: конец перемотки
    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        if (DataContext is PlayerBarViewModel vm)
        {
            vm.EndSeek(); // Применяем перемотку в движке
        }
    }

    // Уход мыши: скрываем тултип
    private void OnSeekAreaExited(object? sender, PointerEventArgs e)
    {
        // Если тащим, не скрываем, пока не отпустим (опционально)
        if (!_isDragging)
        {
            HoverTooltip.IsVisible = false;
        }
    }
}