using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Views;

public partial class PlayerBarView : UserControl
{
    private bool _isDraggingSeek;
    private bool _isDraggingVolume;

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

        HoverTooltip.IsVisible = true;
        var hoverTime = TimeSpan.FromSeconds(hoverSeconds);
        HoverTimeText.Text = hoverTime.TotalHours >= 1
            ? hoverTime.ToString(@"h\:mm\:ss")
            : hoverTime.ToString(@"m\:ss");
        UpdateTooltipPosition(HoverTooltip, hitBox, e);

        if (_isDraggingSeek) vm.UpdateSeekPosition(hoverSeconds);
    }

    private void OnSeekAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (!e.GetCurrentPoint(hitBox).Properties.IsLeftButtonPressed) return;
        if (!vm.HasTrack) return;

        _isDraggingSeek = true;
        e.Pointer.Capture(hitBox);
        vm.StartSeek();

        double ratio = GetClickRatio(hitBox, e);
        vm.UpdateSeekPosition(ratio * vm.DurationSeconds);
    }

    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek) return;
        _isDraggingSeek = false;
        e.Pointer.Capture(null);
        if (DataContext is PlayerBarViewModel vm) vm.EndSeek();
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
        int volumePercent = (int)(ratio * vm.MaxVolume);

        VolumeTooltip.IsVisible = true;
        VolumeTooltipText.Text = $"{volumePercent}%";
        UpdateTooltipPosition(VolumeTooltip, hitBox, e);

        if (_isDraggingVolume) vm.Volume = volumePercent;
    }

    private void OnVolumeAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (!e.GetCurrentPoint(hitBox).Properties.IsLeftButtonPressed) return;

        _isDraggingVolume = true;
        e.Pointer.Capture(hitBox);

        double ratio = GetClickRatio(hitBox, e);
        vm.Volume = (int)(ratio * vm.MaxVolume);
    }

    private void OnVolumeAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingVolume) return;
        _isDraggingVolume = false;
        e.Pointer.Capture(null);

        // Важно: Сохраняем настройки только при отпускании мыши, чтобы не спамить I/O
        if (DataContext is PlayerBarViewModel vm) vm.OnVolumeChangeComplete();
    }

    private void OnVolumeAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingVolume) VolumeTooltip.IsVisible = false;
    }

    #endregion

    #region Вспомогательные методы

    private static double GetClickRatio(Border hitBox, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(hitBox);
        return Math.Clamp(point.Position.X / hitBox.Bounds.Width, 0, 1);
    }

    private static void UpdateTooltipPosition(Border tooltip, Border hitBox, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(hitBox);
        double tooltipX = point.Position.X - (tooltip.Bounds.Width / 2);
        tooltipX = Math.Clamp(tooltipX, 0, hitBox.Bounds.Width - tooltip.Bounds.Width);
        if (tooltip.RenderTransform is TranslateTransform tr) tr.X = tooltipX;
    }

    #endregion
}