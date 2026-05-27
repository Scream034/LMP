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
    private const double ItemHeight = 62.0;

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
            Log.Debug($"[TrackList] Attached. ScrollViewer found: {_scrollViewer != null}");

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
        Log.Debug("[TrackList] Detached from visual tree.");
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

    #region Drag & Drop Helpers (Координаты с выводом отладочной информации)

    /// <summary>
    /// Безопасно вычисляет положение курсора мыши относительно начала <see cref="ItemsRepeater"/>,
    /// используя систему координат стабильного <see cref="ScrollViewer"/>. Выводит подробный лог.
    /// </summary>
    private Point GetSafePositionInRepeater(DragEventArgs e, string context)
    {
        if (_repeater == null)
        {
            Log.Debug($"[{context}] GetSafePositionInRepeater: _repeater is null!");
            return default;
        }

        var rawPos = e.GetPosition(_repeater);
        var scrollViewer = _scrollViewer ?? this.FindAncestorOfType<ScrollViewer>();

        Log.Debug($"[{context}] === Safe Position Calc Started ===");
        Log.Debug($"[{context}] Raw e.GetPosition(_repeater): X={rawPos.X:F1}, Y={rawPos.Y:F1}");

        if (scrollViewer != null)
        {
            var posInScrollViewer = e.GetPosition(scrollViewer);
            var repeaterOrigin = _repeater.TranslatePoint(new Point(0, 0), scrollViewer);
            var offset = scrollViewer.Offset;

            Log.Debug($"[{context}] ScrollViewer.Offset.Y: {offset.Y:F1}");
            Log.Debug($"[{context}] Pointer pos in ScrollViewer: X={posInScrollViewer.X:F1}, Y={posInScrollViewer.Y:F1}");

            if (repeaterOrigin.HasValue)
            {
                Log.Debug($"[{context}] _repeater origin inside ScrollViewer: X={repeaterOrigin.Value.X:F1}, Y={repeaterOrigin.Value.Y:F1}");
                var calculated = new Point(
                    posInScrollViewer.X - repeaterOrigin.Value.X,
                    posInScrollViewer.Y - repeaterOrigin.Value.Y);
                Log.Debug($"[{context}] Calculated Safe Position: X={calculated.X:F1}, Y={calculated.Y:F1} (Diff with raw Y: {calculated.Y - rawPos.Y:F1})");
                return calculated;
            }
            else
            {
                Log.Warn($"[{context}] _repeater.TranslatePoint returned NULL. Origin cannot be translated.");
            }
        }
        else
        {
            Log.Warn($"[{context}] ScrollViewer not found. Using fallback raw position.");
        }

        return rawPos;
    }

    /// <summary>
    /// Безопасно вычисляет Y-координату мыши относительно конкретного элемента списка с подробным логированием.
    /// </summary>
    private double GetSafeElementRelativeY(DragEventArgs e, Control element, Point safePosInRepeater, int idx, string context)
    {
        var scrollViewer = _scrollViewer ?? this.FindAncestorOfType<ScrollViewer>();
        Log.Debug($"[{context}] GetSafeElementRelativeY for item index={idx}");

        if (scrollViewer != null)
        {
            var posInScrollViewer = e.GetPosition(scrollViewer);
            var elementOrigin = element.TranslatePoint(new Point(0, 0), scrollViewer);
            if (elementOrigin.HasValue)
            {
                double calculatedRelY = posInScrollViewer.Y - elementOrigin.Value.Y;
                Log.Debug($"[{context}] posInScrollViewer.Y={posInScrollViewer.Y:F1}, elementOrigin.Y={elementOrigin.Value.Y:F1} -> calculatedRelY={calculatedRelY:F1}");
                return calculatedRelY;
            }
            else
            {
                Log.Warn($"[{context}] element.TranslatePoint returned NULL for item index={idx}");
            }
        }

        double fallbackRelY = safePosInRepeater.Y - (idx * ItemHeight);
        Log.Debug($"[{context}] Fallback calculation used: safePosInRepeater.Y={safePosInRepeater.Y:F1} - (idx*{ItemHeight}) -> fallbackRelY={fallbackRelY:F1}");
        return fallbackRelY;
    }

    #endregion

    #region Drag & Drop

    /// <summary>
    /// O(1) индекс элемента по позиции Y.
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
        {
            var repeaterPos = e.GetPosition(_repeater);
            _dragSourceIndex = GetItemIndexFromPosition(repeaterPos);
            Log.Debug($"[PointerPressed] Pressed at repeater: X={repeaterPos.X:F1}, Y={repeaterPos.Y:F1}. Calculated Source Index: {_dragSourceIndex}");
        }
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
        if (heldMs < DragMinHoldMs)
        {
            Log.Debug($"[PointerMoved] Drag threshold met but hold time too short ({heldMs}ms < {DragMinHoldMs}ms). Drag aborted.");
            return;
        }

        if (sender is not Control source) return;

        _isDragging = true;
        Log.Info($"[PointerMoved] Starting drag-and-drop process from source index: {_dragSourceIndex}");
        using var dragData = new DragDataTransfer(_dragSourceIndex);
        source.Classes.Add("dragging");

        try 
        { 
            var result = await DragDrop.DoDragDropAsync(_dragPressedArgs, dragData, DragDropEffects.Move); 
            Log.Info($"[PointerMoved] DoDragDropAsync completed. Result: {result}");
        }
        catch (Exception ex)
        {
            Log.Error($"[PointerMoved] Error during DoDragDropAsync: {ex.Message}");
        }
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
            Log.Debug($"[PointerReleased] Right-click detected on item: {vm.Title}");
            ShowSharedFlyout(ctl, vm, showAtPointer: true);
            e.Handled = true;
            return;
        }

        Log.Debug("[PointerReleased] Mouse button released. Resetting drag parameters.");
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

        Log.Debug("[DragOver] OnDragOver triggered.");

        // Получаем безопасные координаты с подробным логом вычислений
        var pos = GetSafePositionInRepeater(e, "DragOver");
        int idx = GetItemIndexFromPosition(pos);
        var overItem = idx >= 0 ? _repeater.TryGetElement(idx) : null;

        Log.Debug($"[DragOver] Hovered visual element index calculated: {idx}. Element found: {overItem != null}");

        if (_lastHighlightedItem != null && _lastHighlightedItem != overItem)
        {
            Log.Debug($"[DragOver] Removing drag highlights from previous item: {_lastHighlightedItem.DataContext}");
            _lastHighlightedItem.Classes.Remove("insert-top");
            _lastHighlightedItem.Classes.Remove("insert-bottom");
        }

        if (overItem == null)
        {
            Log.Debug("[DragOver] overItem is null (no row under calculated coordinate), exiting OnDragOver.");
            return;
        }
        _lastHighlightedItem = overItem;

        // Вычисляем координату внутри самой строки (0..60px)
        double relY = GetSafeElementRelativeY(e, overItem, pos, idx, "DragOver");
        Log.Debug($"[DragOver] Element relative position relY: {relY:F1}px (Row Height: {ItemHeight}px)");

        if (relY < ItemHeight / 2)
        {
            Log.Debug($"[DragOver] relY < ItemHeight/2 ({ItemHeight / 2}). Visual line: TOP.");
            overItem.Classes.Add("insert-top");
            overItem.Classes.Remove("insert-bottom");
        }
        else
        {
            Log.Debug($"[DragOver] relY >= ItemHeight/2 ({ItemHeight / 2}). Visual line: BOTTOM.");
            overItem.Classes.Remove("insert-top");
            overItem.Classes.Add("insert-bottom");
        }

        HandleAutoScroll(e);
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        Log.Debug("[DragLeave] OnDragLeave triggered. Stopping timers & cleaning styles.");
        CleanupDragStyles();
        _autoScrollTimer?.Stop();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Log.Info("[Drop] OnDrop event received.");
        CleanupDragStyles();
        _autoScrollTimer?.Stop();

        if (!EnableReordering || !HasTrackIndexData(e) || _repeater == null)
        {
            Log.Warn($"[Drop] Drop aborted. EnableReordering={EnableReordering}, HasTrackData={HasTrackIndexData(e)}, RepeaterExists={_repeater != null}");
            return;
        }

        int oldIndex = GetTrackIndex(e);
        Log.Info($"[Drop] Extracted oldIndex from drag payload: {oldIndex}");
        if (oldIndex < 0) return;

        int newIndex = CalculateDropIndex(e, oldIndex);
        Log.Info($"[Drop] Final calculated target index (newIndex): {newIndex}");

        if (_repeater.ItemsSource is ICollection col)
        {
            Log.Debug($"[Drop] Source collection count: {col.Count}");
            if (newIndex >= 0 && oldIndex != newIndex && newIndex < col.Count)
            {
                Log.Info($"[Drop] Executing MoveItemCommand: moving track from {oldIndex} to {newIndex}");
                MoveItemCommand?.Execute((oldIndex, newIndex));
            }
            else
            {
                Log.Warn($"[Drop] Move rejected. Verification failed (newIndex out of bounds or oldIndex == newIndex).");
            }
        }
        else
        {
            Log.Error("[Drop] ItemsSource is not ICollection!");
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
        if (_repeater == null)
        {
            Log.Warn("[DropIndex] _repeater is null in CalculateDropIndex!");
            return -1;
        }

        Log.Debug($"[DropIndex] Calculating target index for oldIndex: {oldIndex}");

        // Точное и безопасное вычисление координат с логом
        var pos = GetSafePositionInRepeater(e, "DropIndex");
        int idx = GetItemIndexFromPosition(pos);

        Log.Debug($"[DropIndex] Calculated raw index from safe coordinates: {idx}");

        if (idx < 0)
        {
            int fallback = _repeater.ItemsSource is ICollection c ? c.Count - 1 : -1;
            Log.Debug($"[DropIndex] Calculated index is out of bounds (<0). Falling back to bottom of list: {fallback}");
            return fallback;
        }

        var targetElement = _repeater.TryGetElement(idx);
        double relY = targetElement != null 
            ? GetSafeElementRelativeY(e, targetElement, pos, idx, "DropIndex") 
            : pos.Y - (idx * ItemHeight);

        Log.Debug($"[DropIndex] Target row element resolved: {targetElement != null}. relY: {relY:F1}px");

        int target = relY > ItemHeight / 2 ? idx + 1 : idx;
        Log.Debug($"[DropIndex] Temp target before drag source index adjustment: {target}");

        if (oldIndex < target)
        {
            target--;
            Log.Debug($"[DropIndex] oldIndex < target ({oldIndex} < {target + 1}). Adjusted target to: {target}");
        }

        int finalClamp = _repeater.ItemsSource is ICollection col
            ? Math.Clamp(target, 0, col.Count - 1)
            : target;

        Log.Debug($"[DropIndex] Target index after clamping: {finalClamp}");
        return finalClamp;
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
        Log.Debug($"[Cleanup] Resetting visual highlight from item: {_lastHighlightedItem.DataContext}");
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

    #region Smooth Snap Scroll Helper

    /// <summary>
    /// Выравнивает позицию ScrollViewer по целочисленной сетке высоты трека
    /// с velocity-based ballistics и time-based ease-out cubic анимацией.
    /// </summary>
    private sealed class SnapScrollHelper : IDisposable
    {
        #region Constants

        private const double TouchpadDeltaThreshold = 0.5;
        private const double MinTracksPerTick = 1.0;
        private const double MaxTracksPerTick = 6.0;
        private const double SlowTickIntervalMs = 300.0;
        private const double FastTickIntervalMs = 30.0;
        private const double BallisticsCurveExponent = 0.5;
        private const double BaseAnimDurationMs = 130.0;
        private const double MinAnimDurationMs = 60.0;
        private const double MaxAnimDurationMs = 200.0;
        private const double BottomSnapEpsilon = 0.5;

        #endregion

        private readonly ScrollViewer _sv;
        private readonly double _itemHeight;
        private readonly DispatcherTimer _animTimer;

        private double _targetOffset;
        private double _animStartOffset;
        private long _animStartTime;
        private long _animDurationActual;

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

            long now = Environment.TickCount64;
            double intervalMs = Math.Clamp(now - _lastWheelTime, 1, SlowTickIntervalMs);
            _lastWheelTime = now;

            double tracksPerTick = ComputeTracksPerTick(intervalMs);
            double direction = Math.Sign(-e.Delta.Y);
            double step = direction * _itemHeight * tracksPerTick;

            double baseOffset = _isAnimating ? _targetOffset : _sv.Offset.Y;
            double raw = Math.Clamp(baseOffset + step, 0, maxOffset);

            if (raw >= maxOffset - BottomSnapEpsilon)
                _targetOffset = maxOffset;
            else if (raw <= BottomSnapEpsilon)
                _targetOffset = 0;
            else
                _targetOffset = SnapToGrid(raw, direction);

            double distance = Math.Abs(_targetOffset - _sv.Offset.Y);
            double referenceStep = _itemHeight * 3.0;
            _animDurationActual = (long)Math.Clamp(
                BaseAnimDurationMs * (distance / referenceStep),
                MinAnimDurationMs,
                MaxAnimDurationMs);

            StartAnim();
        }

        private static double ComputeTracksPerTick(double intervalMs)
        {
            double t = Math.Clamp(
                (intervalMs - FastTickIntervalMs) / (SlowTickIntervalMs - FastTickIntervalMs),
                0.0, 1.0);
            double curved = Math.Pow(t, BallisticsCurveExponent);
            return MinTracksPerTick + (MaxTracksPerTick - MinTracksPerTick) * (1.0 - curved);
        }

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

        private static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3.0);

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

    #endregion
}