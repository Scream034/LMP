using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;

namespace LMP.Features.Player;

public partial class PlayerBarView : UserControl
{
    #region Constants

    private const double SeekThumbRadius = 7.0;
    private const double SeekCursorHalfWidth = 1.0;

    private const double VolumeThumbRadius = 6.0;
    private const double VolumeSliderMinHeight = 60.0;
    private const double VolumeSliderMaxHeight = 200.0;

    private const int VolumePopupCloseDelayMs = 400;
    private const int VolumeTooltipHideDelayMs = 1500;
    private const int SparkAnimationIntervalMs = 16;
    private const double SparkSpeed = 6.0;
    private const double SparkWidth = 80.0;
    private const double MinSegmentWidthPx = 2.0;
    private const double VolumePopupContentWidth = 28.0;

    private const int SeekHintAutoHideMs = 2500;
    private const int SeekCancelledHintMs = 1500;

    #endregion

    #region State

    private bool _isDraggingSeek;
    private bool _isDraggingVolume;
    private bool _isVolumePopupHovered;
    private bool _isVolumeButtonHovered;
    private bool _isSuspended;

    private double _seekDragRatio;
    private double _sparkPosition = -SparkWidth;
    private double _cachedSeekWidth;

    private readonly List<Border> _bufferSegments = [];
    private IBrush? _bufferBrushCache;

    /// <summary>
    /// Rx-based timed disposables. SerialDisposable автоматически отменяет предыдущий таймер.
    /// </summary>
    private readonly SerialDisposable _volumePopupCloseDisposable = new();
    private readonly SerialDisposable _volumeTooltipHideDisposable = new();
    private readonly SerialDisposable _seekHintDisposable = new();

    /// <summary>
    /// Spark анимация остаётся на DispatcherTimer т.к. нужен frame-rate update (16ms).
    /// Observable.Interval не гарантирует такую точность из-за scheduler overhead.
    /// </summary>
    private DispatcherTimer? _sparkAnimationTimer;

    private FlyoutBase? _formatFlyout;
    private PlayerBarViewModel? _currentViewModel;

    /// <summary>
    /// Флаг: hover-подписки на VolumeButton сейчас активны.
    /// Единая точка управления — <see cref="SetVolumeHoverEnabled"/>.
    /// </summary>
    private bool _volumeHoverSubscribed;

    #endregion

    public PlayerBarView()
    {
        InitializeComponent();
        SetupEventHandlers();
        SetupSparkTimer();
    }

    #region Initialization

