using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LMP.UI.Features.Shared;

namespace LMP.UI.Controls;

public partial class TrackListControl : UserControl
{
    #region Constants

    private const string DragFormatTrackIndex = "application/x-lmp-track-index";

    private static readonly DataFormat<string> TrackIndexDataFormat =
        DataFormat.CreateStringPlatformFormat(DragFormatTrackIndex);

    /// <summary>
    /// Фиксированная высота строки трека — O(1) hit-test при drag-and-drop.
    /// </summary>
    private const double ItemHeight = 60.0;

    /// <summary>
    /// Минимальное смещение в пикселях для начала drag.
    /// </summary>
    private const double DragThreshold = 8.0;

    /// <summary>
    /// Минимальное время удержания кнопки мыши перед началом drag (мс).
    /// </summary>
    private const int DragMinHoldMs = 180;

    private const int AutoScrollMargin = 50;
    private const double AutoScrollAmount = 12.0;
    private const double NearBottomThreshold = 80.0;

    #endregion

    #region Fields

    private readonly EventHandler<string> _languageChangedHandler;

    // Drag & Drop
    private Point _dragStartPoint;
    private int _dragSourceIndex = -1;
    private bool _isDragging;
    private Control? _lastHighlightedItem;
    private long _dragPressTimestamp;

    /// <summary>
    /// Saved PointerPressedEventArgs from OnItemPointerPressed.
    /// Avalonia 12 requires PointerPressedEventArgs (not PointerEventArgs)
    /// for DoDragDropAsync — PR #20988.
    /// </summary>
    private PointerPressedEventArgs? _dragPressedArgs;

    // Scroll & Layout
    private ScrollViewer? _scrollViewer;
    private ItemsRepeater? _repeater;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollOffset;

