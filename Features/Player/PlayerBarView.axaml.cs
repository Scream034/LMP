using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace LMP.Features.Player;

/// <summary>
/// Code-behind для панели управления плеером.
/// </summary>
public partial class PlayerBarView : UserControl
{
    #region Constants

    private const double SeekThumbDiameter = 14.0;
    private const double SeekThumbRadius = SeekThumbDiameter / 2.0;
    private const double SeekCursorHalfWidth = 1.0;

    private const double VolumeThumbDiameter = 12.0;
    private const double VolumeThumbRadius = VolumeThumbDiameter / 2.0;
    private const double VolumeSliderMinHeight = 60.0;
    private const double VolumeSliderMaxHeight = 200.0;

    private const int VolumePopupCloseDelayMs = 400;
    private const int VolumeTooltipHideDelayMs = 1500;
    private const int SparkAnimationIntervalMs = 16;
    private const double SparkSpeed = 6.0;
    private const double SparkWidth = 80.0;
    private const double MinSegmentWidthPx = 2.0;
    private const double VolumePopupContentWidth = 28.0;

    #endregion

    #region State

    private bool _isDraggingSeek;
    private bool _isDraggingVolume;
    private bool _isVolumePopupHovered;
    private bool _isVolumeButtonHovered;
    private bool _isWindowActive = true;
    private bool _isSuspended;
    
    /// <summary>
    /// Флаг: тултип громкости активен (показывать слева от слайдера).
    /// </summary>
    private bool _isVolumeTooltipActive;

    private double _seekDragRatio;
    private double _sparkPosition = -SparkWidth;
    private double _cachedSeekWidth;

    private readonly List<Border> _bufferSegments = [];
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
        VolumePopup.Closed += OnVolumePopupClosed;

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
            _isVolumeTooltipActive = false;
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
        VolumePopup.Closed -= OnVolumePopupClosed;

        _currentViewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _currentViewModel = null;

        ClearBufferSegments();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _isWindowActive = true;

