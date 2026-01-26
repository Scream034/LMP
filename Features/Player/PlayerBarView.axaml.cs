using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace LMP.Features.Player;

public partial class PlayerBarView : UserControl
{
    private bool _isDraggingSeek;
    private bool _isDraggingVolume;

    public PlayerBarView()
    {
        InitializeComponent();
        
        SeekHitBox.PropertyChanged += OnSeekHitBoxPropertyChanged;
        VolumeHitBox.PropertyChanged += OnVolumeHitBoxPropertyChanged;
    }

    // FIX: Принудительно скрываем тултипы при уходе контрола из дерева (сворачивание, переключение табов)
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ForceHideTooltips();
    }

    private void ForceHideTooltips()
    {
        if (HoverTooltip != null) HoverTooltip.IsVisible = false;
        if (VolumeTooltip != null) VolumeTooltip.IsVisible = false;
        _isDraggingSeek = false;
        _isDraggingVolume = false;
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
                        if (!_isDraggingSeek) UpdateSeekVisual();
                        break;
                    case nameof(vm.BufferedSeconds):
                        UpdateBufferVisual();
                        break;
                    case nameof(vm.Volume):
                    case nameof(vm.MaxVolume):
                        if (!_isDraggingVolume) UpdateVolumeVisual();
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
        ratio = Math.Clamp(ratio, 0, 1);
        double progressWidth = width * ratio;
        
        ProgressBar.Width = progressWidth;
        Canvas.SetLeft(SeekThumb, progressWidth - 6);
    }

    private void UpdateBufferVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        
        double width = SeekHitBox.Bounds.Width;
        if (width <= 0 || vm.DurationSeconds <= 0) return;

        double ratio = vm.BufferedSeconds / vm.DurationSeconds;
        ratio = Math.Clamp(ratio, 0, 1);
        BufferBar.Width = width * ratio;
    }

    private void UpdateVolumeVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        
        double width = VolumeHitBox.Bounds.Width;
        if (width <= 0 || vm.MaxVolume <= 0) return;

        double ratio = (double)vm.Volume / vm.MaxVolume;
        ratio = Math.Clamp(ratio, 0, 1);
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

        // FIX: Если курсор ушел за пределы, скрываем тултип
        if (!hitBox.IsPointerOver && !_isDraggingSeek)
        {
            HoverTooltip.IsVisible = false;
            return;
        }

        double ratio = GetClickRatio(hitBox, e);
        double hoverSeconds = ratio * vm.DurationSeconds;

        HoverTooltip.IsVisible = true;
        var hoverTime = TimeSpan.FromSeconds(hoverSeconds);
        HoverTimeText.Text = hoverTime.TotalHours >= 1
            ? hoverTime.ToString(@"h\:mm\:ss")
            : hoverTime.ToString(@"m\:ss");
        
        UpdateTooltipPosition(HoverTooltip, hitBox, e);

        if (_isDraggingSeek)
        {
            double thumbX = ratio * hitBox.Bounds.Width - 6;
            Canvas.SetLeft(SeekThumb, thumbX);
            ProgressBar.Width = thumbX + 6;
            vm.UpdateSeekPosition(hoverSeconds);
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
        double pos = ratio * vm.DurationSeconds;
        
        double thumbX = ratio * hitBox.Bounds.Width - 6;
        Canvas.SetLeft(SeekThumb, thumbX);
        ProgressBar.Width = thumbX + 6;
        
        vm.UpdateSeekPosition(pos);
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
            UpdateSeekVisual();
        }
    }

    private void OnSeekAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        OnSeekAreaReleased(sender, null!);
        HoverTooltip.IsVisible = false;
    }

    #endregion

    #region Volume Logic

    // FEATURE: Скролл громкости
    private void OnVolumeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        
        int step = 5;
        // e.Delta.Y > 0 - вверх, < 0 - вниз
        int delta = e.Delta.Y > 0 ? step : -step;
        
        int newVolume = Math.Clamp(vm.Volume + delta, 0, vm.MaxVolume);
        
        if (newVolume != vm.Volume)
        {
            vm.Volume = newVolume;
            vm.OnVolumeChangeComplete();
            
            // Показываем тултип на короткое время при скролле
            VolumeTooltip.IsVisible = true;
            VolumeTooltipText.Text = $"{newVolume}%";
            
            // Центрируем тултип над слайдером если скроллим не над ним конкретно
            if (VolumeHitBox.Bounds.Width > 0)
            {
                double thumbX = ((double)newVolume / vm.MaxVolume) * VolumeHitBox.Bounds.Width;
                double tooltipX = thumbX - (VolumeTooltip.Bounds.Width / 2);
                 if (VolumeTooltip.RenderTransform is TranslateTransform tr) tr.X = tooltipX;
                 else VolumeTooltip.RenderTransform = new TranslateTransform(tooltipX, 0);
            }
            
            // Таймер для скрытия тултипа после скролла (упрощенно через async)
            // В реальном коде лучше CancellationToken, но для UI эффекта сойдет
            _ = System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => 
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                   if (!_isDraggingVolume && !VolumeHitBox.IsPointerOver) VolumeTooltip.IsVisible = false;
                }));
        }
        
        e.Handled = true;
    }

    private void OnVolumeAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;

        // FIX: Если курсор ушел за пределы, скрываем тултип
        if (!hitBox.IsPointerOver && !_isDraggingVolume)
        {
            VolumeTooltip.IsVisible = false;
            return;
        }

        double ratio = GetClickRatio(hitBox, e);
        int volumePercent = (int)(ratio * vm.MaxVolume);

        VolumeTooltip.IsVisible = true;
        VolumeTooltipText.Text = $"{volumePercent}%";
        UpdateTooltipPosition(VolumeTooltip, hitBox, e);

        if (_isDraggingVolume)
        {
            double thumbX = ratio * hitBox.Bounds.Width - 6;
            Canvas.SetLeft(VolumeThumb, thumbX);
            VolumeBar.Width = thumbX + 6;
            
            vm.Volume = volumePercent;
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
        double thumbX = ratio * hitBox.Bounds.Width - 6;
        
        Canvas.SetLeft(VolumeThumb, thumbX);
        VolumeBar.Width = thumbX + 6;

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
            UpdateVolumeVisual();
        }
    }

    private void OnVolumeAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        OnVolumeAreaReleased(sender, null!);
        VolumeTooltip.IsVisible = false;
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
        else
        {
            tooltip.RenderTransform = new TranslateTransform(tooltipX, 0);
        }
    }

    #endregion
}