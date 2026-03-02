using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LMP.Core.Services;
using LMP.Features.Shared;

namespace LMP.UI.Controls;

public partial class TrackListControl : UserControl
{
    #region Constants

    private const string DragFormatTrackIndex = "application/x-lmp-track-index";
    private const double DragThreshold = 5.0;

    private static readonly DataFormat<string> TrackIndexDataFormat =
        DataFormat.CreateStringPlatformFormat(DragFormatTrackIndex);

    private const int AutoScrollMargin = 50;
    private const double AutoScrollAmount = 12.0;

    /// <summary>
    /// Если дельта скролла за кадр больше этого порога — считаем скролл быстрым.
    /// </summary>
    private const double ScrollSpeedThreshold = 2.0;

    /// <summary>
    /// Задержка после остановки скролла перед загрузкой обложек (мс).
    /// </summary>
    private const int ScrollDebounceMs = 150;

    #endregion

    #region Fields

    private readonly EventHandler<string> _languageChangedHandler;

    // Drag & Drop State
    private Point _dragStartPoint;
    private ListBox? _listBox;
    private ListBoxItem? _lastDragOverItem;
    private bool _isDragging;

    // Авто-скролл при drag
    private ScrollViewer? _scrollViewer;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollOffset;

    // Отслеживание скорости скролла
    private DispatcherTimer? _scrollSpeedTimer;
    private IDisposable? _scrollTrackingSub;
    private double _lastScrollOffset;
    private bool _scrollInitialized;

    #endregion

    #region Styled Properties

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

