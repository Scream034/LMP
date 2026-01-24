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
        
        // Подписка на изменение размеров контейнеров для пересчета позиций
        SeekHitBox.PropertyChanged += OnSeekHitBoxPropertyChanged;
        VolumeHitBox.PropertyChanged += OnVolumeHitBoxPropertyChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        if (DataContext is PlayerBarViewModel vm)
        {
            // Используем WeakEventManager или просто подписку с отпиской, 
            // но в рамках UserControl, живущего долго, прямая подписка допустима.
            vm.PropertyChanged += (s, args) =>
            {
                // Оптимизация: обновляем UI только при изменении конкретных свойств
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

    // Обработка изменения размера окна/контрола
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

    #region Visual Updates (Direct Manipulation for Performance)

    private void UpdateSeekVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        
        double width = SeekHitBox.Bounds.Width;
        if (width <= 0 || vm.DurationSeconds <= 0) return;

        double ratio = vm.PositionSeconds / vm.DurationSeconds;
        ratio = Math.Clamp(ratio, 0, 1);
        double progressWidth = width * ratio;
        
        // Прямое изменение свойств контролов быстрее, чем Binding для частых обновлений
        ProgressBar.Width = progressWidth;
        Canvas.SetLeft(SeekThumb, progressWidth - 6); // 6 = radius of thumb
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

        double ratio = GetClickRatio(hitBox, e);
        double hoverSeconds = ratio * vm.DurationSeconds;

        // Показываем tooltip
        HoverTooltip.IsVisible = true;
        var hoverTime = TimeSpan.FromSeconds(hoverSeconds);
        HoverTimeText.Text = hoverTime.TotalHours >= 1
            ? hoverTime.ToString(@"h\:mm\:ss")
            : hoverTime.ToString(@"m\:ss");
        
        UpdateTooltipPosition(HoverTooltip, hitBox, e);

        // Если тянем - обновляем визуально Thumb сразу (отзывчивость)
        double thumbX = ratio * hitBox.Bounds.Width - 6;
        
        if (_isDraggingSeek)
        {
            Canvas.SetLeft(SeekThumb, thumbX);
            ProgressBar.Width = thumbX + 6; // Обновляем полосу прогресса при перетаскивании
            vm.UpdateSeekPosition(hoverSeconds);
        }
    }

    private void OnSeekAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || DataContext is not PlayerBarViewModel vm) return;
        if (!e.GetCurrentPoint(hitBox).Properties.IsLeftButtonPressed) return;
        if (!vm.HasTrack) return;

        _isDraggingSeek = true;
        e.Pointer.Capture(hitBox); // Захват мыши, чтобы можно было уводить курсор за пределы
        vm.StartSeek();
        SeekThumb.Classes.Add("dragging");

        // Сразу прыгаем в точку клика
        double ratio = GetClickRatio(hitBox, e);
        double pos = ratio * vm.DurationSeconds;
        
        // Визуальное обновление моментально
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
            UpdateSeekVisual(); // Возврат на реальную позицию, если просто увели мышь
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

        if (_isDraggingVolume)
        {
            double thumbX = ratio * hitBox.Bounds.Width - 6;
            Canvas.SetLeft(VolumeThumb, thumbX);
            VolumeBar.Width = thumbX + 6;
            
            vm.Volume = volumePercent;
            // Note: UpdateVolumeVisual вызывается через PropertyChanged ViewModel, 
            // но при драге мы обновляем UI напрямую для плавности.
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
        
        // Ограничиваем, чтобы тултип не вылезал за границы бара
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