    private void SetupEventHandlers()
    {
        SeekContainer.PropertyChanged += OnSeekContainerPropertyChanged;
        VolumeSliderPanel.PropertyChanged += OnVolumeSliderPropertyChanged;

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

    private void SetupSparkTimer()
    {
        _sparkAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SparkAnimationIntervalMs)
        };
        _sparkAnimationTimer.Tick += OnSparkAnimationTick;
    }

    #endregion

    #region Lifecycle

    private void OnShuffleButtonEntered(object? sender, PointerEventArgs e)
        => ShufflePopup.IsOpen = true;

    private void OnShuffleButtonExited(object? sender, PointerEventArgs e)
        => ShufflePopup.IsOpen = false;

    private void OnShuffleButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            _currentViewModel?.ToggleAutoShuffleCommand.Execute().Subscribe();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (VisualRoot is Window window)
        {
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
        }

        // Окно считается активным при attach — включаем hover
        SetVolumeHoverEnabled(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (VisualRoot is Window window)
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
        }

        SetVolumeHoverEnabled(false);
        VolumePopup.Opened -= OnVolumePopupOpened;
        VolumePopup.Closed -= OnVolumePopupClosed;

        CloseAllPopups();
        CancelSeekDrag();
        CancelVolumeDrag();
        StopSparkAnimation();

        UnsubscribeFromViewModel();
        ClearBufferSegments();

        _volumePopupCloseDisposable.Dispose();
        _volumeTooltipHideDisposable.Dispose();
        _seekHintDisposable.Dispose();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        SetVolumeHoverEnabled(true);

        if (!_isSuspended)
            RefreshAllVisuals();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        SetVolumeHoverEnabled(false);
        CloseAllPopups();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        UnsubscribeFromViewModel();

        if (DataContext is PlayerBarViewModel vm)
        {
            _currentViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.SuspendRequested += OnSuspend;
            vm.ResumeRequested += OnResume;

            InitializeVolumeSlider(vm);

            if (vm.IsLoading)
                StartSparkAnimation();
            else
                StopSparkAnimation();

            InvalidateBufferBrushCache();
            UpdateBufferVisual();
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _currentViewModel.SuspendRequested -= OnSuspend;
            _currentViewModel.ResumeRequested -= OnResume;
            _currentViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PlayerBarViewModel vm) return;

        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.IsLoading):
                if (_isSuspended) return;
                if (vm.IsLoading) StartSparkAnimation();
                else StopSparkAnimation();
                return;

            case nameof(PlayerBarViewModel.IsTrackResetting):
                if (vm.IsTrackResetting) ApplySliderReset();
                else RemoveSliderReset();
                return;
        }

        if (_isSuspended) return;

        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.PositionSeconds):
            case nameof(PlayerBarViewModel.DurationSeconds):
                if (!_isDraggingSeek)
                    UpdateSeekAndGlow();
                break;

            case nameof(PlayerBarViewModel.BufferedRanges):
            case nameof(PlayerBarViewModel.IsFullyBuffered):
                UpdateBufferVisual();
                break;

            case nameof(PlayerBarViewModel.Volume):
                if (!_isDraggingVolume) UpdateVolumeVisual();
                break;

            case nameof(PlayerBarViewModel.MaxVolume):
                UpdateVolumeSliderHeight(vm.MaxVolume);
                if (!_isDraggingVolume) UpdateVolumeVisual();
                break;

            case nameof(PlayerBarViewModel.IsSeekBusy):
                if (!vm.IsSeekBusy) UpdateBufferVisual();
                break;
        }
    }

    private void ApplySliderReset()
    {
        ProgressBar.Classes.Add("hidden");
        ProgressBar.Width = 0;
        SeekThumb.Classes.Add("hidden");
        SeekCursor.Classes.Add("hidden");
        PlayingGlow.Width = 0;

        HideAllBufferSegments();

        if (!_isSuspended)
            StartSparkAnimation();
    }

    private void RemoveSliderReset()
    {
        ProgressBar.Classes.Remove("hidden");
        SeekThumb.Classes.Remove("hidden");
        SeekCursor.Classes.Remove("hidden");

        if (_currentViewModel?.IsLoading != true)
            StopSparkAnimation();

        if (!_isSuspended)
            RefreshAllVisuals();
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
        RefreshAllVisuals();
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

            ApplyVolumeVisual(ratio, height);
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

    #region Unified Visual Updates

    /// <summary>
    /// Обновляет все визуальные элементы. Вызывается при Resume, Activated, RemoveSliderReset.
    /// Единая точка вместо дублирования 4 вызовов в 5 местах.
    /// </summary>
    private void RefreshAllVisuals()
    {
        UpdateSeekAndGlow();
        UpdateBufferVisual();
        UpdateVolumeVisual();
    }

    /// <summary>
    /// Объединённое обновление seek progress и playing glow (одна формула ratio).
    /// </summary>
    private void UpdateSeekAndGlow()
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        if (vm.IsTrackResetting) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;

        if (width <= 0 || duration <= 0) return;

        double ratio = Math.Clamp(vm.PositionSeconds / duration, 0, 1);
        double position = width * ratio;

        // Seek visual
        ProgressBar.Width = position;
        Canvas.SetLeft(SeekThumb, position - SeekThumbRadius);

        // Playing glow
        PlayingGlow.Width = Math.Max(20, position);
    }

    #endregion

    #region Volume Hover (single toggle point)

    /// <summary>
    /// Единая точка управления hover-подписками на VolumeButton.
    /// Предотвращает двойную подписку через флаг <see cref="_volumeHoverSubscribed"/>.
    /// </summary>
    private void SetVolumeHoverEnabled(bool enabled)
    {
        if (enabled == _volumeHoverSubscribed) return;

        if (enabled)
        {
            VolumeButton.PointerEntered += OnVolumeButtonEntered;
            VolumeButton.PointerExited += OnVolumeButtonExited;
        }
        else
        {
            VolumeButton.PointerEntered -= OnVolumeButtonEntered;
            VolumeButton.PointerExited -= OnVolumeButtonExited;
        }

        _volumeHoverSubscribed = enabled;
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
        if (_isSuspended) return;

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
                RefreshAllVisuals();
        }
    }

    private void OnVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is nameof(Bounds) or nameof(Height))
        {
            UpdateVolumeVisual();
            UpdateVolumePopupOffset();
        }
    }

    private void OnVolumePopupOpened(object? sender, EventArgs e)
    {
        if (_currentViewModel != null)
        {
            if (VolumeSliderPanel.Height <= 0 || double.IsNaN(VolumeSliderPanel.Height))
                UpdateVolumeSliderHeight(_currentViewModel.MaxVolume);
            UpdateVolumeVisual();
            UpdateVolumePopupOffset();
        }
    }

    private void OnVolumePopupClosed(object? sender, EventArgs e)
    {
        _volumeTooltipHideDisposable.Disposable = null;
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
        double opacity = vm.IsFullyBuffered ? 0.6 : 0.45;

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
            segment.Opacity = opacity;
        }

        for (int i = ranges.Count; i < _bufferSegments.Count; i++)
            _bufferSegments[i].IsVisible = false;
    }

    private void EnsureBufferSegmentCount(int needed)
    {
        var brush = GetBufferBrush();

        while (_bufferSegments.Count < needed)
        {
            var segment = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
                Opacity = 0.45,
                Background = brush
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

    #region Seek Visual Helpers

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

    #region Seek Hint Tooltip (Rx-based)

    private void ShowSeekHint(string text, int autoHideMs)
    {
        SeekHintText.Text = text;
        SeekHintPopup.IsOpen = true;

        _seekHintDisposable.Disposable = Observable.Timer(TimeSpan.FromMilliseconds(autoHideMs))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => SeekHintPopup.IsOpen = false);
    }

    private void HideSeekHint()
    {
        SeekHintPopup.IsOpen = false;
        _seekHintDisposable.Disposable = null;
    }

    #endregion

    #region Volume Visual

    private static double ComputeVolumeSliderHeight(int maxVolume)
    {
        if (maxVolume <= 0) maxVolume = 100;

        const double baseHeight = 80.0;
        double extra = maxVolume > 100
            ? 40.0 * Math.Log2(1 + (maxVolume - 100) / 200.0)
            : 0;

        return Math.Clamp(baseHeight + extra, VolumeSliderMinHeight, VolumeSliderMaxHeight);
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
        if (_currentViewModel == null) return;

        double height = VolumeSliderPanel.Height;
        int maxVolume = _currentViewModel.MaxVolume;

        if (height <= 0 || double.IsNaN(height))
        {
            height = ComputeVolumeSliderHeight(maxVolume);
            VolumeSliderPanel.Height = height;
        }

        if (maxVolume <= 0) maxVolume = 100;

        double ratio = Math.Clamp((double)_currentViewModel.Volume / maxVolume, 0, 1);
        ApplyVolumeVisual(ratio, height);
    }

    /// <summary>
    /// Единый метод для применения визуальных изменений Volume slider.
    /// Используется из UpdateVolumeVisual, InitializeVolumeSlider, OnVolumeAreaMoved, OnVolumeAreaPressed.
    /// </summary>
    private void ApplyVolumeVisual(double ratio, double height)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) ratio = 0;
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            height = VolumeSliderMinHeight;

        ratio = Math.Clamp(ratio, 0, 1);

        double barHeight = height * ratio;
        if (double.IsNaN(barHeight) || barHeight < 0) barHeight = 0;

        VolumeBar.Height = barHeight;

        double thumbTop = Math.Max(0, height * (1 - ratio) - VolumeThumbRadius);
        if (double.IsNaN(thumbTop)) thumbTop = 0;

        VolumeThumb.Margin = new Thickness(0, thumbTop, 0, 0);
    }

    private void ShowVolumeTooltip(int currentVolume, int maxVolume, double ratio)
    {
        VolumeTooltipText.Text = $"{currentVolume}% / {maxVolume}%";

        double height = VolumeSliderPanel.Height;
        if (height <= 0) height = VolumeSliderMinHeight;

        double thumbY = height * (1 - ratio);
        VolumeTooltipPopup.VerticalOffset = thumbY - (height / 2.0);

        VolumeTooltipPopup.IsOpen = true;

        // Автоскрытие через Rx (SerialDisposable отменяет предыдущий таймер)
        _volumeTooltipHideDisposable.Disposable = Observable.Timer(TimeSpan.FromMilliseconds(VolumeTooltipHideDelayMs))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => VolumeTooltipPopup.IsOpen = false);
    }

    #endregion

    #region Popup Helpers

    private void CloseAllPopups()
    {
        SeekTooltipPopup.IsOpen = false;
        SeekHintPopup.IsOpen = false;
        VolumeTooltipPopup.IsOpen = false;
        VolumePopup.IsOpen = false;

        _volumePopupCloseDisposable.Disposable = null;
        _volumeTooltipHideDisposable.Disposable = null;
        _seekHintDisposable.Disposable = null;
    }

    private void ShowSeekPreview() => PreviewFill.Classes.Add("active");

    private void HideSeekPreview()
    {
        PreviewFill.Classes.Remove("active");
        PreviewFill.Width = 0;
    }

    #endregion

    #region Volume Popup Hover

    /// <summary>
    /// При наведении на кнопку громкости:
    /// 1. Если suspended — запрашиваем Resume через ViewModel
    /// 2. Открываем Volume popup
    /// </summary>
    private void OnVolumeButtonEntered(object? sender, PointerEventArgs e)
    {
        _isVolumeButtonHovered = true;
        _volumePopupCloseDisposable.Disposable = null; // cancel pending close

        if (_isSuspended)
        {
            _currentViewModel?.RequestResumeIfSuspended();
            return;
        }

        if (_currentViewModel != null)
        {
            try
            {
                if (VolumeSliderPanel.Height <= 0 || double.IsNaN(VolumeSliderPanel.Height))
                    InitializeVolumeSlider(_currentViewModel);

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
        _volumePopupCloseDisposable.Disposable = null; // cancel pending close
    }

    private void OnVolumePopupContentExited(object? sender, PointerEventArgs e)
    {
        _isVolumePopupHovered = false;
        if (!_isDraggingVolume)
            TryScheduleVolumePopupClose();
    }

    /// <summary>
    /// Планирует закрытие Volume popup через Rx timer (SerialDisposable).
    /// Предыдущий таймер автоматически отменяется.
    /// </summary>
    private void TryScheduleVolumePopupClose()
    {
        if (_isDraggingVolume || _isVolumePopupHovered || _isVolumeButtonHovered)
            return;

        _volumePopupCloseDisposable.Disposable = Observable.Timer(TimeSpan.FromMilliseconds(VolumePopupCloseDelayMs))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!_isVolumePopupHovered && !_isVolumeButtonHovered && !_isDraggingVolume)
                    VolumePopup.IsOpen = false;
            });
    }

    #endregion

    #region Seek Slider

    private void OnSeekAreaMoved(object? sender, PointerEventArgs e)
    {
        if (_currentViewModel == null || _currentViewModel.DurationSeconds <= 0) return;

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
        double seconds = ratio * _currentViewModel.DurationSeconds;

        UpdateSeekCursor(x);
        UpdateSeekTooltip(x, seconds);

        if (_isDraggingSeek)
        {
            _seekDragRatio = ratio;
            ShowSeekPreview();
            UpdateSeekPreview(x);
            SeekTooltipPopup.IsOpen = true;
            _currentViewModel.UpdateSeekPosition(seconds);
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
        if (_currentViewModel == null || !_currentViewModel.HasTrack) return;

        var point = e.GetCurrentPoint(SeekContainer);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelSeekDrag();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingSeek = true;
        e.Pointer.Capture(SeekHitBox);
        _currentViewModel.StartSeek();
        SeekContainer.Classes.Add("dragging");

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        if (width <= 0) return;

        double x = Math.Clamp(point.Position.X, 0, width);
        double ratio = x / width;
        _seekDragRatio = ratio;

        ShowSeekPreview();
        UpdateSeekPreview(x);
        UpdateSeekCursor(x);
        UpdateSeekTooltip(x, ratio * _currentViewModel.DurationSeconds);
        SeekTooltipPopup.IsOpen = true;

        string cancelHint = _currentViewModel.L.Get("Seek_CancelHint", "ESC or Right Click to cancel");
        ShowSeekHint(cancelHint, SeekHintAutoHideMs);
    }

    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek) return;

        if (_currentViewModel != null)
        {
            double targetSeconds = _seekDragRatio * _currentViewModel.DurationSeconds;
            _currentViewModel.UpdateSeekPosition(targetSeconds);
            _currentViewModel.EndSeek();
        }

        HideSeekHint();
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

            if (_currentViewModel != null)
            {
                _currentViewModel.CancelSeek();
                string cancelledText = _currentViewModel.L.Get("Seek_Cancelled", "Seek cancelled");
                ShowSeekHint(cancelledText, SeekCancelledHintMs);
            }
        }

        SeekTooltipPopup.IsOpen = false;
        HideSeekPreview();
        UpdateSeekAndGlow();
    }

    #endregion

    #region Volume Slider

    /// <summary>
    /// Scroll на кнопке/ползунке громкости.
    /// </summary>
    private void OnVolumeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (_currentViewModel == null) return;

        int step = _currentViewModel.GetVolumeScrollStep();
        int delta = e.Delta.Y > 0 ? step : -step;
        int newVolume = Math.Clamp(_currentViewModel.Volume + delta, 0, _currentViewModel.MaxVolume);

        if (newVolume != _currentViewModel.Volume)
        {
            _currentViewModel.Volume = newVolume;
            _currentViewModel.OnVolumeChangeComplete();
        }

        double ratio = Math.Clamp((double)newVolume / _currentViewModel.MaxVolume, 0, 1);
        ShowVolumeTooltip(newVolume, _currentViewModel.MaxVolume, ratio);

        e.Handled = true;
    }

    private void OnVolumeAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border hitBox) return;
        if (_currentViewModel == null) return;

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
        int volumePercent = (int)(ratio * _currentViewModel.MaxVolume);

        if (_isDraggingVolume)
        {
            ApplyVolumeVisual(ratio, height);
            _currentViewModel.Volume = volumePercent;
            ShowVolumeTooltip(volumePercent, _currentViewModel.MaxVolume, ratio);
        }
        else if (hitBox.IsPointerOver)
        {
            ShowVolumeTooltip(volumePercent, _currentViewModel.MaxVolume, ratio);
        }
    }

    private void OnVolumeAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox) return;
        if (_currentViewModel == null) return;

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
        VolumeBar.Classes.Add("dragging");

        double height = VolumeSliderPanel.Height;
        if (height <= 0) return;

        double y = Math.Clamp(point.Position.Y, 0, height);
        double ratio = 1 - (y / height);
        int newVolume = (int)(ratio * _currentViewModel.MaxVolume);

        ApplyVolumeVisual(ratio, height);
        _currentViewModel.Volume = newVolume;
        ShowVolumeTooltip(newVolume, _currentViewModel.MaxVolume, ratio);
    }

    private void OnVolumeAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingVolume) return;

        _currentViewModel?.OnVolumeChangeComplete();

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
        VolumeBar.Classes.Remove("dragging");
    }

    private void CancelVolumeDrag()
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeThumb.Classes.Remove("dragging");
            VolumeBar.Classes.Remove("dragging");
        }

        _volumeTooltipHideDisposable.Disposable = null;
        VolumeTooltipPopup.IsOpen = false;
        UpdateVolumeVisual();
    }

    #endregion

    #region Keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            bool hadSeek = _isDraggingSeek;
            bool hadVolume = _isDraggingVolume;

            CancelSeekDrag();
            CancelVolumeDrag();

            if (hadSeek || hadVolume)
                e.Handled = true;
        }
    }

    #endregion
}