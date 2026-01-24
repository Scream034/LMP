using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MyLiteMusicPlayer.Features.Player;

public partial class PlayerBarView : UserControl
{
    private bool _isDraggingSeek;
    private bool _isDraggingVolume;

    public PlayerBarView()
    {
        InitializeComponent();
        
        // Подписка на изменения для обновления визуала слайдеров
        SeekHitBox.PropertyChanged += OnSeekHitBoxPropertyChanged;
        VolumeHitBox.PropertyChanged += OnVolumeHitBoxPropertyChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        if (DataContext is PlayerBarViewModel vm)
        {
            vm.PropertyChanged += (s, args) =>
            {
                switch (args.PropertyName)
                {
                    case nameof(vm.PositionSeconds):
                    case nameof(vm.DurationSeconds):
                        UpdateSeekVisual();
                        break;
                    case nameof(vm.BufferedSeconds):
                        UpdateBufferVisual();
                        break;
                    case nameof(vm.Volume):
                    case nameof(vm.MaxVolume):
                        UpdateVolumeVisual();
                        break;
                }
            };
        }
    }

    private void OnSeekHitBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "Bounds")
        {
            UpdateSeekVisual();
            UpdateBufferVisual();
        }
    }

    private void OnVolumeHitBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "Bounds")
        {
            UpdateVolumeVisual();
        }
    }

    #region Visual Updates

    private void UpdateSeekVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        
        double width = SeekHitBox.Bounds.Width;
        if (width <= 0 || vm.DurationSeconds <= 0) return;

        double ratio = vm.PositionSeconds / vm.DurationSeconds;
        double progressWidth = width * ratio;
        
        ProgressBar.Width = progressWidth;
        Canvas.SetLeft(SeekThumb, progressWidth - 6); // 6 = половина ширины thumb
    }

    private void UpdateBufferVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        
        double width = SeekHitBox.Bounds.Width;
        if (width <= 0 || vm.DurationSeconds <= 0) return;

        double ratio = vm.BufferedSeconds / vm.DurationSeconds;
        BufferBar.Width = width * ratio;
    }

    private void UpdateVolumeVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        
        double width = VolumeHitBox.Bounds.Width;
        if (width <= 0 || vm.MaxVolume <= 0) return;

        double ratio = (double)vm.Volume / vm.MaxVolume;
        double progressWidth = width * ratio;
        
        VolumeBar.Width = progressWidth;
        Canvas.SetLeft(VolumeThumb, progressWidth - 6);
    }

    #endregion

    #region Seek Logic

    private void OnSeekAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (vm.DurationSeconds <= 0) return;

        double ratio = GetClickRatio(hitBox, e);
        double hoverSeconds = ratio * vm.DurationSeconds;

        // Показываем tooltip
        HoverTooltip.IsVisible = true;
        var hoverTime = TimeSpan.FromSeconds(hoverSeconds);
        HoverTimeText.Text = hoverTime.TotalHours >= 1
            ? hoverTime.ToString(@"h\:mm\:ss")
            : hoverTime.ToString(@"m\:ss");
        UpdateTooltipPosition(HoverTooltip, hitBox, e);

        // Обновляем thumb при наведении
        double thumbX = ratio * hitBox.Bounds.Width - 6;
        Canvas.SetLeft(SeekThumb, thumbX);

        if (_isDraggingSeek)
        {
            vm.UpdateSeekPosition(hoverSeconds);
            SeekThumb.Classes.Add("dragging");
        }
    }

    private void OnSeekAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (!e.GetCurrentPoint(hitBox).Properties.IsLeftButtonPressed) return;
        if (!vm.HasTrack) return;

        _isDraggingSeek = true;
        e.Pointer.Capture(hitBox);
        vm.StartSeek();
        SeekThumb.Classes.Add("dragging");

        double ratio = GetClickRatio(hitBox, e);
        vm.UpdateSeekPosition(ratio * vm.DurationSeconds);
    }

    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek) return;
        
        _isDraggingSeek = false;
        e.Pointer.Capture(null);
        SeekThumb.Classes.Remove("dragging");
        
        if (DataContext is PlayerBarViewModel vm)
        {
            vm.EndSeek();
        }
    }

    private void OnSeekAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSeek)
        {
            HoverTooltip.IsVisible = false;
            UpdateSeekVisual(); // Возвращаем thumb на актуальную позицию
        }
    }

    #endregion

    #region Volume Logic

    private void OnVolumeAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;

        double ratio = GetClickRatio(hitBox, e);
        int volumePercent = (int)(ratio * vm.MaxVolume);

        VolumeTooltip.IsVisible = true;
        VolumeTooltipText.Text = $"{volumePercent}%";
        UpdateTooltipPosition(VolumeTooltip, hitBox, e);

        // Обновляем thumb при наведении
        double thumbX = ratio * hitBox.Bounds.Width - 6;
        Canvas.SetLeft(VolumeThumb, thumbX);

        if (_isDraggingVolume)
        {
            vm.Volume = volumePercent;
            VolumeThumb.Classes.Add("dragging");
        }
    }

    private void OnVolumeAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (!e.GetCurrentPoint(hitBox).Properties.IsLeftButtonPressed) return;

        _isDraggingVolume = true;
        e.Pointer.Capture(hitBox);
        VolumeThumb.Classes.Add("dragging");

        double ratio = GetClickRatio(hitBox, e);
        vm.Volume = (int)(ratio * vm.MaxVolume);
    }

    private void OnVolumeAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingVolume) return;
        
        _isDraggingVolume = false;
        e.Pointer.Capture(null);
        VolumeThumb.Classes.Remove("dragging");

        if (DataContext is PlayerBarViewModel vm)
        {
            vm.OnVolumeChangeComplete();
        }
    }

    private void OnVolumeAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingVolume)
        {
            VolumeTooltip.IsVisible = false;
            UpdateVolumeVisual(); // Возвращаем thumb на актуальную позицию
        }
    }

    #endregion

    #region Helpers

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
        
        if (tooltip.RenderTransform is TranslateTransform tr)
        {
            tr.X = tooltipX;
        }
    }

    #endregion
}