        if (!_isSuspended)
        {
            UpdateSeekVisual();
            UpdateBufferVisual();
            UpdatePlayingGlow();
        }
    }

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
            vm.RegisterView(this);

            InitializeVolumeSlider(vm);

            if (vm.IsLoading)
                StartSparkAnimation();
            else
                StopSparkAnimation();

            InvalidateBufferBrushCache();
            UpdateBufferVisual();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PlayerBarViewModel vm) return;

        if (e.PropertyName == nameof(PlayerBarViewModel.IsLoading))
        {
            if (vm.IsLoading)
                StartSparkAnimation();
            else
                StopSparkAnimation();
            return;
        }

        if (e.PropertyName == nameof(PlayerBarViewModel.IsTrackResetting))
        {
            if (vm.IsTrackResetting)
                ApplySliderReset();
            else
                RemoveSliderReset();
            return;
        }

        if (_isSuspended || !_isWindowActive) return;

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

    private void ApplySliderReset()
    {
        ProgressBar.Classes.Add("hidden");
        ProgressBar.Width = 0;
        SeekThumb.Classes.Add("hidden");
        SeekCursor.Classes.Add("hidden");
        HideAllBufferSegments();
        PlayingGlow.Width = 0;
    }

    private void RemoveSliderReset()
    {
        ProgressBar.Classes.Remove("hidden");
        SeekThumb.Classes.Remove("hidden");
        SeekCursor.Classes.Remove("hidden");

        UpdateSeekVisual();
        UpdateBufferVisual();
        UpdatePlayingGlow();
    }

    public void OnSuspend()
    {
        _isSuspended = true;
        StopSparkAnimation();
        PlayingGlow.Classes.Add("suspended");
        CloseAllPopups();
    }

    public void OnResume()
    {
        _isSuspended = false;
        PlayingGlow.Classes.Remove("suspended");

        if (_currentViewModel?.IsLoading == true)
            StartSparkAnimation();

        _cachedSeekWidth = SeekContainer.Bounds.Width;
        UpdateSeekVisual();
        UpdateBufferVisual();
        UpdatePlayingGlow();
        UpdateVolumeVisual();
    }

    private void InitializeVolumeSlider(PlayerBarViewModel vm)
    {
        try
        {
            int maxVolume = vm.MaxVolume > 0 ? vm.MaxVolume : 100;
            double height = ComputeVolumeSliderHeight(maxVolume);
            VolumeSliderPanel.Height = height;

            int volume = Math.Clamp(vm.Volume, 0, maxVolume);
            double ratio = (double)volume / maxVolume;

            VolumeBar.Height = height * ratio;
            double thumbTop = height * (1 - ratio) - VolumeThumbRadius;
            VolumeThumb.Margin = new Thickness(0, Math.Max(0, thumbTop), 0, 0);

            UpdateVolumePopupOffset();
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
        if (_sparkAnimationTimer == null || _isSuspended) return;

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
        if (!_isWindowActive || _isSuspended) return;

        double containerWidth = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
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
            _cachedSeekWidth = SeekContainer.Bounds.Width;

            if (!_isSuspended)
            {
                UpdateSeekVisual();
                UpdateBufferVisual();
                UpdatePlayingGlow();
            }
        }
    }

    private void OnVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Bounds) || e.Property.Name == nameof(Height))
        {
            UpdateVolumeVisual();
            UpdateVolumePopupOffset();
        }
    }

    private void OnVolumePopupOpened(object? sender, EventArgs e)
    {
        if (DataContext is PlayerBarViewModel vm)
        {
            if (VolumeSliderPanel.Height <= 0)
                UpdateVolumeSliderHeight(vm.MaxVolume);
            UpdateVolumeVisual();
            UpdateVolumePopupOffset();
        }
    }

    private void OnVolumePopupClosed(object? sender, EventArgs e)
    {
        // Скрываем тултип при закрытии popup
        _isVolumeTooltipActive = false;
        VolumeTooltipPopup.IsOpen = false;
    }

    private void UpdateVolumePopupOffset()
    {
        double buttonWidth = VolumeButton.Width;
        if (double.IsNaN(buttonWidth) || buttonWidth <= 0) buttonWidth = 38;

        double popupWidth = VolumePopupContentWidth + 2;
        double offset = (buttonWidth - popupWidth) / 2.0;

        VolumePopup.HorizontalOffset = offset;
    }

    #endregion

    #region Buffer Segments Visual

    private void UpdateBufferVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        if (width <= 0) return;

        if (vm.IsTrackResetting)
        {
            HideAllBufferSegments();
            return;
        }

        var ranges = vm.BufferedRanges;

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

            double left = Math.Round(start * width);
            double segWidth = Math.Round((end - start) * width);

            if (segWidth < MinSegmentWidthPx && segWidth > 0)
                segWidth = MinSegmentWidthPx;
            if (left + segWidth > width)
                segWidth = width - left;

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

    private void UpdateSeekVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        if (vm.IsTrackResetting) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;

        if (width <= 0 || duration <= 0) return;

        double ratio = Math.Clamp(vm.PositionSeconds / duration, 0, 1);
        double position = width * ratio;

        ProgressBar.Width = position;
        Canvas.SetLeft(SeekThumb, position - SeekThumbRadius);
    }

    private void UpdatePlayingGlow()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        if (vm.IsTrackResetting) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
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

    private static double ComputeVolumeSliderHeight(int maxVolume)
    {
        if (maxVolume <= 0) maxVolume = 100;

        const double baseHeight = 80.0;
        double extra = maxVolume > 100
            ? 40.0 * Math.Log2(1 + (maxVolume - 100) / 200.0)
            : 0;

        double height = baseHeight + extra;
        return Math.Clamp(height, VolumeSliderMinHeight, VolumeSliderMaxHeight);
    }

    private void UpdateVolumeSliderHeight(int maxVolume)
    {
        double height = ComputeVolumeSliderHeight(maxVolume);

        if (double.IsNaN(height) || double.IsInfinity(height))
            height = VolumeSliderMinHeight;

        VolumeSliderPanel.Height = height;
        UpdateVolumePopupOffset();
    }

    private void UpdateVolumeVisual()
    {
        if (DataContext is not PlayerBarViewModel vm) return;

        double height = VolumeSliderPanel.Height;
        int maxVolume = vm.MaxVolume;

        if (height <= 0 || double.IsNaN(height))
        {
            height = ComputeVolumeSliderHeight(maxVolume);
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

    /// <summary>
    /// Показывает тултип громкости слева от слайдера.
    /// </summary>
    private void ShowVolumeTooltip(int currentVolume, int maxVolume, double ratio)
    {
        VolumeTooltipText.Text = $"{currentVolume}% / {maxVolume}%";

        double height = VolumeSliderPanel.Height;
        double yOffset = height * (1 - ratio) - (height / 2);
        VolumeTooltipPopup.VerticalOffset = yOffset;

        _isVolumeTooltipActive = true;
        VolumeTooltipPopup.IsOpen = true;

        _volumeTooltipHideTimer?.Stop();
        _volumeTooltipHideTimer?.Start();
    }

    #endregion

    #region Popup Helpers

    private void CloseAllPopups()
    {
        SeekTooltipPopup.IsOpen = false;
        VolumeTooltipPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        _isVolumeTooltipActive = false;
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

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
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

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
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

        // Получаем шаг из ViewModel
        int step = vm.GetVolumeScrollStep();
        int delta = e.Delta.Y > 0 ? step : -step;
        int newVolume = Math.Clamp(vm.Volume + delta, 0, vm.MaxVolume);

        if (newVolume != vm.Volume)
        {
            vm.Volume = newVolume;
            vm.OnVolumeChangeComplete();
        }

        // Показываем тултип слева от слайдера
        double ratio = Math.Clamp((double)newVolume / vm.MaxVolume, 0, 1);
        ShowVolumeTooltip(newVolume, vm.MaxVolume, ratio);

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

        if (_isDraggingVolume)
        {
            UpdateVolumeVisualInternal(ratio, height);
            vm.Volume = volumePercent;
            ShowVolumeTooltip(volumePercent, vm.MaxVolume, ratio);
        }
        else if (hitBox.IsPointerOver)
        {
            ShowVolumeTooltip(volumePercent, vm.MaxVolume, ratio);
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

        ShowVolumeTooltip(newVolume, vm.MaxVolume, ratio);
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
        if (!_isDraggingVolume && !_isVolumeTooltipActive)
        {
            _volumeTooltipHideTimer?.Stop();
            VolumeTooltipPopup.IsOpen = false;
        }
    }

    private void OnVolumeAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        CancelVolumeDrag();

    private void CompleteVolumeDrag(IPointer pointer)
    {
        _isDraggingVolume = false;
        pointer.Capture(null);
        VolumeThumb.Classes.Remove("dragging");
        
        // Не скрываем тултип сразу — пусть таймер скроет
    }

    private void CancelVolumeDrag()
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeThumb.Classes.Remove("dragging");
        }

        _isVolumeTooltipActive = false;
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