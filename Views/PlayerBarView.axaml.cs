using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Views;

public partial class PlayerBarView : UserControl
{
    private bool _isDraggingSeek = false;
    private bool _isDraggingVolume = false;

    public PlayerBarView()
    {
        InitializeComponent();
    }

    #region Логика перемотки (Seek)

    private void OnSeekAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm || vm.DurationSeconds <= 0) return;

        double ratio = GetClickRatio(hitBox, e);
        double hoverSeconds = ratio * vm.DurationSeconds;

        // Обновляем текст и позицию тултипа
        HoverTooltip.IsVisible = true;
        var hoverTime = TimeSpan.FromSeconds(hoverSeconds);
        HoverTimeText.Text = hoverTime.TotalHours >= 1 ? hoverTime.ToString(@"h\:mm\:ss") : hoverTime.ToString(@"m\:ss");
        UpdateTooltipPosition(HoverTooltip, hitBox, e);

        if (_isDraggingSeek)
        {
            vm.PositionSeconds = hoverSeconds;
        }
    }

    private void OnSeekAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        var properties = e.GetCurrentPoint(hitBox).Properties;
        if (!properties.IsLeftButtonPressed) return;

        _isDraggingSeek = true;

        // ВАЖНО: Захватываем указатель, чтобы не терять события вне границ
        e.Pointer.Capture(hitBox);

        vm.StartSeek();
        vm.PositionSeconds = GetClickRatio(hitBox, e) * vm.DurationSeconds;
    }

    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek) return;

        _isDraggingSeek = false;
        e.Pointer.Capture(null); // Освобождаем указатель

        if (DataContext is PlayerBarViewModel vm)
        {
            vm.EndSeek();
        }
    }

    private void OnSeekAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSeek) HoverTooltip.IsVisible = false;
    }

    #endregion

    #region Логика Громкости (Volume)

    private void OnVolumeAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;

        double ratio = GetClickRatio(hitBox, e);

        VolumeTooltip.IsVisible = true;
        VolumeTooltipText.Text = $"{(int)(ratio * 100)}%";
        UpdateTooltipPosition(VolumeTooltip, hitBox, e);

        if (_isDraggingVolume)
        {
            vm.Volume = (float)ratio;
        }
    }

    private void OnVolumeAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (!e.GetCurrentPoint(hitBox).Properties.IsLeftButtonPressed) return;

        _isDraggingVolume = true;
        e.Pointer.Capture(hitBox);

        vm.Volume = (float)GetClickRatio(hitBox, e);
    }

    private void OnVolumeAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingVolume) return;
        _isDraggingVolume = false;
        e.Pointer.Capture(null);
    }

    private void OnVolumeAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingVolume) VolumeTooltip.IsVisible = false;
    }

    #endregion

    #region Вспомогательные методы

    private double GetClickRatio(Border hitBox, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(hitBox);
        return Math.Clamp(point.Position.X / hitBox.Bounds.Width, 0, 1);
    }

    private void UpdateTooltipPosition(Border tooltip, Border hitBox, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(hitBox);
        double tooltipX = point.Position.X - (tooltip.Bounds.Width / 2);
        tooltipX = Math.Clamp(tooltipX, 0, hitBox.Bounds.Width - tooltip.Bounds.Width);

        if (tooltip.RenderTransform is TranslateTransform tr)
        {
            tr.X = tooltipX;
        }
    }
    #endregion
}