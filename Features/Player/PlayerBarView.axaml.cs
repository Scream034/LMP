using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace LMP.Features.Player;

/// <summary>
/// Code-behind для панели управления плеером.
/// Обрабатывает взаимодействие с seek/volume слайдерами и визуальные обновления.
/// </summary>
public partial class PlayerBarView : UserControl
{
    #region Constants

    private const double SeekThumbDiameter = 14.0;
    private const double SeekThumbRadius = SeekThumbDiameter / 2.0;
    private const double SeekCursorHalfWidth = 1.0;

    private const double VolumeThumbDiameter = 12.0;
    private const double VolumeThumbRadius = VolumeThumbDiameter / 2.0;
    private const double VolumeSliderHeightPerPercent = 0.8;
    private const double VolumeSliderMinHeight = 60.0;

    private const int VolumeScrollStep = 1;
    private const int VolumePopupCloseDelayMs = 400;
    private const int VolumeTooltipHideDelayMs = 1500;
    private const int SparkAnimationIntervalMs = 16;
    private const double SparkSpeed = 6.0;
    private const double SparkWidth = 80.0;

    /// <summary>
    /// Минимальная ширина сегмента буфера в пикселях.
    /// Нужна чтобы даже маленький чанк был виден.
    /// </summary>
    private const double MinSegmentWidthPx = 2.0;

    #endregion

    #region State

    private bool _isDraggingSeek;
    private bool _isDraggingVolume;
    private bool _isVolumePopupHovered;
    private bool _isVolumeButtonHovered;
    private bool _isWindowActive = true;

    private double _seekDragRatio;
    private double _sparkPosition = -SparkWidth;

    /// <summary>
    /// Пул Border'ов для сегментов буфера.
    /// Используем абсолютное позиционирование внутри Grid через Margin.
    /// </summary>
    private readonly List<Border> _bufferSegments = [];

    /// <summary>
    /// Кэшированная кисть для сегментов буфера.
    /// Создаётся один раз, не пересоздаётся каждый кадр.
    /// </summary>
    private IBrush? _bufferBrushCache;

    private DispatcherTimer? _volumePopupCloseTimer;
    private DispatcherTimer? _volumeTooltipHideTimer;
    private DispatcherTimer? _sparkAnimationTimer;
    private FlyoutBase? _formatFlyout;

    private PlayerBarViewModel? _currentViewModel;

    #endregion

    public PlayerBarView()
    {
        InitializeComponent();
        SetupEventHandlers();
        SetupTimers();
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

    private void SetupTimers()
    {
        _volumePopupCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VolumePopupCloseDelayMs)
        };
        _volumePopupCloseTimer.Tick += (_, _) =>
        {
            if (!_isVolumePopupHovered && !_isVolumeButtonHovered && !_isDraggingVolume)
                VolumePopup.IsOpen = false;
            _volumePopupCloseTimer?.Stop();
        };