    private SnapScrollHelper? _snapScroll;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<bool> EnableSnapScrollProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableSnapScroll), true);

    /// <summary>
    /// Включает выравнивание позиции скролла по сетке высоты трека (60px).
    /// Устраняет sub-pixel рендеринг текста и иконок, снижает нагрузку на GPU.
    /// Touchpad не затрагивается — пропорциональный scroll сохраняется.
    /// </summary>
    public bool EnableSnapScroll
    {
        get => GetValue(EnableSnapScrollProperty);
        set => SetValue(EnableSnapScrollProperty, value);
    }

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TrackListControl, IEnumerable?>(nameof(Items));

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly StyledProperty<ICommand?> LoadMoreCommandProperty =
        AvaloniaProperty.Register<TrackListControl, ICommand?>(nameof(LoadMoreCommand));

    public ICommand? LoadMoreCommand
    {
        get => GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> MoveItemCommandProperty =
        AvaloniaProperty.Register<TrackListControl, ICommand?>(nameof(MoveItemCommand));

    public ICommand? MoveItemCommand
    {
        get => GetValue(MoveItemCommandProperty);
        set => SetValue(MoveItemCommandProperty, value);
    }

    public static readonly StyledProperty<bool> EnableReorderingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableReordering), false);

    public bool EnableReordering
    {
        get => GetValue(EnableReorderingProperty);
        set => SetValue(EnableReorderingProperty, value);
    }

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoading));

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly StyledProperty<bool> IsLoadingMoreProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoadingMore));

    public bool IsLoadingMore
    {
        get => GetValue(IsLoadingMoreProperty);
        set => SetValue(IsLoadingMoreProperty, value);
    }

    public static readonly StyledProperty<bool> IsFetchingFromNetworkProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsFetchingFromNetwork));

    public bool IsFetchingFromNetwork
    {
        get => GetValue(IsFetchingFromNetworkProperty);
        set => SetValue(IsFetchingFromNetworkProperty, value);
    }

    public static readonly StyledProperty<bool> ReachedEndProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(ReachedEnd));

    public bool ReachedEnd
    {
        get => GetValue(ReachedEndProperty);
        set => SetValue(ReachedEndProperty, value);
    }

    public static readonly StyledProperty<bool> UseSearchLoaderProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(UseSearchLoader), false);

    public bool UseSearchLoader
    {
        get => GetValue(UseSearchLoaderProperty);
        set => SetValue(UseSearchLoaderProperty, value);
    }

    public static readonly StyledProperty<bool> IsPlaylistContextProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsPlaylistContext), false);

    public bool IsPlaylistContext
    {
        get => GetValue(IsPlaylistContextProperty);
        set => SetValue(IsPlaylistContextProperty, value);
    }

    public static readonly StyledProperty<bool> IsQueueContextProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsQueueContext), false);

    public bool IsQueueContext
    {
        get => GetValue(IsQueueContextProperty);
        set => SetValue(IsQueueContextProperty, value);
    }

    #endregion

    #region Direct Properties

    public static readonly DirectProperty<TrackListControl, string> SearchingTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(SearchingText), static o => o.SearchingText, static (o, v) => o.SearchingText = v);

    public string SearchingText
    {
        get;
        private set => SetAndRaise(SearchingTextProperty, ref field, value);
    } = "Searching...";

    public static readonly DirectProperty<TrackListControl, string> LoadingMoreTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(LoadingMoreText), static o => o.LoadingMoreText, static (o, v) => o.LoadingMoreText = v);

    public string LoadingMoreText
    {
        get;
        private set => SetAndRaise(LoadingMoreTextProperty, ref field, value);
    } = "Searching for more";

    public static readonly DirectProperty<TrackListControl, string> EndOfListTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(EndOfListText), static o => o.EndOfListText, static (o, v) => o.EndOfListText = v);

    public string EndOfListText
    {
        get;
        private set => SetAndRaise(EndOfListTextProperty, ref field, value);
    } = "End of list";

    public static readonly DirectProperty<TrackListControl, bool> IsFooterVisibleProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, bool>(
            nameof(IsFooterVisible), static o => o.IsFooterVisible);

    public bool IsFooterVisible
    {
        get;
        private set => SetAndRaise(IsFooterVisibleProperty, ref field, value);
    }

    #endregion

    #region Constructor

    public TrackListControl()
    {
        InitializeComponent();

        _languageChangedHandler = (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                UpdateLocalizedTexts();
            else
                Dispatcher.UIThread.Post(UpdateLocalizedTexts);
        };

        UpdateLocalizedTexts();
    }

    #endregion

    #region Lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
        UpdateLocalizedTexts();

        _repeater = this.FindControl<ItemsRepeater>("MainRepeater");

        Dispatcher.UIThread.Post(() =>
        {
            _scrollViewer = this.FindAncestorOfType<ScrollViewer>();

            if (_scrollViewer != null && EnableSnapScroll)
            {
                _snapScroll?.Dispose();
                _snapScroll = new SnapScrollHelper(_scrollViewer, ItemHeight);
            }
        }, DispatcherPriority.Loaded);

        _autoScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnAutoScrollTick);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        base.OnDetachedFromVisualTree(e);

        _autoScrollTimer?.Stop();
        _autoScrollTimer = null;
        _snapScroll?.Dispose();
        _snapScroll = null;
        _repeater = null;
        _scrollViewer = null;
        _lastHighlightedItem = null;
    }

    #endregion

    #region Property Changed

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            UpdateItemsContext();
            Dispatcher.UIThread.Post(CheckInitialLoad, DispatcherPriority.Background);
        }
        else if (change.Property == IsPlaylistContextProperty ||
                 change.Property == IsQueueContextProperty)
        {
            UpdateItemsContext();
        }
        else if (change.Property == ReachedEndProperty)
        {
            if (_scrollViewer != null)
                UpdateFooterVisibility(_scrollViewer.Offset);
            else
                IsFooterVisible = ReachedEnd;
        }
        else if (change.Property == EnableSnapScrollProperty)
        {
            if (_scrollViewer == null) return;
            _snapScroll?.Dispose();
            _snapScroll = change.GetNewValue<bool>()
                ? new SnapScrollHelper(_scrollViewer, ItemHeight)
                : null;
        }
    }

    private void UpdateFooterVisibility(Vector offset)
    {
        if (_scrollViewer == null) { IsFooterVisible = false; return; }

        double distanceToBottom =
            _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - offset.Y;
        bool isNearBottom = distanceToBottom <= NearBottomThreshold
                            || _scrollViewer.Extent.Height <= _scrollViewer.Viewport.Height;
        IsFooterVisible = ReachedEnd && isNearBottom;
    }

    private void CheckInitialLoad()
    {
        if (_scrollViewer == null || LoadMoreCommand == null) return;

        if (_scrollViewer.Extent.Height <= _scrollViewer.Viewport.Height &&
            LoadMoreCommand.CanExecute(null))
        {
            LoadMoreCommand.Execute(null);
        }
    }

    #endregion

    #region Drag & Drop

    /// <summary>
    /// O(1) индекс элемента по позиции Y.
    /// Все строки фиксированной высоты <see cref="ItemHeight"/> — деление без итерации.
    /// </summary>
    private int GetItemIndexFromPosition(Point positionInRepeater)
    {
        if (_repeater?.ItemsSource is not ICollection collection) return -1;
        int index = (int)(positionInRepeater.Y / ItemHeight);
        return index >= 0 && index < collection.Count ? index : -1;
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!EnableReordering) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _dragStartPoint = e.GetPosition(null);
        _dragPressTimestamp = Environment.TickCount64;
        _isDragging = false;
        _dragPressedArgs = e;

        if (_repeater != null)
            _dragSourceIndex = GetItemIndexFromPosition(e.GetPosition(_repeater));
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!EnableReordering || _isDragging || _dragSourceIndex < 0 || _dragPressedArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(null);
        double deltaX = Math.Abs(pos.X - _dragStartPoint.X);
        double deltaY = Math.Abs(pos.Y - _dragStartPoint.Y);

        if (deltaX < DragThreshold && deltaY < DragThreshold) return;

        long heldMs = Environment.TickCount64 - _dragPressTimestamp;
        if (heldMs < DragMinHoldMs) return;

        if (sender is not Control source) return;

        _isDragging = true;
        using var dragData = new DragDataTransfer(_dragSourceIndex);
        source.Classes.Add("dragging");

        try { await DragDrop.DoDragDropAsync(_dragPressedArgs, dragData, DragDropEffects.Move); }
        finally
        {
            source.Classes.Remove("dragging");
            CleanupDragStyles();
            _isDragging = false;
            _dragSourceIndex = -1;
            _dragPressedArgs = null;
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right &&
            sender is Control ctl && ctl.DataContext is TrackItemViewModel vm)
        {
            ShowSharedFlyout(ctl, vm, showAtPointer: true);
            e.Handled = true;
            return;
        }

        _isDragging = false;
        _dragSourceIndex = -1;
        _dragPressedArgs = null;
        CleanupDragStyles();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!EnableReordering || !HasTrackIndexData(e))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        if (_repeater == null) return;

        var pos = e.GetPosition(_repeater);
        int idx = GetItemIndexFromPosition(pos);
        var overItem = idx >= 0 ? _repeater.TryGetElement(idx) : null;

        if (_lastHighlightedItem != null && _lastHighlightedItem != overItem)
        {
            _lastHighlightedItem.Classes.Remove("insert-top");
            _lastHighlightedItem.Classes.Remove("insert-bottom");
        }

        if (overItem == null) return;
        _lastHighlightedItem = overItem;

        var relY = pos.Y - _repeater.Bounds.Position.Y - (idx * ItemHeight);

        if (relY < ItemHeight / 2)
        {
            overItem.Classes.Add("insert-top");
            overItem.Classes.Remove("insert-bottom");
        }
        else
        {
            overItem.Classes.Remove("insert-top");
            overItem.Classes.Add("insert-bottom");
        }

        HandleAutoScroll(e);
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        CleanupDragStyles();
        _autoScrollTimer?.Stop();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        CleanupDragStyles();
        _autoScrollTimer?.Stop();

        if (!EnableReordering || !HasTrackIndexData(e) || _repeater == null) return;

        int oldIndex = GetTrackIndex(e);
        if (oldIndex < 0) return;

        int newIndex = CalculateDropIndex(e, oldIndex);

        if (_repeater.ItemsSource is ICollection col &&
            newIndex >= 0 && oldIndex != newIndex && newIndex < col.Count)
        {
            MoveItemCommand?.Execute((oldIndex, newIndex));
        }
    }

    private void HandleAutoScroll(DragEventArgs e)
    {
        if (_scrollViewer == null) return;
        var pos = e.GetPosition(_scrollViewer);

        if (pos.Y < AutoScrollMargin)
        { _autoScrollOffset = -AutoScrollAmount; _autoScrollTimer?.Start(); }
        else if (pos.Y > _scrollViewer.Bounds.Height - AutoScrollMargin)
        { _autoScrollOffset = AutoScrollAmount; _autoScrollTimer?.Start(); }
        else
        { _autoScrollOffset = 0; _autoScrollTimer?.Stop(); }
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_scrollViewer == null || _autoScrollOffset == 0) return;
        _scrollViewer.Offset += new Vector(0, _autoScrollOffset);
    }

    private int CalculateDropIndex(DragEventArgs e, int oldIndex)
    {
        if (_repeater == null) return -1;

        var pos = e.GetPosition(_repeater);
        int idx = GetItemIndexFromPosition(pos);

        if (idx < 0)
            return _repeater.ItemsSource is ICollection c ? c.Count - 1 : -1;

        var targetElement = _repeater.TryGetElement(idx);
        var relY = targetElement != null ? e.GetPosition(targetElement).Y : pos.Y - (idx * ItemHeight);

        int target = relY > ItemHeight / 2 ? idx + 1 : idx;
        if (oldIndex < target) target--;

        return _repeater.ItemsSource is ICollection col
            ? Math.Clamp(target, 0, col.Count - 1)
            : target;
    }

    private static bool HasTrackIndexData(DragEventArgs e)
        => e.DataTransfer is DragDataTransfer ||
           e.DataTransfer.Formats.Any(f => f.Identifier == DragFormatTrackIndex);

    private static int GetTrackIndex(DragEventArgs e)
    {
        if (e.DataTransfer is DragDataTransfer a) return a.TrackIndex;
        foreach (var item in e.DataTransfer.Items)
        {
            if (item is DragDataTransferItem d) return d.TrackIndex;
            var raw = item.TryGetRaw(TrackIndexDataFormat);
            if (raw is string s && int.TryParse(s, out int i)) return i;
            if (raw is int v) return v;
        }
        return -1;
    }

    private void CleanupDragStyles()
    {
        if (_lastHighlightedItem == null) return;
        _lastHighlightedItem.Classes.Remove("insert-top");
        _lastHighlightedItem.Classes.Remove("insert-bottom");
        _lastHighlightedItem = null;
    }

    #endregion

    #region Drag Data Types

    private sealed class DragDataTransferItem(int trackIndex) : IDataTransferItem
    {
        public int TrackIndex { get; } = trackIndex;
        public IReadOnlyList<DataFormat> Formats { get; } = [TrackIndexDataFormat];
        public object? TryGetRaw(DataFormat format)
            => format.Identifier == DragFormatTrackIndex ? TrackIndex.ToString() : null;
    }

    private sealed class DragDataTransfer(int trackIndex) : IDataTransfer
    {
        private bool _disposed;
        public int TrackIndex { get; } = trackIndex;
        public IReadOnlyList<DataFormat> Formats { get; } = [TrackIndexDataFormat];
        public IReadOnlyList<IDataTransferItem> Items { get; } = [new DragDataTransferItem(trackIndex)];
        public void Dispose() { if (_disposed) return; _disposed = true; }
    }

    #endregion

    #region Context Menu

    private void OnMoreButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not TrackItemViewModel vm) return;
        ShowSharedFlyout(c, vm, showAtPointer: false);
    }

    /// <summary>
    /// Показывает контекстное меню трека.
    /// showAtPointer=true — при ПКМ, меню под курсором.
    /// showAtPointer=false — при клике на "три точки", меню привязано к кнопке.
    /// </summary>
    private void ShowSharedFlyout(Control target, TrackItemViewModel vm, bool showAtPointer)
    {
        if (this.Resources.TryGetValue("SharedTrackMenuFlyout", out var res) && res is MenuFlyout f)
            f.ShowAt(target, showAtPointer);
    }

    private void OnMenuFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target: Control t } && t.DataContext is TrackItemViewModel vm)
            vm.IsMenuOpen = true;
    }

    private void OnMenuFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target: Control t } && t.DataContext is TrackItemViewModel vm)
            vm.IsMenuOpen = false;
    }

    #endregion

    #region Private Methods

    private void UpdateLocalizedTexts()
    {
        var L = LocalizationService.Instance;
        SearchingText = L["Search_Searching"] ?? "Searching...";
        LoadingMoreText = L["Search_LoadingMore"] ?? "Searching for more";
        EndOfListText = L["Search_EndOfList"] ?? "End of list";
    }

    /// <summary>
    /// Проставляет контекстные флаги всем VM в коллекции.
    /// Пропускает VM у которых значение уже совпадает — устраняет
    /// лавину RaisePropertyChanged при повторных вызовах с теми же данными.
    /// </summary>
    private void UpdateItemsContext()
    {
        if (Items == null) return;

        var isPlaylist = IsPlaylistContext;
        var isQueue = IsQueueContext;

        foreach (var item in Items)
        {
            if (item is not TrackItemViewModel vm) continue;
            if (vm.IsPlaylistContext != isPlaylist) vm.IsPlaylistContext = isPlaylist;
            if (vm.IsQueueContext != isQueue) vm.IsQueueContext = isQueue;
        }
    }

    #endregion
    /// <summary>
    /// Выравнивает позицию ScrollViewer по целочисленной сетке высоты трека
    /// с velocity-based ballistics и time-based ease-out cubic анимацией.
    ///
    /// <para><b>Ballistics (Enhanced Scroll Precision):</b></para>
    /// <para>Количество треков на тик зависит от скорости вращения колеса — интервала
    /// между последовательными wheel-событиями. Медленно = 1 трек (точность),
    /// быстро = до MaxTracksPerTick (навигация). Кривая степенная, не линейная —
    /// ощущение аналогично Windows Enhanced Pointer Precision.</para>
    ///
    /// <para><b>Накопление:</b> при удержании колеса target накапливается от предыдущего
    /// значения, анимация плавно "подхватывается" без рывка.</para>
    ///
    /// <para><b>Integer pixel rendering:</b> _targetOffset всегда snap-aligned (целый пиксель).
    /// Промежуточные lerp-кадры используют дробные px — TextOptions.BaselinePixelAlignment=Unaligned
    /// на ItemsRepeater компенсирует sub-pixel text rendering.</para>
    /// </summary>
    private sealed class SnapScrollHelper : IDisposable
    {
        #region Constants

        /// <summary>
        /// Порог дельты колеса для различения мыши и тачпада.
        /// Mouse wheel: |delta| ≈ 1.0–3.0. Touchpad: |delta| ≈ 0.05–0.3.
        /// </summary>
        private const double TouchpadDeltaThreshold = 0.5;

        /// <summary>
        /// Минимальное количество треков на тик — при очень медленном скролле.
        /// 1 трек = максимальная точность, ощущение контроля.
        /// </summary>
        private const double MinTracksPerTick = 1.0;

        /// <summary>
        /// Максимальное количество треков на тик — при очень быстром скролле.
        /// </summary>
        private const double MaxTracksPerTick = 6.0;

        /// <summary>
        /// Интервал между тиками (мс) при котором используется MinTracksPerTick.
        /// Больше этого значения = пользователь скроллит медленно и точно.
        /// </summary>
        private const double SlowTickIntervalMs = 300.0;

        /// <summary>
        /// Интервал между тиками (мс) при котором используется MaxTracksPerTick.
        /// Меньше этого значения = пользователь скроллит быстро.
        /// </summary>
        private const double FastTickIntervalMs = 30.0;

        /// <summary>
        /// Коэффициент степени кривой баллистики.
        /// 0.5 = √ (квадратный корень) — плавный рост, не слишком агрессивный.
        /// Аналогично формуле BetterTouchTool: output = input × (1 + strength × √(|input|/4)).
        /// </summary>
        private const double BallisticsCurveExponent = 0.5;

        /// <summary>Базовая длительность анимации (мс) для шага в 3 трека.</summary>
        private const double BaseAnimDurationMs = 130.0;

        /// <summary>Минимальная длительность анимации — для коротких шагов (1 трек).</summary>
        private const double MinAnimDurationMs = 60.0;

        /// <summary>Максимальная длительность анимации — для длинных шагов (6 треков).</summary>
        private const double MaxAnimDurationMs = 200.0;

        /// <summary>
        /// Порог расстояния до maxOffset при котором snap заменяется точным maxOffset.
        /// </summary>
        private const double BottomSnapEpsilon = 0.5;

        #endregion

        private readonly ScrollViewer _sv;
        private readonly double _itemHeight;
        private readonly DispatcherTimer _animTimer;

        private double _targetOffset;
        private double _animStartOffset;
        private long _animStartTime;
        private long _animDurationActual;

        /// <summary>Timestamp последнего wheel-события для вычисления velocity.</summary>
        private long _lastWheelTime;

        private bool _isAnimating;
        private bool _disposed;

        public SnapScrollHelper(ScrollViewer sv, double itemHeight)
        {
            _sv = sv;
            _itemHeight = itemHeight;
            _targetOffset = sv.Offset.Y;
            _lastWheelTime = Environment.TickCount64;

            sv.AddHandler(
                PointerWheelChangedEvent,
                OnWheel,
                RoutingStrategies.Tunnel,
                handledEventsToo: false);

            _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _animTimer.Tick += OnAnimTick;
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (Math.Abs(e.Delta.Y) < TouchpadDeltaThreshold) return;

            e.Handled = true;

            double maxOffset = Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);
            if (maxOffset <= 0) return;

            // ═══ BALLISTICS: velocity-based tracks per tick ═══
            long now = Environment.TickCount64;
            double intervalMs = Math.Clamp(now - _lastWheelTime, 1, SlowTickIntervalMs);
            _lastWheelTime = now;

            double tracksPerTick = ComputeTracksPerTick(intervalMs);

            double direction = Math.Sign(-e.Delta.Y);
            double step = direction * _itemHeight * tracksPerTick;

            // Накапливаем от текущего target если анимация уже идёт
            double baseOffset = _isAnimating ? _targetOffset : _sv.Offset.Y;
            double raw = Math.Clamp(baseOffset + step, 0, maxOffset);

            if (raw >= maxOffset - BottomSnapEpsilon)
                _targetOffset = maxOffset;
            else if (raw <= BottomSnapEpsilon)
                _targetOffset = 0;
            else
                _targetOffset = SnapToGrid(raw, direction);

            // Адаптивная длительность: пропорционально реальной дистанции
            double distance = Math.Abs(_targetOffset - _sv.Offset.Y);
            double referenceStep = _itemHeight * 3.0;
            _animDurationActual = (long)Math.Clamp(
                BaseAnimDurationMs * (distance / referenceStep),
                MinAnimDurationMs,
                MaxAnimDurationMs);

            StartAnim();
        }

        /// <summary>
        /// Вычисляет количество треков на тик по velocity-кривой.
        ///
        /// <para>Кривая степенная (exponent=0.5, т.е. √):
        /// быстрый рост в начале (медленный → средний scroll),
        /// плавный выход к MaxTracksPerTick при высокой скорости.
        /// Это соответствует поведению Windows Enhanced Pointer Precision.</para>
        ///
        /// <para>intervalMs → нормализуем в [0..1] где 0=быстро, 1=медленно →
        /// применяем обратную степенную кривую → интерполируем треки.</para>
        /// </summary>
        private static double ComputeTracksPerTick(double intervalMs)
        {
            // t=0 → быстро (MaxTracks), t=1 → медленно (MinTracks)
            double t = Math.Clamp(
                (intervalMs - FastTickIntervalMs) / (SlowTickIntervalMs - FastTickIntervalMs),
                0.0, 1.0);

            // Степенная кривая: √t даёт быстрый рост при малых t (высокая скорость),
            // плавный выход при больших t (низкая скорость)
            double curved = Math.Pow(t, BallisticsCurveExponent);

            return MinTracksPerTick + (MaxTracksPerTick - MinTracksPerTick) * (1.0 - curved);
        }

        /// <summary>
        /// Кадр time-based анимации с ease-out cubic.
        /// Прогресс по реальному времени — не зависит от fps.
        /// </summary>
        private void OnAnimTick(object? sender, EventArgs e)
        {
            double elapsed = (Environment.TickCount64 - _animStartTime) / (double)_animDurationActual;

            if (elapsed >= 1.0)
            {
                SetOffset(_targetOffset);
                StopAnim();
                return;
            }

            SetOffset(_animStartOffset + (_targetOffset - _animStartOffset) * EaseOutCubic(elapsed));
        }

        /// <summary>
        /// Запускает или перезапускает анимацию от текущей визуальной позиции.
        /// Перезапуск при новом wheel-событии во время анимации — плавное «подхватывание».
        /// </summary>
        private void StartAnim()
        {
            _animStartOffset = _sv.Offset.Y;
            _animStartTime = Environment.TickCount64;

            if (_isAnimating) return;

            _isAnimating = true;
            _animTimer.Start();
        }

        private void StopAnim()
        {
            _isAnimating = false;
            _animTimer.Stop();
        }

        /// <summary>Ease-out cubic: f(t) = 1 - (1-t)³</summary>
        private static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3.0);

        /// <summary>
        /// Направление-зависимый snap:
        /// вниз → Ceiling (всегда доходим до следующей границы),
        /// вверх → Floor (не перепрыгиваем назад).
        /// </summary>
        private double SnapToGrid(double offset, double direction) =>
            direction > 0
                ? Math.Ceiling(offset / _itemHeight) * _itemHeight
                : Math.Floor(offset / _itemHeight) * _itemHeight;

        private void SetOffset(double y) =>
            _sv.Offset = new Vector(_sv.Offset.X, y);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _animTimer.Stop();
            _sv.RemoveHandler(PointerWheelChangedEvent, OnWheel);
        }
    }
}