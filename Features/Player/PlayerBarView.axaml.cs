using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using System;

namespace LMP.Features.Player;

public partial class PlayerBarView : UserControl
{
    #region Layout Constants

    private const double SeekThumbDiameter = 14.0;
    private const double SeekThumbRadius = SeekThumbDiameter / 2.0;
    private const double SeekCursorHalfWidth = 1.0;

    private const double VolumeThumbDiameter = 14.0;
    private const double VolumeThumbRadius = VolumeThumbDiameter / 2.0;

    private const double VolumeSliderHeightPerPercent = 0.8;

    private const int VolumeScrollStep = 5;
    private const int VolumePopupCloseDelayMs = 400;

    #endregion

    #region State

    private bool _isDraggingSeek;
    private bool _isDraggingVolume;
    private bool _isVolumePopupHovered;
    private bool _isVolumeButtonHovered;

    private double _seekDragRatio;

    private DispatcherTimer? _volumePopupCloseTimer;
    private FlyoutBase? _formatFlyout;

    #endregion

    public PlayerBarView()
    {
        InitializeComponent();
        SetupEventHandlers();
        SetupVolumePopupTimer();
    }

    #region Initialization

    private void SetupEventHandlers()
    {
        SeekContainer.PropertyChanged += OnSeekContainerPropertyChanged;
        VolumeSliderPanel.PropertyChanged += OnVolumeSliderPropertyChanged;

        VolumeButton.PointerEntered += OnVolumeButtonEntered;
        VolumeButton.PointerExited += OnVolumeButtonExited;
        VolumePopup.Opened += OnVolumePopupOpened;

        _formatFlyout = FormatButton.Flyout;
        if (_formatFlyout != null)
        {
            _formatFlyout.Opened += (_, _) => FormatButton.Classes.Add("popup-open");
            _formatFlyout.Closed += (_, _) => FormatButton.Classes.Remove("popup-open");
        }

        KeyDown += OnKeyDown;
    }

