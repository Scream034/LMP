using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using ReactiveUI;

namespace LMP.Features.Player;

/// <summary>
/// Code-behind для нижней панели плеера.
///
/// <para><b>Seek progress — direct engine read, zero prediction:</b>
/// RAF callback вызывает <see cref="PlayerBarViewModel.ReadCurrentPositionSeconds"/>
/// каждый кадр (144fps на 144Hz). Метод читает <c>AudioEngine.CurrentPosition</c>
/// напрямую, минуя Rx pipeline (Throttle → DistinctUntilChanged → ObserveOn).
/// Никакого dead reckoning, никакого EMA — движок знает точную позицию.</para>
///
/// <para><b>TranslateTransform вместо Width/ScaleX:</b>
/// ProgressBar имеет <c>HorizontalAlignment="Stretch"</c> (полная ширина контейнера).
/// <c>TranslateTransform.X = -(width * (1 - ratio))</c> сдвигает его влево.
/// <c>ClipToBounds</c> на TrackContainer скрывает overflow.
/// Не инвалидирует layout, не масштабирует CornerRadius.</para>
///
/// <para><b>Auto-stop RAF:</b> loop работает пока трек играет.
/// При паузе — рендерит последний кадр и останавливается.
/// При resume — PropertyChanged("IsPlaying") перезапускает loop.</para>
///
/// <para><b>Spark — AXAML Animation:</b>
/// При <c>IsVisible=false</c> Avalonia полностью останавливает анимацию — 0 CPU.</para>
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

    // ── RAF state (minimal — no prediction, no smoothing) ──

    /// <summary>
    /// Флаг активности RAF-loop.
    /// </summary>
    private bool _isRafRunning;

    /// <summary>
    /// Кэш TopLevel для вызова RequestAnimationFrame.
    /// </summary>
    private TopLevel? _topLevel;

    // ── Cached RenderTransform references ──

    /// <summary>Кэш TranslateTransform для ProgressBar — избегает cast на горячем пути.</summary>
    private TranslateTransform? _progressBarTranslate;

    /// <summary>Кэш TranslateTransform для PlayingGlow.</summary>
    private TranslateTransform? _playingGlowTranslate;

    /// <summary>Кэш TranslateTransform для SeekThumb.</summary>
    private TranslateTransform? _seekThumbTranslate;

    /// <summary>Кэш TranslateTransform для SeekCursor.</summary>
    private TranslateTransform? _seekCursorTranslate;

    /// <summary>
    /// Последнее значение позиции прочитанное из AudioEngine.
    /// Используется для детектирования реального тика движка (≈200ms).
    /// Инициализируется в -1 чтобы первый кадр всегда выставил anchor.
    /// </summary>
    private double _lastEnginePosition = -1.0;

    /// <summary>
    /// Позиция в момент последнего реального тика движка.
    /// Dead reckoning интерполирует вперёд от этой точки.
    /// </summary>
    private double _anchorPosition;

    /// <summary>
    /// Frame time (от RAF callback) в момент последнего реального тика движка.
    /// Разность (currentFrameTime - anchorFrameTime) даёт elapsed для dead reckoning.
    /// Инициализация в RAF callback (не в конструкторе) — frameTime гарантированно валиден.
    /// </summary>
    private TimeSpan _anchorFrameTime;

    #endregion

    public PlayerBarView()
    {
        InitializeComponent();
        CacheRenderTransforms();
        SetupEventHandlers();
    }

    #region Initialization

    /// <summary>
    /// Кэширует ссылки на RenderTransform объекты, назначенные в AXAML.
    /// Вызывается один раз в конструкторе — горячий путь RAF-loop
    /// использует прямые ссылки без приведений и property lookup.
    /// </summary>
    private void CacheRenderTransforms()
    {
        _progressBarTranslate = (TranslateTransform)ProgressBar.RenderTransform!;
        _playingGlowTranslate = (TranslateTransform)PlayingGlow.RenderTransform!;
        _seekThumbTranslate = (TranslateTransform)SeekThumb.RenderTransform!;
        _seekCursorTranslate = (TranslateTransform)SeekCursor.RenderTransform!;
    }

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
            // IsPlaying: запускаем RAF при play, при pause loop сам остановится
            case nameof(PlayerBarViewModel.IsPlaying):
                if (vm.IsPlaying && !_isDraggingSeek)
                    EnsureRafRunning();
                break;

            // PositionSeconds: запускаем RAF для отрисовки после seek/position update
            case nameof(PlayerBarViewModel.PositionSeconds):
            case nameof(PlayerBarViewModel.DurationSeconds):
                if (!_isDraggingSeek)
                    EnsureRafRunning();
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
    /// Сброс слайдера при смене трека.
    /// TranslateTransform.X = -10000 прячет ProgressBar за пределы clip area.
    /// </summary>
    private void ApplySliderReset()
    {
        StopRaf(); // StopRaf уже сбрасывает _lastEnginePosition = -1.0

        ProgressBar.Classes.Add("hidden");
        _progressBarTranslate!.X = -10000;
        _playingGlowTranslate!.X = -10000;
        SeekThumb.Classes.Add("hidden");
        SeekCursor.Classes.Add("hidden");
        _seekThumbTranslate!.X = 0;
        _seekCursorTranslate!.X = 0;
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
            // Немедленный snapshot для первого кадра
            ApplySeekFromEngine(vm);
            EnsureRafRunning();
        }

        UpdateBufferVisual();
        UpdateVolumeVisual();
    }

    /// <summary>
    /// Немедленный snapshot из движка. Сбрасывает anchor чтобы
    /// следующий RAF кадр начал интерполяцию с актуальной точки.
    /// </summary>
    private void ApplySeekFromEngine(PlayerBarViewModel vm)
    {
        if (vm.IsTrackResetting) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;
        if (width <= 0 || duration <= 0) return;

        double position = vm.ReadCurrentPositionSeconds();
        _anchorPosition = position;
        _anchorFrameTime = TimeSpan.Zero; // сбрасываем — первый RAF кадр переустановит
        _lastEnginePosition = -1.0;       // форсируем anchor update на следующем кадре

        double ratio = Math.Clamp(position / duration, 0, 1);
        ApplySeekVisual(ratio, width);
    }

    /// <summary>
    /// Применяет вычисленную позицию ко всем seek-элементам за один вызов.
    ///
    /// <remarks>
    /// <para><b>TranslateTransform.X на ProgressBar/Glow:</b>
    /// ProgressBar имеет полную ширину контейнера (HorizontalAlignment=Stretch).
    /// X = -(width * (1 - ratio)) сдвигает его влево.
    /// ClipToBounds на TrackContainer скрывает невидимую часть.
    /// CornerRadius не масштабируется (в отличие от ScaleX).</para>
    ///
    /// <para><b>Sub-pixel rendering:</b> координаты НЕ округляются —
    /// GPU anti-aliasing обеспечивает плавный sub-pixel переход.</para>
    ///
    /// <para><b>Zero layout invalidation:</b> все операции post-layout
    /// (TranslateTransform.X). На 144Hz — стабильный frame time.</para>
    /// </remarks>
    /// </summary>
    /// <param name="ratio">Позиция 0..1 (position / duration).</param>
    /// <param name="width">Ширина seek контейнера в пикселях.</param>
    private void ApplySeekVisual(double ratio, double width)
    {
        // ProgressBar: сдвигаем влево на невидимую часть
        _progressBarTranslate!.X = -(width * (1.0 - ratio));

        // Glow: минимум ~20px видимой ширины для заметного эффекта
        double glowFill = Math.Max(20.0, width * ratio);
        _playingGlowTranslate!.X = -(width - glowFill);

        // Thumb и Cursor: sub-pixel position через TranslateTransform
        double position = width * ratio;
        _seekThumbTranslate!.X = position - SeekThumbRadius;
        _seekCursorTranslate!.X = position - SeekCursorHalfWidth;
    }

    #endregion

    #region Seek RAF Loop

    /// <summary>
    /// Запускает RAF-loop если не запущен.
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
    /// Останавливает RAF-loop.
    /// </summary>
    private void StopRaf()
    {
        _isRafRunning = false;
        _lastEnginePosition = -1.0;
    }

    /// <summary>
    /// Per-frame callback, синхронизированный с render-циклом Avalonia.
    ///
    /// <para><b>Алгоритм (engine-anchored dead reckoning):</b></para>
    /// <para>Каждый кадр читаем реальную позицию из AudioEngine (через VM).
    /// AudioPlayer обновляет её раз в ~200ms (PositionUpdateInterval).
    /// Если позиция изменилась — это реальный тик: ставим anchor = newPos, anchorTime = frameTime.
    /// Если не изменилась — интерполируем линейно вперёд от anchor.</para>
    ///
    /// <para><b>Почему не EMA и не чистое чтение:</b>
    /// Чистое чтение: одно значение на 28 кадров → видимые прыжки каждые 200ms.
    /// EMA: сглаживает backward jumps но вносит переменную скорость (то быстрее, то медленнее).
    /// Dead reckoning от реального тика: anchor всегда точен, интерполяция всегда 1.0x.</para>
    ///
    /// <para><b>Защита от спайков:</b> dt clamp в 250ms защищает от огромного прыжка
    /// после alt-tab / debugger pause.</para>
    /// </summary>
    private void OnAnimationFrame(TimeSpan frameTime)
    {
        if (!_isRafRunning) return;

        bool shouldContinue = ApplySeekFrame(frameTime);

        if (shouldContinue)
            _topLevel?.RequestAnimationFrame(OnAnimationFrame);
        else
            _isRafRunning = false;
    }

    /// <summary>
    /// Читает позицию из движка, обновляет anchor при реальном тике,
    /// интерполирует display position и применяет к визуальным элементам.
    /// </summary>
    /// <returns>true если нужен следующий кадр.</returns>
    private bool ApplySeekFrame(TimeSpan frameTime)
    {
        if (_currentViewModel is not { } vm) return false;
        if (vm.IsTrackResetting) return false;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;
        if (width <= 0 || duration <= 0) return false;

        // Читаем реальную позицию из AudioEngine
        double enginePosition = vm.ReadCurrentPositionSeconds();

        // Детектируем реальный тик движка: позиция изменилась (порог 1ms)
        // или первый кадр (_lastEnginePosition == -1)
        if (Math.Abs(enginePosition - _lastEnginePosition) > 0.001)
        {
            _anchorPosition = enginePosition;
            _anchorFrameTime = frameTime;
            _lastEnginePosition = enginePosition;
        }

        // Dead reckoning: интерполируем вперёд от последнего реального тика
        double elapsed = (frameTime - _anchorFrameTime).TotalSeconds;

        // Clamp delta: защита от огромного прыжка после alt-tab / debugger
        if (elapsed > 0.25) elapsed = 0.25;

        double rate = vm.IsPlaying ? 1.0 : 0.0;
        double displayPosition = Math.Clamp(_anchorPosition + elapsed * rate, 0, duration);

        double ratio = displayPosition / duration;
        ApplySeekVisual(ratio, width);

        return vm.IsPlaying;
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

                // Мгновенный snapshot из движка после отмены
                ApplySeekFromEngine(vm);
            }
        }

        SeekTooltipPopup.IsOpen = false;
        HideSeekPreview();
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