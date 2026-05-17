using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ReactiveUI;

namespace LMP.Features.Player;

/// <summary>
/// Code-behind для нижней панели плеера.
///
/// <para><b>Seek progress — ScaleTransform вместо Width:</b>
/// <c>ScaleTransform.ScaleX</c> применяется после layout-прохода и не вызывает
/// пересчёт соседних элементов. <c>DoubleTransition</c> на ScaleX работает через
/// Avalonia styling layer и интерполирует плавно каждые 50ms (PositionUpdateThrottle).
/// При drag транзишн отключается классом "dragging" → мгновенный отклик.</para>
///
/// <para><b>Spark — AXAML Animation вместо DispatcherTimer:</b>
/// AXAML <c>Animation</c> с <c>TranslateTransform.X</c> синхронизирована с render loop.
/// При <c>IsVisible=false</c> Avalonia полностью останавливает анимацию — 0 CPU.</para>
///
/// <para><b>Suspend/Resume архитектура:</b>
/// При Suspend: spark скрывается, PlayingGlow получает класс "suspended".
/// При Resume: состояние восстанавливается через <see cref="RefreshAllVisuals"/>.</para>
///
/// <para><b>Volume popup:</b> hover-подписки управляются через единый
/// <see cref="SetVolumeHoverEnabled"/> с флагом <see cref="_volumeHoverSubscribed"/>
/// для предотвращения двойной подписки.</para>
///
/// <para><b>Buffer segments:</b> рисуются через <see cref="Canvas"/> с переиспользованием
/// <see cref="Border"/> объектов (pool pattern) — без аллокаций на каждый render.</para>
/// </summary>
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
    private double _cachedSeekWidth;

    private readonly List<Border> _bufferSegments = [];
    private IBrush? _bufferBrushCache;

    private readonly SerialDisposable _volumePopupCloseDisposable = new();
    private readonly SerialDisposable _volumeTooltipHideDisposable = new();
    private readonly SerialDisposable _seekHintDisposable = new();

    private FlyoutBase? _formatFlyout;
    private PlayerBarViewModel? _currentViewModel;

    private bool _volumeHoverSubscribed;

    /// <summary>
    /// Якорная позиция в секундах на момент последнего PropertyChanged от VM.
    /// RAF-loop интерполирует от этой точки вперёд по clock.
    /// </summary>
    private double _anchorPositionSeconds;

    /// <summary>
    /// Время <see cref="_rafClock"/> в момент установки <see cref="_anchorPositionSeconds"/>.
    /// Delta между текущим кадром и этим значением даёт elapsed для интерполяции.
    /// </summary>
    private TimeSpan _anchorFrameTime;

    /// <summary>
    /// Флаг активности RAF-loop. False → loop не запрашивает следующий кадр.
    /// Используется как zero-cost stop: не требует отмены, просто игнорирует следующий callback.
    /// </summary>
    private bool _isRafRunning;

    /// <summary>
    /// Кэш TopLevel для вызова RequestAnimationFrame.
    /// Заполняется при первом запуске RAF и при OnAttachedToVisualTree.
    /// </summary>
    private TopLevel? _topLevel;

    /// <summary>
    /// Единые часы для якорной схемы интерполяции.
    /// Работают непрерывно с момента создания View — не нужен reset при каждом старте RAF.
    /// PropertyChanged (UI thread) и RAF-callback (UI thread) читают один Stopwatch — thread-safe.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _rafClock = System.Diagnostics.Stopwatch.StartNew();

    #endregion

    public PlayerBarView()
    {
        InitializeComponent();
        SetupEventHandlers();
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

    #endregion

    #region Lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);

        if (VisualRoot is Window window)
        {
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
        }

        SetVolumeHoverEnabled(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        StopRaf();
        _topLevel = null;

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

        UnsubscribeFromViewModel();
        ClearBufferSegments();

        _volumePopupCloseDisposable.Dispose();
        _volumeTooltipHideDisposable.Dispose();
        _seekHintDisposable.Dispose();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        SetVolumeHoverEnabled(true);
        if (!_isSuspended) RefreshAllVisuals();
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
            InvalidateBufferBrushCache();
            UpdateBufferVisual();
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_currentViewModel is null) return;
        _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _currentViewModel.SuspendRequested -= OnSuspend;
        _currentViewModel.ResumeRequested -= OnResume;
        _currentViewModel = null;
    }

    private void OnViewModelPropertyChanged(
        object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PlayerBarViewModel vm) return;

        switch (e.PropertyName)
        {
            // IsLoading и IsTrackResetting обрабатываем всегда — они управляют
            // состоянием Spark (IsVisible) и слайдера. Spark — AXAML анимация,
            // отключается через IsVisible=false без участия DispatcherTimer.
            case nameof(PlayerBarViewModel.IsLoading):
                SparkContainer.IsVisible = vm.IsLoading && !vm.IsTrackResetting;
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
                {
                    _anchorPositionSeconds = vm.PositionSeconds;
                    _anchorFrameTime = _rafClock.Elapsed;
                    EnsureRafRunning();
                }
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

    /// <summary>
    /// Сброс слайдера при смене трека: ScaleX=0 вместо Width=0 — zero layout pass.
    /// Spark управляется через IsVisible — AXAML анимация сама останавливается.
    /// </summary>
    private void ApplySliderReset()
    {
        ProgressBar.Classes.Add("hidden");
        ProgressBar.Width = 0;
        PlayingGlow.Width = 0;
        SeekThumb.Classes.Add("hidden");
        SeekCursor.Classes.Add("hidden");
        HideAllBufferSegments();
        SparkContainer.IsVisible = true;
    }

    private void RemoveSliderReset()
    {
        ProgressBar.Classes.Remove("hidden");
        SeekThumb.Classes.Remove("hidden");
        SeekCursor.Classes.Remove("hidden");

        SparkContainer.IsVisible = _currentViewModel?.IsLoading ?? false;

        if (!_isSuspended) RefreshAllVisuals();
    }

    public void OnSuspend()
    {
        _isSuspended = true;
        StopRaf();
        SparkContainer.IsVisible = false;
        PlayingGlow.Classes.Add("suspended");
        CloseAllPopups();
    }

    public void OnResume()
    {
        _isSuspended = false;
        PlayingGlow.Classes.Remove("suspended");
        SparkContainer.IsVisible = _currentViewModel?.IsLoading ?? false;
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

    #region Shuffle Button

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

    #endregion

    #region Unified Visual Updates

    /// <summary>
    /// Обновляет все визуальные элементы разом.
    /// Вызывается при Resume, WindowActivated, RemoveSliderReset.
    /// </summary>
    private void RefreshAllVisuals()
    {
        if (_currentViewModel is { } vm)
        {
            _anchorPositionSeconds = vm.PositionSeconds;
            _anchorFrameTime = _rafClock.Elapsed;
            EnsureRafRunning();
        }

        UpdateBufferVisual();
        UpdateVolumeVisual();
    }

    /// <summary>
    /// Немедленно применяет текущую позицию VM к визуальным элементам.
    ///
    /// <para>Вызывается только в ситуациях где нужен мгновенный снимок:
    /// <see cref="RefreshAllVisuals"/>, <see cref="RemoveSliderReset"/>, <see cref="CancelSeekDrag"/>.
    /// В обычном playback позиция обновляется через RAF-loop (<see cref="OnAnimationFrame"/>),
    /// который интерполирует между событиями VM.</para>
    /// </summary>
    private void UpdateSeekAndGlow()
    {
        if (_currentViewModel is not { } vm) return;
        if (vm.IsTrackResetting) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;
        if (width <= 0 || duration <= 0) return;

        double ratio = Math.Clamp(vm.PositionSeconds / duration, 0, 1);
        double position = width * ratio;

        ProgressBar.Width = position;
        PlayingGlow.Width = Math.Max(20, position);
        Canvas.SetLeft(SeekThumb, position - SeekThumbRadius);
        Canvas.SetLeft(SeekCursor, position - SeekCursorHalfWidth);
    }

    #endregion

    #region Seek RAF Loop

    /// <summary>
    /// Обновляет anchor-точку и запускает RAF-loop если не запущен.
    /// Вызывается при каждом <see cref="PlayerBarViewModel.PositionSeconds"/> event.
    /// </summary>
    private void EnsureRafRunning()
    {
        if (_isSuspended) return;

        _topLevel ??= TopLevel.GetTopLevel(this);

        if (!_isRafRunning)
        {
            _isRafRunning = true;
            _topLevel?.RequestAnimationFrame(OnAnimationFrame);
        }
    }

    /// <summary>
    /// Останавливает RAF-loop без отмены — следующий callback просто не перепланирует себя.
    /// Zero-cost stop: не требует CancellationToken или флагов синхронизации.
    /// </summary>
    private void StopRaf() => _isRafRunning = false;

    /// <summary>
    /// Per-frame callback, синхронизированный с render-циклом Avalonia.
    ///
    /// <para><b>Anchor interpolation:</b> каждый кадр вычисляет предсказанную позицию:
    /// <c>estimated = anchorPosition + (now - anchorTime) * playbackRate</c>.
    /// Это устраняет визуальные ступени от throttle VM (≈50ms) — seek bar движется
    /// непрерывно независимо от частоты PropertyChanged.</para>
    ///
    /// <para><b>Единый кадр:</b> ProgressBar.Width, SeekThumb.Left, SeekCursor.Left
    /// обновляются в одном вызове — гарантирована синхронность всех элементов.</para>
    ///
    /// <para><b>Авто-стоп:</b> если трек на паузе — elapsed * rate = 0, позиция не дрейфует.
    /// Loop продолжает работать, но визуально ничего не меняется до следующего anchor update.</para>
    /// </summary>
    private void OnAnimationFrame(TimeSpan _)
    {
        if (!_isRafRunning) return;

        ApplySeekFrame();

        _topLevel?.RequestAnimationFrame(OnAnimationFrame);
    }

    /// <summary>
    /// Применяет интерполированную позицию к ВСЕМ визуальным элементам seek bar.
    ///
    /// <para><b>Во время drag:</b> ProgressBar, PlayingGlow, SeekThumb, SeekCursor —
    /// все показывают текущую позицию воспроизведения. Мышь управляет только
    /// PreviewFill и SeekTooltip через <see cref="OnSeekAreaMoved"/>.</para>
    ///
    /// <para><b>Пользователь видит:</b> где реально играет трек (thumb + progress),
    /// и куда он собирается перемотать (preview + tooltip).</para>
    /// </summary>
    private void ApplySeekFrame()
    {
        if (_currentViewModel is not { } vm) return;
        if (vm.IsTrackResetting) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;
        if (width <= 0 || duration <= 0) return;

        double elapsed = (_rafClock.Elapsed - _anchorFrameTime).TotalSeconds;
        double rate = vm.IsPlaying ? 1.0 : 0.0;
        double estimated = Math.Clamp(_anchorPositionSeconds + elapsed * rate, 0, duration);

        double position = width * (estimated / duration);

        ProgressBar.Width = position;
        PlayingGlow.Width = Math.Max(20, position);
        Canvas.SetLeft(SeekThumb, position - SeekThumbRadius);
        Canvas.SetLeft(SeekCursor, position - SeekCursorHalfWidth);
    }

    #endregion

    #region Volume Hover

    /// <summary>
    /// Единая точка управления hover-подписками на VolumeButton.
    /// Флаг <see cref="_volumeHoverSubscribed"/> предотвращает двойную подписку
    /// при повторном вызове OnAttachedToVisualTree (например, при навигации).
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

    #region Bounds Handlers

    private void OnSeekContainerPropertyChanged(
        object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != nameof(Bounds)) return;

        _cachedSeekWidth = SeekContainer.Bounds.Width;
        if (!_isSuspended) RefreshAllVisuals();
    }

    private void OnVolumeSliderPropertyChanged(
        object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is nameof(Bounds) or nameof(Height))
        {
            UpdateVolumeVisual();
            UpdateVolumePopupOffset();
        }
    }

    private void OnVolumePopupOpened(object? sender, EventArgs e)
    {
        if (_currentViewModel is null) return;

        if (VolumeSliderPanel.Height <= 0 || double.IsNaN(VolumeSliderPanel.Height))
            UpdateVolumeSliderHeight(_currentViewModel.MaxVolume);

        UpdateVolumeVisual();
        UpdateVolumePopupOffset();
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
        VolumePopup.HorizontalOffset = (buttonWidth - popupWidth) / 2.0;
    }

    #endregion

    #region Buffer Segments Visual

    private void UpdateBufferVisual()
    {
        if (_currentViewModel is not { } vm) return;

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

            start = Math.Clamp(double.IsFinite(start) ? start : 0, 0, 1);
            end = Math.Clamp(double.IsFinite(end) ? end : 0, start, 1);

            double left = Math.Round(start * width);
            double segWidth = Math.Round((end - start) * width);

            if (segWidth is > 0 and < MinSegmentWidthPx) segWidth = MinSegmentWidthPx;
            if (left + segWidth > width) segWidth = width - left;

            Canvas.SetLeft(segment, left);
            Canvas.SetTop(segment, 0);
            segment.Width = Math.Max(0, segWidth);
            segment.Height = 4;
            segment.Background = brush;
            segment.Opacity = opacity;
            segment.IsVisible = segWidth > 0;
        }

        for (int i = ranges.Count; i < _bufferSegments.Count; i++)
            _bufferSegments[i].IsVisible = false;
    }

    /// <summary>
    /// Пул Border-объектов для буферных сегментов.
    /// Переиспользование вместо new Border() на каждый сегмент — zero alloc на горячем пути.
    /// </summary>
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
        if (_bufferBrushCache is not null) return _bufferBrushCache;

        var app = Application.Current;
        if (app?.Resources.TryGetResource(
            "TextSecondaryBrush", app.ActualThemeVariant, out var res) == true
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

    private void UpdateSeekTooltip(double x, double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        HoverTimeText.Text = time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");

        SeekTooltipBorder.Measure(Size.Infinity);
        double tooltipWidth = SeekTooltipBorder.DesiredSize.Width;
        SeekTooltipPopup.HorizontalOffset = x - tooltipWidth / 2;
    }

    /// <summary>
    /// Перемещает preview-курсор на позицию x по горизонтали.
    /// Курсор показывает куда встанет Thumb после отпускания drag.
    /// </summary>
    private void UpdateSeekPreview(double x)
        => Canvas.SetLeft(SeekPreviewCursor, x - 1.5);

    #endregion

    #region Seek Hint (Rx-based)

    private void ShowSeekHint(string text, int autoHideMs)
    {
        SeekHintText.Text = text;
        SeekHintPopup.IsOpen = true;

        _seekHintDisposable.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(autoHideMs))
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
        if (!double.IsFinite(height)) height = VolumeSliderMinHeight;

        VolumeSliderPanel.Height = height;
        UpdateVolumePopupOffset();
    }

    private void UpdateVolumeVisual()
    {
        if (_currentViewModel is null) return;

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
    /// Единый метод применения Volume slider.
    /// Используется из <see cref="UpdateVolumeVisual"/>, <see cref="InitializeVolumeSlider"/>,
    /// <see cref="OnVolumeAreaMoved"/>, <see cref="OnVolumeAreaPressed"/>.
    /// </summary>
    private void ApplyVolumeVisual(double ratio, double height)
    {
        ratio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        height = double.IsFinite(height) && height > 0 ? height : VolumeSliderMinHeight;

        double barHeight = height * ratio;
        if (!double.IsFinite(barHeight) || barHeight < 0) barHeight = 0;

        VolumeBar.Height = barHeight;

        double thumbTop = Math.Max(0, height * (1 - ratio) - VolumeThumbRadius);
        VolumeThumb.Margin = new Thickness(0, double.IsFinite(thumbTop) ? thumbTop : 0, 0, 0);
    }

    private void ShowVolumeTooltip(int currentVolume, int maxVolume, double ratio)
    {
        VolumeTooltipText.Text = $"{currentVolume}% / {maxVolume}%";

        double height = VolumeSliderPanel.Height;
        if (height <= 0) height = VolumeSliderMinHeight;

        double thumbY = height * (1 - ratio);
        VolumeTooltipPopup.VerticalOffset = thumbY - height / 2.0;
        VolumeTooltipPopup.IsOpen = true;

        _volumeTooltipHideDisposable.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(VolumeTooltipHideDelayMs))
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

    private void ShowSeekPreview()
        => SeekPreviewCursor.Classes.Add("active");

    private void HideSeekPreview()
        => SeekPreviewCursor.Classes.Remove("active");

    #endregion

    #region Volume Popup Hover

    private void OnVolumeButtonEntered(object? sender, PointerEventArgs e)
    {
        _isVolumeButtonHovered = true;
        _volumePopupCloseDisposable.Disposable = null;

        if (_isSuspended)
        {
            _currentViewModel?.RequestResumeIfSuspended();
            return;
        }

        if (_currentViewModel is null) return;

        try
        {
            if (VolumeSliderPanel.Height <= 0 || double.IsNaN(VolumeSliderPanel.Height))
                InitializeVolumeSlider(_currentViewModel);

            VolumePopup.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerBar] Volume popup open failed: {ex.Message}");
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
        _volumePopupCloseDisposable.Disposable = null;
    }

    private void OnVolumePopupContentExited(object? sender, PointerEventArgs e)
    {
        _isVolumePopupHovered = false;
        if (!_isDraggingVolume) TryScheduleVolumePopupClose();
    }

    /// <summary>
    /// SerialDisposable гарантирует: при hover-in до истечения таймера
    /// предыдущий таймер отменяется и popup остаётся открытым.
    /// </summary>
    private void TryScheduleVolumePopupClose()
    {
        if (_isDraggingVolume || _isVolumePopupHovered || _isVolumeButtonHovered) return;

        _volumePopupCloseDisposable.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(VolumePopupCloseDelayMs))
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
        if (_currentViewModel is not { DurationSeconds: > 0 } vm) return;

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
        if (_currentViewModel is not { HasTrack: true } vm) return;

        var point = e.GetCurrentPoint(SeekContainer);

        if (point.Properties.IsRightButtonPressed) { CancelSeekDrag(); return; }
        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingSeek = true;
        SeekContainer.Classes.Add("dragging");
        e.Pointer.Capture(SeekHitBox);
        vm.StartSeek();

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        if (width <= 0) return;

        double x = Math.Clamp(point.Position.X, 0, width);
        _seekDragRatio = x / width;

        ShowSeekPreview();
        UpdateSeekPreview(x);
        UpdateSeekTooltip(x, _seekDragRatio * vm.DurationSeconds);
        SeekTooltipPopup.IsOpen = true;

        ShowSeekHint(
            vm.L.Get("Seek_CancelHint", "ESC or Right Click to cancel"),
            SeekHintAutoHideMs);
    }

    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek) return;

        if (_currentViewModel is { } vm)
        {
            double targetSeconds = _seekDragRatio * vm.DurationSeconds;
            vm.UpdateSeekPosition(targetSeconds);
            vm.EndSeek();

            _anchorPositionSeconds = targetSeconds;
            _anchorFrameTime = _rafClock.Elapsed;
        }

        HideSeekHint();
        CompleteSeekDrag(e.Pointer);
    }

    private void OnSeekAreaExited(object? sender, PointerEventArgs e)
    {
        if (_isDraggingSeek) return;

        SeekTooltipPopup.IsOpen = false;
        HideSeekPreview();
    }

    private void OnSeekAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => CancelSeekDrag();

    private void CompleteSeekDrag(IPointer pointer)
    {
        _isDraggingSeek = false;
        pointer.Capture(null);

        // Возвращаем transition — интерполяция снова активна
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

            if (_currentViewModel is { } vm)
            {
                vm.CancelSeek();
                ShowSeekHint(
                    vm.L.Get("Seek_Cancelled", "Seek cancelled"),
                    SeekCancelledHintMs);
            }
        }

        SeekTooltipPopup.IsOpen = false;
        HideSeekPreview();
        UpdateSeekAndGlow();
    }

    #endregion

    #region Volume Slider

    private void OnVolumeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (_currentViewModel is not { } vm) return;

        int step = vm.GetVolumeScrollStep();
        int delta = e.Delta.Y > 0 ? step : -step;
        int newVol = Math.Clamp(vm.Volume + delta, 0, vm.MaxVolume);

        if (newVol != vm.Volume)
        {
            vm.Volume = newVol;
            vm.OnVolumeChangeComplete();
        }

        double ratio = Math.Clamp((double)newVol / vm.MaxVolume, 0, 1);
        ShowVolumeTooltip(newVol, vm.MaxVolume, ratio);

        e.Handled = true;
    }

    private void OnVolumeAreaMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border || _currentViewModel is not { } vm) return;

        var point = e.GetCurrentPoint(VolumeSliderPanel);

        if (point.Properties.IsRightButtonPressed) { CancelVolumeDrag(); return; }

        double height = VolumeSliderPanel.Height;
        if (height <= 0) return;

        double y = Math.Clamp(point.Position.Y, 0, height);
        double ratio = 1 - y / height;
        int volPct = (int)(ratio * vm.MaxVolume);

        if (_isDraggingVolume)
        {
            ApplyVolumeVisual(ratio, height);
            vm.Volume = volPct;
            ShowVolumeTooltip(volPct, vm.MaxVolume, ratio);
        }
        else if ((sender as Border)!.IsPointerOver)
        {
            ShowVolumeTooltip(volPct, vm.MaxVolume, ratio);
        }
    }

    private void OnVolumeAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border hitBox || _currentViewModel is not { } vm) return;

        var point = e.GetCurrentPoint(VolumeSliderPanel);

        if (point.Properties.IsRightButtonPressed) { CancelVolumeDrag(); return; }
        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingVolume = true;
        e.Pointer.Capture(hitBox);
        VolumeThumb.Classes.Add("dragging");
        VolumeBar.Classes.Add("dragging");

        double height = VolumeSliderPanel.Height;
        if (height <= 0) return;

        double y = Math.Clamp(point.Position.Y, 0, height);
        double ratio = 1 - y / height;
        int newVol = (int)(ratio * vm.MaxVolume);

        ApplyVolumeVisual(ratio, height);
        vm.Volume = newVol;
        ShowVolumeTooltip(newVol, vm.MaxVolume, ratio);
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
        if (!_isDraggingVolume) VolumeTooltipPopup.IsOpen = false;
    }

    private void OnVolumeAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => CancelVolumeDrag();

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
        if (e.Key != Key.Escape) return;

        bool hadSeek = _isDraggingSeek;
        bool hadVolume = _isDraggingVolume;

        CancelSeekDrag();
        CancelVolumeDrag();

        if (hadSeek || hadVolume) e.Handled = true;
    }

    #endregion
}