    private void SetupVolumePopupTimer()
    {
        _volumePopupCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VolumePopupCloseDelayMs)
        };
        _volumePopupCloseTimer.Tick += (_, _) =>
        {
            if (!_isVolumePopupHovered && !_isVolumeButtonHovered && !_isDraggingVolume)
            {
                VolumePopup.IsOpen = false;
            }
            _volumePopupCloseTimer?.Stop();
        };
    }

    #endregion

    #region Lifecycle

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        CloseAllPopups();
        CancelSeekDrag();
        CancelVolumeDrag();

        VolumeButton.PointerEntered -= OnVolumeButtonEntered;
        VolumeButton.PointerExited -= OnVolumeButtonExited;
        VolumePopup.Opened -= OnVolumePopupOpened;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is PlayerBarViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PlayerBarViewModel vm) return;

        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.PositionSeconds):
            case nameof(PlayerBarViewModel.DurationSeconds):
                if (!_isDraggingSeek)
                {
                    UpdateSeekVisual();
                    UpdatePlayingGlow();
                }
                break;

            case nameof(PlayerBarViewModel.BufferedSeconds):
                UpdateBufferVisual();
                break;

            case nameof(PlayerBarViewModel.Volume):
                // Обновляем визуал только если не перетаскиваем
                // При перетаскивании визуал обновляется напрямую
                if (!_isDraggingVolume)
                {
                    UpdateVolumeVisual();
                }
                break;

            case nameof(PlayerBarViewModel.MaxVolume):
                UpdateVolumeSliderHeight(vm.MaxVolume);
                if (!_isDraggingVolume) UpdateVolumeVisual();
                break;
        }
    }

    #endregion

    #region Bounds Handlers

    private void OnSeekContainerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Bounds))
        {
            UpdateSeekVisual();
            UpdateBufferVisual();
            UpdatePlayingGlow();
        }
    }

    private void OnVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Bounds) || e.Property.Name == nameof(Height))
        {
            UpdateVolumeVisual();
        }
    }

    private void OnVolumePopupOpened(object? sender, EventArgs e)
    {
        if (DataContext is PlayerBarViewModel vm)
        {
            UpdateVolumeSliderHeight(vm.MaxVolume);
            UpdateVolumeVisual();
        }
    }

    #endregion

    #region Visual Updates

    private void UpdateSeekVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double width = SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;

        if (width <= 0 || duration <= 0) return;

        double ratio = Math.Clamp(vm.PositionSeconds / duration, 0, 1);
        double position = width * ratio;

        ProgressBar.Width = position;
        Canvas.SetLeft(SeekThumb, position - SeekThumbRadius);
    }

    private void UpdateBufferVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double width = SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;

        if (width <= 0 || duration <= 0) return;

        double ratio = Math.Clamp(vm.BufferedSeconds / duration, 0, 1);
        BufferBar.Width = width * ratio;
    }

    private void UpdatePlayingGlow()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double width = SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;

        if (width <= 0 || duration <= 0) return;

        double ratio = Math.Clamp(vm.PositionSeconds / duration, 0, 1);
        double position = width * ratio;

        PlayingGlow.Width = Math.Max(20, position);
    }

    private void UpdateVolumeSliderHeight(int maxVolume)
    {
        double height = Math.Max(60, maxVolume * VolumeSliderHeightPerPercent);
        VolumeSliderPanel.Height = height;
    }

    private void UpdateVolumeVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double height = VolumeSliderPanel.Height;
        int maxVolume = vm.MaxVolume;

        if (height <= 0 || maxVolume <= 0) return;

        double ratio = Math.Clamp((double)vm.Volume / maxVolume, 0, 1);
        UpdateVolumeVisualInternal(ratio, height);
    }

    /// <summary>
    /// Обновляет визуальные элементы громкости по заданному ratio.
    /// Используется и при перетаскивании, и при обычном обновлении.
    /// </summary>
    private void UpdateVolumeVisualInternal(double ratio, double height)
    {
        VolumeBar.Height = height * ratio;

        double thumbTop = height * (1 - ratio) - VolumeThumbRadius;
        VolumeThumb.Margin = new Thickness(0, Math.Max(0, thumbTop), 0, 0);
    }

    private void UpdateSeekCursor(double x)
    {
        Canvas.SetLeft(SeekCursor, x - SeekCursorHalfWidth);
    }

    private void UpdateSeekTooltip(double x, double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        HoverTimeText.Text = time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");

        SeekTooltipBorder.Measure(Size.Infinity);
        double tooltipWidth = SeekTooltipBorder.DesiredSize.Width;
        SeekTooltipPopup.HorizontalOffset = x - (tooltipWidth / 2);
    }

    private void UpdateSeekPreview(double x)
    {
        PreviewFill.Width = Math.Max(0, x);
    }

    private void UpdateVolumePreview(double ratio, double height)
    {
        VolumePreviewBar.Height = height * ratio;

        double yOffset = height * (1 - ratio) - (height / 2);
        VolumeTooltipPopup.VerticalOffset = yOffset;
    }

    #endregion

    #region Popup Helpers

    private void CloseAllPopups()
    {
        SeekTooltipPopup.IsOpen = false;
        VolumeTooltipPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
    }

    private void ShowSeekPreview() => PreviewFill.Classes.Add("active");
    private void HideSeekPreview()
    {
        PreviewFill.Classes.Remove("active");
        PreviewFill.Width = 0;
    }

    private void ShowVolumePreview() => VolumePreviewBar.Classes.Add("active");
    private void HideVolumePreview()
    {
        VolumePreviewBar.Classes.Remove("active");
        VolumePreviewBar.Height = 0;
    }

    #endregion

    #region Volume Popup Hover

    private void OnVolumeButtonEntered(object? sender, PointerEventArgs e)
    {
        _isVolumeButtonHovered = true;
        _volumePopupCloseTimer?.Stop();
        VolumePopup.IsOpen = true;
    }

    private void OnVolumeButtonExited(object? sender, PointerEventArgs e)
    {
        _isVolumeButtonHovered = false;
        TryScheduleVolumePopupClose();
    }

    private void OnVolumePopupContentEntered(object? sender, PointerEventArgs e)
    {
        _isVolumePopupHovered = true;
        _volumePopupCloseTimer?.Stop();
    }

    private void OnVolumePopupContentExited(object? sender, PointerEventArgs e)
    {
        _isVolumePopupHovered = false;
        if (!_isDraggingVolume)
        {
            TryScheduleVolumePopupClose();
        }
    }

    private void TryScheduleVolumePopupClose()
    {
        if (!_isDraggingVolume && !_isVolumePopupHovered && !_isVolumeButtonHovered)
        {
            _volumePopupCloseTimer?.Start();
        }
    }

    #endregion

    #region Seek Slider

    private void OnSeekAreaMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        if (vm.DurationSeconds <= 0) return;

        var point = e.GetCurrentPoint(SeekContainer);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelSeekDrag();
            return;
        }

        double width = SeekContainer.Bounds.Width;
        if (width <= 0) return;

        double x = Math.Clamp(point.Position.X, 0, width);
        double ratio = x / width;
        double seconds = ratio * vm.DurationSeconds;

        UpdateSeekCursor(x);
        UpdateSeekTooltip(x, seconds);

        if (_isDraggingSeek)
        {
            _seekDragRatio = ratio;
            ShowSeekPreview();
            UpdateSeekPreview(x);
            SeekTooltipPopup.IsOpen = true;
        }
        else if (SeekHitBox.IsPointerOver)
        {
            ShowSeekPreview();
            UpdateSeekPreview(x);
            SeekTooltipPopup.IsOpen = true;
        }
        else
        {
            SeekTooltipPopup.IsOpen = false;
            HideSeekPreview();
        }
    }

    private void OnSeekAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        if (!vm.HasTrack) return;

        var point = e.GetCurrentPoint(SeekContainer);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelSeekDrag();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingSeek = true;
        e.Pointer.Capture(SeekHitBox);
        vm.StartSeek();
        SeekContainer.Classes.Add("dragging");

        double width = SeekContainer.Bounds.Width;
        if (width <= 0) return;

        double x = Math.Clamp(point.Position.X, 0, width);
        double ratio = x / width;

        _seekDragRatio = ratio;

        ShowSeekPreview();
        UpdateSeekPreview(x);
        UpdateSeekCursor(x);
        UpdateSeekTooltip(x, ratio * vm.DurationSeconds);
        SeekTooltipPopup.IsOpen = true;
    }

    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek) return;

        if (DataContext is PlayerBarViewModel vm)
        {
            double targetSeconds = _seekDragRatio * vm.DurationSeconds;
            vm.UpdateSeekPosition(targetSeconds);
            vm.EndSeek();
        }

        CompleteSeekDrag(e.Pointer);
    }

    private void OnSeekAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSeek)
        {
            SeekTooltipPopup.IsOpen = false;
            HideSeekPreview();
        }
    }

    private void OnSeekAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CancelSeekDrag();
    }

    private void CompleteSeekDrag(IPointer pointer)
    {
        _isDraggingSeek = false;
        pointer.Capture(null);
        SeekContainer.Classes.Remove("dragging");
        SeekTooltipPopup.IsOpen = false;
        HideSeekPreview();
    }

    private void CancelSeekDrag()
    {
        if (_isDraggingSeek)
        {
            _isDraggingSeek = false;
            SeekContainer.Classes.Remove("dragging");

            if (DataContext is PlayerBarViewModel vm)
            {
                vm.CancelSeek();
            }
        }

        SeekTooltipPopup.IsOpen = false;
        HideSeekPreview();
        UpdateSeekVisual();
    }

    #endregion

    #region Volume Slider

    private void OnVolumeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        int delta = e.Delta.Y > 0 ? VolumeScrollStep : -VolumeScrollStep;
        int newVolume = Math.Clamp(vm.Volume + delta, 0, vm.MaxVolume);

        if (newVolume != vm.Volume)
        {
            vm.Volume = newVolume;
            vm.OnVolumeChangeComplete();
        }

        e.Handled = true;
    }

    private void OnVolumeAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox) return;
        if (DataContext is not PlayerBarViewModel vm) return;

        var point = e.GetCurrentPoint(VolumeSliderPanel);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelVolumeDrag();
            return;
        }

        double height = VolumeSliderPanel.Height;
        if (height <= 0) return;

        double y = Math.Clamp(point.Position.Y, 0, height);
        double ratio = 1 - (y / height);
        int volumePercent = (int)(ratio * vm.MaxVolume);

        VolumeTooltipText.Text = $"{volumePercent}%";

        if (_isDraggingVolume)
        {
            // ✨ Плавное обновление при перетаскивании
            // Обновляем визуал напрямую для максимальной плавности
            UpdateVolumeVisualInternal(ratio, height);
            
            // Обновляем значение в ViewModel (будет применено с анимацией через binding)
            vm.Volume = volumePercent;
            
            ShowVolumePreview();
            UpdateVolumePreview(ratio, height);
            VolumeTooltipPopup.IsOpen = true;
        }
        else if (hitBox.IsPointerOver)
        {
            ShowVolumePreview();
            UpdateVolumePreview(ratio, height);
            VolumeTooltipPopup.IsOpen = true;
        }
        else
        {
            VolumeTooltipPopup.IsOpen = false;
            HideVolumePreview();
        }
    }

    private void OnVolumeAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox) return;
        if (DataContext is not PlayerBarViewModel vm) return;

        var point = e.GetCurrentPoint(VolumeSliderPanel);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelVolumeDrag();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingVolume = true;
        e.Pointer.Capture(hitBox);
        VolumeThumb.Classes.Add("dragging");

        double height = VolumeSliderPanel.Height;
        if (height <= 0) return;

        double y = Math.Clamp(point.Position.Y, 0, height);
        double ratio = 1 - (y / height);
        int newVolume = (int)(ratio * vm.MaxVolume);

        // ✨ Сразу обновляем визуал и значение при клике
        UpdateVolumeVisualInternal(ratio, height);
        vm.Volume = newVolume;

        ShowVolumePreview();
        UpdateVolumePreview(ratio, height);
    }

    private void OnVolumeAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingVolume) return;

        if (DataContext is PlayerBarViewModel vm)
        {
            // Сохраняем громкость после завершения перетаскивания
            vm.OnVolumeChangeComplete();
        }

        CompleteVolumeDrag(e.Pointer);
        TryScheduleVolumePopupClose();
    }

    private void OnVolumeAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingVolume)
        {
            VolumeTooltipPopup.IsOpen = false;
            HideVolumePreview();
        }
    }

    private void OnVolumeAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CancelVolumeDrag();
    }

    private void CompleteVolumeDrag(IPointer pointer)
    {
        _isDraggingVolume = false;
        pointer.Capture(null);
        VolumeThumb.Classes.Remove("dragging");
        VolumeTooltipPopup.IsOpen = false;
        HideVolumePreview();
    }

    private void CancelVolumeDrag()
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeThumb.Classes.Remove("dragging");
        }

        VolumeTooltipPopup.IsOpen = false;
        HideVolumePreview();
        UpdateVolumeVisual();
    }

    #endregion

    #region Keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelSeekDrag();
            CancelVolumeDrag();
            e.Handled = true;
        }
    }

    #endregion
}