    public static readonly StyledProperty<bool> UseInternalScrollProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(UseInternalScroll), false);

    public bool UseInternalScroll
    {
        get => GetValue(UseInternalScrollProperty);
        set => SetValue(UseInternalScrollProperty, value);
    }

    public static readonly StyledProperty<bool> EnableSmoothLoadingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableSmoothLoading), true);

    public bool EnableSmoothLoading
    {
        get => GetValue(EnableSmoothLoadingProperty);
        set => SetValue(EnableSmoothLoadingProperty, value);
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

    /// <summary>
    /// True когда пользователь активно скроллит список.
    /// Используется для отложенной загрузки обложек через DeferredUrlConverter.
    /// </summary>
    public static readonly StyledProperty<bool> IsScrollingFastProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsScrollingFast), false);

    public bool IsScrollingFast
    {
        get => GetValue(IsScrollingFastProperty);
        private set => SetValue(IsScrollingFastProperty, value);
    }

    #endregion

    #region Direct Properties

    public static readonly DirectProperty<TrackListControl, string> SearchingTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(SearchingText),
            static o => o.SearchingText,
            static (o, v) => o.SearchingText = v);

    public string SearchingText
    {
        get;
        private set => SetAndRaise(SearchingTextProperty, ref field, value);
    } = "Searching...";

    public static readonly DirectProperty<TrackListControl, string> LoadingMoreTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(LoadingMoreText),
            static o => o.LoadingMoreText,
            static (o, v) => o.LoadingMoreText = v);

    public string LoadingMoreText
    {
        get;
        private set => SetAndRaise(LoadingMoreTextProperty, ref field, value);
    } = "Searching for more";

    public static readonly DirectProperty<TrackListControl, string> EndOfListTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(EndOfListText),
            static o => o.EndOfListText,
            static (o, v) => o.EndOfListText = v);

    public string EndOfListText
    {
        get;
        private set => SetAndRaise(EndOfListTextProperty, ref field, value);
    } = "End of list";

    public static readonly DirectProperty<TrackListControl, ScrollBarVisibility> ScrollVisibilityProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, ScrollBarVisibility>(
            nameof(ScrollVisibility),
            static o => o.ScrollVisibility);

    public ScrollBarVisibility ScrollVisibility
    {
        get;
        private set => SetAndRaise(ScrollVisibilityProperty, ref field, value);
    } = ScrollBarVisibility.Disabled;

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
        _listBox = this.FindControl<ListBox>("MainListBox");

        if (_listBox is { IsLoaded: true })
        {
            _scrollViewer = _listBox.FindDescendantOfType<ScrollViewer>();
            SetupScrollTracking();
        }
        else
        {
            _listBox?.Loaded += OnListBoxLoaded;
        }

        _autoScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnAutoScrollTick);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        base.OnDetachedFromVisualTree(e);

        // Авто-скролл
        _autoScrollTimer?.Stop();
        _autoScrollTimer = null;

        // Скорость скролла
        _scrollSpeedTimer?.Stop();
        _scrollSpeedTimer = null;
        _scrollTrackingSub?.Dispose();
        _scrollTrackingSub = null;
        _scrollInitialized = false;
        IsScrollingFast = false;

        _listBox?.Loaded -= OnListBoxLoaded;
        _listBox = null;
        _lastDragOverItem = null;
        _scrollViewer = null;
    }

    private void OnListBoxLoaded(object? sender, RoutedEventArgs e)
    {
        _scrollViewer = _listBox?.FindDescendantOfType<ScrollViewer>();
        SetupScrollTracking();
    }

    #endregion

    #region Scroll Speed Tracking

    /// <summary>
    /// Подписка на изменения Offset в ScrollViewer для отслеживания скорости скролла.
    /// При быстром скролле IsScrollingFast=true → DeferredUrlConverter отдаёт null → обложки не грузятся.
    /// Через 150мс после остановки скролла IsScrollingFast=false → обложки загружаются.
    /// </summary>
    private void SetupScrollTracking()
    {
        if (_scrollViewer == null) return;

        // Очищаем предыдущую подписку
        _scrollTrackingSub?.Dispose();
        _scrollSpeedTimer?.Stop();

        _scrollInitialized = false;
        _lastScrollOffset = 0;

        // Таймер дебаунса — срабатывает через 150мс после последнего скролл-события
        _scrollSpeedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ScrollDebounceMs)
        };
        _scrollSpeedTimer.Tick += OnScrollSpeedDebounce;

        // Подписка на изменение Offset
        _scrollTrackingSub = _scrollViewer.GetObservable(ScrollViewer.OffsetProperty)
            .Subscribe(OnScrollOffsetChanged);
    }

    private void OnScrollOffsetChanged(Vector offset)
    {
        // Пропускаем первое значение (инициализация)
        if (!_scrollInitialized)
        {
            _scrollInitialized = true;
            _lastScrollOffset = offset.Y;
            return;
        }

        var delta = Math.Abs(offset.Y - _lastScrollOffset);
        _lastScrollOffset = offset.Y;

        if (delta > ScrollSpeedThreshold)
        {
            IsScrollingFast = true;
            _scrollSpeedTimer?.Stop();
            _scrollSpeedTimer?.Start();
        }
    }

    private void OnScrollSpeedDebounce(object? sender, EventArgs e)
    {
        _scrollSpeedTimer?.Stop();
        IsScrollingFast = false;
    }

    #endregion

    #region Property Changed

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UseInternalScrollProperty)
        {
            ScrollVisibility = UseInternalScroll
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
        }
        else if (change.Property == IsPlaylistContextProperty ||
                 change.Property == IsQueueContextProperty ||
                 change.Property == ItemsProperty)
        {
            UpdateItemsContext();

            if (change.Property == ItemsProperty)
            {
                Dispatcher.UIThread.Post(CheckInitialLoad, DispatcherPriority.Background);
            }
        }
    }

    private void CheckInitialLoad()
    {
        if (_scrollViewer == null || LoadMoreCommand == null) return;

        if (_scrollViewer.Extent.Height <= _scrollViewer.Viewport.Height)
        {
            if (LoadMoreCommand.CanExecute(null))
            {
                LoadMoreCommand.Execute(null);
            }
        }
    }

    #endregion

    #region Drag & Drop Logic

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!EnableReordering) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
        }
    }

    private async void OnListBoxPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!EnableReordering || _isDragging) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        var currentPoint = e.GetPosition(null);
        double deltaX = Math.Abs(currentPoint.X - _dragStartPoint.X);
        double deltaY = Math.Abs(currentPoint.Y - _dragStartPoint.Y);

        if (deltaX < DragThreshold && deltaY < DragThreshold) return;

        if (e.Source is not Visual sourceVisual) return;
        var draggedItem = sourceVisual.FindAncestorOfType<ListBoxItem>();
        if (draggedItem == null) return;

        int dragIndex = _listBox?.IndexFromContainer(draggedItem) ?? -1;
        if (dragIndex < 0) return;

        _isDragging = true;
        using var dragData = new DragDataTransfer(dragIndex);
        draggedItem.Classes.Add("dragging");

        try { await DragDrop.DoDragDropAsync(e, dragData, DragDropEffects.Move); }
        finally
        {
            draggedItem.Classes.Remove("dragging");
            CleanupDragStyles();
            _isDragging = false;
        }
    }

    private void OnListBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
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

        if (e.Source is not Visual sourceVisual) return;

        var overItem = sourceVisual.FindAncestorOfType<ListBoxItem>();

        if (_lastDragOverItem != null && _lastDragOverItem != overItem)
        {
            _lastDragOverItem.Classes.Remove("insert-top");
            _lastDragOverItem.Classes.Remove("insert-bottom");
        }

        if (overItem == null) return;

        _lastDragOverItem = overItem;

        var position = e.GetPosition(overItem);
        double halfHeight = overItem.Bounds.Height / 2;

        if (position.Y < halfHeight)
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

        if (!EnableReordering || !HasTrackIndexData(e) || _listBox == null) return;

        int oldIndex = GetTrackIndex(e);
        if (oldIndex < 0) return;

        int newIndex = CalculateDropIndex(e, oldIndex);
        if (newIndex >= 0 && oldIndex != newIndex && newIndex < _listBox.ItemCount)
        {
            MoveItemCommand?.Execute((oldIndex, newIndex));
        }
    }

    private void HandleAutoScroll(DragEventArgs e)
    {
        if (_scrollViewer == null) return;

        var positionInScroller = e.GetPosition(_scrollViewer);

        if (positionInScroller.Y < AutoScrollMargin)
        {
            _autoScrollOffset = -AutoScrollAmount;
            _autoScrollTimer?.Start();
        }
        else if (positionInScroller.Y > _scrollViewer.Bounds.Height - AutoScrollMargin)
        {
            _autoScrollOffset = AutoScrollAmount;
            _autoScrollTimer?.Start();
        }
        else
        {
            _autoScrollOffset = 0;
            _autoScrollTimer?.Stop();
        }
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_scrollViewer == null || _autoScrollOffset == 0) return;
        _scrollViewer.Offset += new Vector(0, _autoScrollOffset);
    }

    private static bool HasTrackIndexData(DragEventArgs e)
    {
        return e.DataTransfer is DragDataTransfer ||
               e.DataTransfer.Formats.Any(f => f.Identifier == DragFormatTrackIndex);
    }

    private static int GetTrackIndex(DragEventArgs e)
    {
        IDataTransfer transfer = e.DataTransfer;

        if (transfer is DragDataTransfer adapter)
            return adapter.TrackIndex;

        foreach (var item in transfer.Items)
        {
            if (item is DragDataTransferItem dragItem)
                return dragItem.TrackIndex;

            object? rawValue = item.TryGetRaw(TrackIndexDataFormat);

            if (rawValue is string strValue && int.TryParse(strValue, out int index))
                return index;

            if (rawValue is int intValue)
                return intValue;
        }

        return -1;
    }

    private int CalculateDropIndex(DragEventArgs e, int oldIndex)
    {
        if (_listBox == null || e.Source is not Visual sourceVisual)
            return -1;

        var targetItem = sourceVisual.FindAncestorOfType<ListBoxItem>();

        if (targetItem == null)
            return _listBox.ItemCount - 1;

        int containerIndex = _listBox.IndexFromContainer(targetItem);
        if (containerIndex < 0)
            return -1;

        var position = e.GetPosition(targetItem);
        bool insertAfter = position.Y > targetItem.Bounds.Height / 2;

        int targetVisualIndex = containerIndex;
        if (insertAfter)
            targetVisualIndex++;

        int moveIndex = targetVisualIndex;
        if (oldIndex < targetVisualIndex)
            moveIndex--;

        return Math.Clamp(moveIndex, 0, _listBox.ItemCount - 1);
    }

    private void CleanupDragStyles()
    {
        if (_lastDragOverItem != null)
        {
            _lastDragOverItem.Classes.Remove("insert-top");
            _lastDragOverItem.Classes.Remove("insert-bottom");
            _lastDragOverItem = null;
        }
    }

    #endregion

    #region Drag Data Types (Avalonia 11.3+)

    private sealed class DragDataTransferItem : IDataTransferItem
    {
        public int TrackIndex { get; }

        public DragDataTransferItem(int trackIndex)
        {
            TrackIndex = trackIndex;
            Formats = [TrackIndexDataFormat];
        }

        public IReadOnlyList<DataFormat> Formats { get; }

        public object? TryGetRaw(DataFormat format)
        {
            if (format.Identifier == DragFormatTrackIndex)
                return TrackIndex.ToString();
            return null;
        }
    }

    private sealed class DragDataTransfer(int trackIndex) : IDataTransfer
    {
        private bool _disposed;
        public int TrackIndex { get; } = trackIndex;

        public IReadOnlyList<DataFormat> Formats { get; } = [TrackIndexDataFormat];
        public IReadOnlyList<IDataTransferItem> Items { get; } = [new DragDataTransferItem(trackIndex)];

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    #endregion

    #region Context Menu & Other Handlers

    private void OnTrackRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
            return;

        if (sender is not Border border)
            return;

        var moreButton = FindDescendantOfType<Button>(border, b => b.Classes.Contains("more-btn"));
        if (moreButton?.Flyout is { } flyout)
        {
            flyout.ShowAt(moreButton);
            e.Handled = true;
        }
    }

    private void OnMenuFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target: Button button } &&
            button.DataContext is TrackItemViewModel vm)
        {
            vm.IsMenuOpen = true;
        }
    }

    private void OnMenuFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target: Button button } &&
            button.DataContext is TrackItemViewModel vm)
        {
            vm.IsMenuOpen = false;
        }
    }

    #endregion

    #region Private Methods

    private void UpdateLocalizedTexts()
    {
        var l = LocalizationService.Instance;
        SearchingText = l["Search_Searching"] ?? "Searching...";
        LoadingMoreText = l["Search_LoadingMore"] ?? "Searching for more";
        EndOfListText = l["Search_EndOfList"] ?? "End of list";
    }

    private void UpdateItemsContext()
    {
        if (Items == null) return;

        foreach (var item in Items)
        {
            if (item is TrackItemViewModel track)
            {
                track.IsPlaylistContext = IsPlaylistContext;
                track.IsQueueContext = IsQueueContext;
            }
        }
    }

    private static T? FindDescendantOfType<T>(Visual visual, Func<T, bool>? predicate = null)
        where T : Visual
    {
        foreach (var child in visual.GetVisualChildren())
        {
            if (child is T typedChild && (predicate == null || predicate(typedChild)))
                return typedChild;

            var result = FindDescendantOfType(child, predicate);
            if (result != null)
                return result;
        }

        return null;
    }

    #endregion
}