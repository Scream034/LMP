using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LMP.Core.Services;
using LMP.Features.Shared;

namespace LMP.UI.Controls;

/// <summary>
/// Универсальный элемент управления для отображения списка музыкальных треков.
/// Поддерживает различные контексты: поиск, плейлист, очередь.
/// </summary>
public partial class TrackListControl : UserControl
{
    #region Fields

    private readonly EventHandler<string> _languageChangedHandler;

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

    #endregion

    #region Direct Properties

    public static readonly DirectProperty<TrackListControl, string> SearchingTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(SearchingText),
            static o => o.SearchingText,
            static (o, v) => o.SearchingText = v);

    private string _searchingText = "Searching...";

    public string SearchingText
    {
        get => _searchingText;
        private set => SetAndRaise(SearchingTextProperty, ref _searchingText, value);
    }

    public static readonly DirectProperty<TrackListControl, string> LoadingMoreTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(LoadingMoreText),
            static o => o.LoadingMoreText,
            static (o, v) => o.LoadingMoreText = v);

    private string _loadingMoreText = "Searching for more";

    public string LoadingMoreText
    {
        get => _loadingMoreText;
        private set => SetAndRaise(LoadingMoreTextProperty, ref _loadingMoreText, value);
    }

    public static readonly DirectProperty<TrackListControl, string> EndOfListTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(EndOfListText),
            static o => o.EndOfListText,
            static (o, v) => o.EndOfListText = v);

    private string _endOfListText = "End of list";

    public string EndOfListText
    {
        get => _endOfListText;
        private set => SetAndRaise(EndOfListTextProperty, ref _endOfListText, value);
    }

    public static readonly DirectProperty<TrackListControl, ScrollBarVisibility> ScrollVisibilityProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, ScrollBarVisibility>(
            nameof(ScrollVisibility),
            static o => o.ScrollVisibility);

    private ScrollBarVisibility _scrollVisibility = ScrollBarVisibility.Disabled;

    public ScrollBarVisibility ScrollVisibility
    {
        get => _scrollVisibility;
        private set => SetAndRaise(ScrollVisibilityProperty, ref _scrollVisibility, value);
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
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        base.OnDetachedFromVisualTree(e);
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
        }
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// ПКМ на строке трека открывает контекстное меню.
    /// </summary>
    private void OnTrackRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
            return;

        if (IsQueueContext)
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

    /// <summary>
    /// Меню открылось — устанавливаем IsMenuOpen = true.
    /// </summary>
    private void OnMenuFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target: Button button } &&
            button.DataContext is TrackItemViewModel vm)
        {
            vm.IsMenuOpen = true;
        }
    }

    /// <summary>
    /// Меню закрылось — устанавливаем IsMenuOpen = false.
    /// </summary>
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

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

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

    /// <summary>
    /// Находит первый дочерний элемент указанного типа, удовлетворяющий условию.
    /// </summary>
    private static T? FindDescendantOfType<T>(Visual visual, Func<T, bool>? predicate = null)
        where T : Visual
    {
        foreach (var child in visual.GetVisualChildren())
        {
            if (child is T typedChild && (predicate == null || predicate(typedChild)))
                return typedChild;

            var result = FindDescendantOfType<T>(child, predicate);
            if (result != null)
                return result;
        }

        return null;
    }

    #endregion
}