        _volumeTooltipHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VolumeTooltipHideDelayMs)
        };
        _volumeTooltipHideTimer.Tick += (_, _) =>
        {
            VolumeTooltipPopup.IsOpen = false;
            _volumeTooltipHideTimer?.Stop();
        };

        _sparkAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SparkAnimationIntervalMs)
        };
        _sparkAnimationTimer.Tick += OnSparkAnimationTick;
    }

    #endregion

    #region Lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (VisualRoot is Window window)
        {
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (VisualRoot is Window window)
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
        }

        CloseAllPopups();
        CancelSeekDrag();
        CancelVolumeDrag();
        StopSparkAnimation();

        VolumeButton.PointerEntered -= OnVolumeButtonEntered;
        VolumeButton.PointerExited -= OnVolumeButtonExited;
        VolumePopup.Opened -= OnVolumePopupOpened;

        _currentViewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _currentViewModel = null;

        ClearBufferSegments();
    }

    private void OnWindowActivated(object? sender, EventArgs e) => _isWindowActive = true;

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _isWindowActive = false;
        CloseAllPopups();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _currentViewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _currentViewModel = null;

        if (DataContext is PlayerBarViewModel vm)
        {
            _currentViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;

            InitializeVolumeSlider(vm);

            if (vm.IsLoading)
                StartSparkAnimation();
            else
                StopSparkAnimation();

            // Инициализируем буфер — ВАЖНО сделать после присвоения DataContext
            InvalidateBufferBrushCache();
            UpdateBufferVisual();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PlayerBarViewModel vm) return;

        // *** ДИАГНОСТИКА для буфера ***
        if (e.PropertyName == nameof(PlayerBarViewModel.BufferedRanges))
        {
            Log.Debug($"[PlayerBarView] PropertyChanged: BufferedRanges, count={vm.BufferedRanges.Count}");
        }

        if (e.PropertyName == nameof(PlayerBarViewModel.IsLoading))
        {
            if (vm.IsLoading)
                StartSparkAnimation();
            else
                StopSparkAnimation();
            return;
        }

        if (!_isWindowActive) return;

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

            case nameof(PlayerBarViewModel.BufferedRanges):
            case nameof(PlayerBarViewModel.IsFullyBuffered):
                Log.Debug($"[PlayerBarView] Calling UpdateBufferVisual, ranges={vm.BufferedRanges.Count}");
                UpdateBufferVisual();
                break;

            case nameof(PlayerBarViewModel.Volume):
                if (!_isDraggingVolume)
                    UpdateVolumeVisual();
                break;

            case nameof(PlayerBarViewModel.MaxVolume):
                UpdateVolumeSliderHeight(vm.MaxVolume);
                if (!_isDraggingVolume) UpdateVolumeVisual();
                break;
        }
    }

    private void InitializeVolumeSlider(PlayerBarViewModel vm)
    {
        try
        {
            int maxVolume = vm.MaxVolume > 0 ? vm.MaxVolume : 100;
            double height = Math.Max(VolumeSliderMinHeight, maxVolume * VolumeSliderHeightPerPercent);
            VolumeSliderPanel.Height = height;

            int volume = Math.Clamp(vm.Volume, 0, maxVolume);
            double ratio = (double)volume / maxVolume;

            VolumeBar.Height = height * ratio;
            double thumbTop = height * (1 - ratio) - VolumeThumbRadius;
            VolumeThumb.Margin = new Thickness(0, Math.Max(0, thumbTop), 0, 0);
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerBar] InitializeVolumeSlider error: {ex.Message}");
            VolumeSliderPanel.Height = VolumeSliderMinHeight;
            VolumeBar.Height = 0;
            VolumeThumb.Margin = new Thickness(0);
        }
    }

    #endregion

    #region Spark Animation

    private void StartSparkAnimation()
    {
        if (_sparkAnimationTimer == null) return;

        _sparkPosition = -SparkWidth;
        SparkRunner.Margin = new Thickness(_sparkPosition, 0, 0, 0);

        if (!_sparkAnimationTimer.IsEnabled)
            _sparkAnimationTimer.Start();
    }

    private void StopSparkAnimation()
    {
        if (_sparkAnimationTimer == null) return;

        _sparkAnimationTimer.Stop();
        SparkRunner.Margin = new Thickness(-SparkWidth, 0, 0, 0);
        _sparkPosition = -SparkWidth;
    }

    private void OnSparkAnimationTick(object? sender, EventArgs e)
    {
        if (!_isWindowActive) return;

        double containerWidth = SeekContainer.Bounds.Width;
        if (containerWidth <= 0) containerWidth = 600;

        _sparkPosition += SparkSpeed;

        if (_sparkPosition > containerWidth + SparkWidth)
            _sparkPosition = -SparkWidth;

        SparkRunner.Margin = new Thickness(_sparkPosition, 0, 0, 0);
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
            UpdateVolumeVisual();
    }

    private void OnVolumePopupOpened(object? sender, EventArgs e)
    {
        if (DataContext is PlayerBarViewModel vm)
        {
            if (VolumeSliderPanel.Height <= 0)
                UpdateVolumeSliderHeight(vm.MaxVolume);
            UpdateVolumeVisual();
        }
    }

    #endregion

    #region Buffer Segments Visual

    /// <summary>
    /// Обновляет визуал сегментированного буфера.
    /// </summary>
    private void UpdateBufferVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        // Берём ширину из SeekContainer — он всегда имеет правильные размеры
        double width = SeekContainer.Bounds.Width;
        if (width <= 0) return;

        var ranges = vm.BufferedRanges;

        // Полностью загружено, но ranges пустые
        if ((ranges == null || ranges.Count == 0) && vm.IsFullyBuffered)
            ranges = [(0.0, 1.0)];

        if (ranges == null || ranges.Count == 0)
        {
            HideAllBufferSegments();
            return;
        }

        EnsureBufferSegmentCount(ranges.Count);

        var brush = GetBufferBrush();

        for (int i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var segment = _bufferSegments[i];

            if (double.IsNaN(start) || double.IsInfinity(start)) start = 0;
            if (double.IsNaN(end) || double.IsInfinity(end)) end = 0;
            start = Math.Clamp(start, 0, 1);
            end = Math.Clamp(end, start, 1);

            double left = start * width;
            double segWidth = (end - start) * width;

            if (segWidth < MinSegmentWidthPx && segWidth > 0)
                segWidth = MinSegmentWidthPx;
            if (left + segWidth > width)
                segWidth = width - left;

            // Canvas абсолютное позиционирование
            Canvas.SetLeft(segment, left);
            Canvas.SetTop(segment, 0);
            segment.Width = Math.Max(0, segWidth);
            segment.Height = 4;
            segment.Background = brush;
            segment.IsVisible = segWidth > 0;
            segment.Opacity = vm.IsFullyBuffered ? 0.6 : 0.45;
        }

        for (int i = ranges.Count; i < _bufferSegments.Count; i++)
            _bufferSegments[i].IsVisible = false;
    }

    private void EnsureBufferSegmentCount(int needed)
    {
        while (_bufferSegments.Count < needed)
        {
            var segment = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
                Opacity = 0.45,
                Background = GetBufferBrush()
            };

            _bufferSegments.Add(segment);
            BufferSegmentsCanvas.Children.Add(segment);
        }
    }

    private void HideAllBufferSegments()
    {
        foreach (var seg in _bufferSegments)
            seg.IsVisible = false;
    }

    private void ClearBufferSegments()
    {
        _bufferSegments.Clear();
        BufferSegmentsCanvas.Children.Clear();
        _bufferBrushCache = null;
    }

    private IBrush GetBufferBrush()
    {
        if (_bufferBrushCache != null)
            return _bufferBrushCache;

        var app = Application.Current;
        if (app?.Resources.TryGetResource("TextSecondaryBrush", app.ActualThemeVariant, out var res) == true
            && res is IBrush b)
        {
            _bufferBrushCache = b;
            return b;
        }

        _bufferBrushCache = new SolidColorBrush(Color.FromArgb(100, 180, 180, 180));
        return _bufferBrushCache;
    }

    private void InvalidateBufferBrushCache() => _bufferBrushCache = null;

    #endregion

    #region Seek Visual

    /// <summary>
    /// Обновляет визуал seek-слайдера (позиция воспроизведения).
    /// </summary>
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

    /// <summary>
    /// Обновляет эффект свечения за прогресс-баром.
    /// </summary>
    private void UpdatePlayingGlow()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double width = SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;

        if (width <= 0 || duration <= 0) return;

        double ratio = Math.Clamp(vm.PositionSeconds / duration, 0, 1);
        PlayingGlow.Width = Math.Max(20, width * ratio);
    }

    private void UpdateSeekCursor(double x) =>
        Canvas.SetLeft(SeekCursor, x - SeekCursorHalfWidth);

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

    private void UpdateSeekPreview(double x) =>
        PreviewFill.Width = Math.Max(0, x);

    #endregion

    #region Volume Visual

    private void UpdateVolumeSliderHeight(int maxVolume)
    {
        if (maxVolume <= 0) maxVolume = 100;

        double height = Math.Max(VolumeSliderMinHeight, maxVolume * VolumeSliderHeightPerPercent);

        if (double.IsNaN(height) || double.IsInfinity(height))
            height = VolumeSliderMinHeight;

        VolumeSliderPanel.Height = height;
    }

    private void UpdateVolumeVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double height = VolumeSliderPanel.Height;
        int maxVolume = vm.MaxVolume;

        if (height <= 0 || double.IsNaN(height))
        {
            height = VolumeSliderMinHeight;
            VolumeSliderPanel.Height = height;
        }

        if (maxVolume <= 0) maxVolume = 100;

        double ratio = Math.Clamp((double)vm.Volume / maxVolume, 0, 1);
        UpdateVolumeVisualInternal(ratio, height);
    }

    private void UpdateVolumeVisualInternal(double ratio, double height)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) ratio = 0;
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            height = VolumeSliderMinHeight;

        ratio = Math.Clamp(ratio, 0, 1);

        double barHeight = height * ratio;
        if (double.IsNaN(barHeight) || barHeight < 0) barHeight = 0;

        VolumeBar.Height = barHeight;

        double thumbTop = height * (1 - ratio) - VolumeThumbRadius;
        thumbTop = Math.Max(0, thumbTop);
        if (double.IsNaN(thumbTop)) thumbTop = 0;

        VolumeThumb.Margin = new Thickness(0, thumbTop, 0, 0);
    }

    private void UpdateVolumeTooltip(int currentVolume, int maxVolume)
    {
        VolumeTooltipText.Text = $"{currentVolume}% / {maxVolume}%";
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

    #endregion

    #region Volume Popup Hover

    private void OnVolumeButtonEntered(object? sender, PointerEventArgs e)
    {
        _isVolumeButtonHovered = true;
        _volumePopupCloseTimer?.Stop();

        if (DataContext is PlayerBarViewModel vm)
        {
            try
            {
                if (VolumeSliderPanel.Height <= 0 || double.IsNaN(VolumeSliderPanel.Height))
                    InitializeVolumeSlider(vm);

                VolumePopup.IsOpen = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[PlayerBar] Failed to open volume popup: {ex.Message}");
            }
        }
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
            TryScheduleVolumePopupClose();
    }

    private void TryScheduleVolumePopupClose()
    {
        if (!_isDraggingVolume && !_isVolumePopupHovered && !_isVolumeButtonHovered)
            _volumePopupCloseTimer?.Start();
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
            vm.UpdateSeekPosition(seconds);
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

    private void OnSeekAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        CancelSeekDrag();

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
                vm.CancelSeek();
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

        _volumeTooltipHideTimer?.Stop();
        VolumeTooltipPopup.IsOpen = true;

        int delta = e.Delta.Y > 0 ? VolumeScrollStep : -VolumeScrollStep;
        int newVolume = Math.Clamp(vm.Volume + delta, 0, vm.MaxVolume);

        if (newVolume != vm.Volume)
        {
            vm.Volume = newVolume;
            vm.OnVolumeChangeComplete();
        }

        UpdateVolumeTooltip(newVolume, vm.MaxVolume);

        double height = VolumeSliderPanel.Height;
        double ratio = Math.Clamp((double)newVolume / vm.MaxVolume, 0, 1);
        double yOffset = height * (1 - ratio) - (height / 2);

        VolumeTooltipPopup.VerticalOffset = yOffset;

        e.Handled = true;
        _volumeTooltipHideTimer?.Start();
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

        UpdateVolumeTooltip(volumePercent, vm.MaxVolume);

        double yOffset = height * (1 - ratio) - (height / 2);
        VolumeTooltipPopup.VerticalOffset = yOffset;

        if (_isDraggingVolume)
        {
            UpdateVolumeVisualInternal(ratio, height);
            vm.Volume = volumePercent;
            VolumeTooltipPopup.IsOpen = true;
        }
        else if (hitBox.IsPointerOver)
        {
            VolumeTooltipPopup.IsOpen = true;
        }
        else
        {
            VolumeTooltipPopup.IsOpen = false;
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

        UpdateVolumeVisualInternal(ratio, height);
        vm.Volume = newVolume;

        UpdateVolumeTooltip(newVolume, vm.MaxVolume);
        double yOffset = height * (1 - ratio) - (height / 2);
        VolumeTooltipPopup.VerticalOffset = yOffset;

        VolumeTooltipPopup.IsOpen = true;
    }

    private void OnVolumeAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingVolume) return;

        if (DataContext is PlayerBarViewModel vm)
            vm.OnVolumeChangeComplete();

        CompleteVolumeDrag(e.Pointer);
        TryScheduleVolumePopupClose();
    }

    private void OnVolumeAreaExited(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingVolume)
            VolumeTooltipPopup.IsOpen = false;
    }

    private void OnVolumeAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        CancelVolumeDrag();

    private void CompleteVolumeDrag(IPointer pointer)
    {
        _isDraggingVolume = false;
        pointer.Capture(null);
        VolumeThumb.Classes.Remove("dragging");
        VolumeTooltipPopup.IsOpen = false;
    }

    private void CancelVolumeDrag()
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeThumb.Classes.Remove("dragging");
        }

        VolumeTooltipPopup.IsOpen = false;
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