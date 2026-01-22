using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MyLiteMusicPlayer.Services;

namespace MyLiteMusicPlayer.Views.Controls;

public partial class TrackListControl : UserControl
{
    public TrackListControl()
    {
        InitializeComponent();

        // Подписка на смену языка
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            // Проверяем, в UI-потоке ли мы
            if (Dispatcher.UIThread.CheckAccess())
            {
                UpdateLocalizedTexts();
            }
            else
            {
                // Если нет - переключаемся в UI-поток
                Dispatcher.UIThread.Post(UpdateLocalizedTexts);
            }
        };

        UpdateLocalizedTexts();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UpdateLocalizedTexts()
    {
        SearchingText = LocalizationService.Instance["Search_Searching"] ?? "Searching...";
        LoadingMoreText = LocalizationService.Instance["Search_LoadingMore"] ?? "Searching for more";
        EndOfListText = LocalizationService.Instance["Search_EndOfList"] ?? "End of list";
    }

    // --- Localization Properties ---

    private string _searchingText = "Searching...";
    public static readonly DirectProperty<TrackListControl, string> SearchingTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(SearchingText), o => o.SearchingText, (o, v) => o.SearchingText = v);

    public string SearchingText
    {
        get => _searchingText;
        private set => SetAndRaise(SearchingTextProperty, ref _searchingText, value);
    }

    private string _loadingMoreText = "Searching for more";
    public static readonly DirectProperty<TrackListControl, string> LoadingMoreTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(LoadingMoreText), o => o.LoadingMoreText, (o, v) => o.LoadingMoreText = v);

    public string LoadingMoreText
    {
        get => _loadingMoreText;
        private set => SetAndRaise(LoadingMoreTextProperty, ref _loadingMoreText, value);
    }

    private string _endOfListText = "End of list";
    public static readonly DirectProperty<TrackListControl, string> EndOfListTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(EndOfListText), o => o.EndOfListText, (o, v) => o.EndOfListText = v);

    public string EndOfListText
    {
        get => _endOfListText;
        private set => SetAndRaise(EndOfListTextProperty, ref _endOfListText, value);
    }

    // --- Existing Properties ---

    public static readonly StyledProperty<bool> EnableSmoothLoadingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableSmoothLoading), true);

    public bool EnableSmoothLoading
    {
        get => GetValue(EnableSmoothLoadingProperty);
        set => SetValue(EnableSmoothLoadingProperty, value);
    }

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TrackListControl, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> LoadMoreCommandProperty =
        AvaloniaProperty.Register<TrackListControl, ICommand?>(nameof(LoadMoreCommand));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoading));

    public static readonly StyledProperty<bool> IsLoadingMoreProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoadingMore));

    public static readonly StyledProperty<bool> IsFetchingFromNetworkProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsFetchingFromNetwork));

    public static readonly StyledProperty<bool> ReachedEndProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(ReachedEnd));

    public static readonly StyledProperty<bool> UseSearchLoaderProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(UseSearchLoader), false);

    public static readonly StyledProperty<bool> UseInternalScrollProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(UseInternalScroll), false);

    private ScrollBarVisibility _scrollVisibility = ScrollBarVisibility.Disabled;
    public static readonly DirectProperty<TrackListControl, ScrollBarVisibility> ScrollVisibilityProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, ScrollBarVisibility>(
            nameof(ScrollVisibility), o => o.ScrollVisibility);

    public ScrollBarVisibility ScrollVisibility
    {
        get => _scrollVisibility;
        private set => SetAndRaise(ScrollVisibilityProperty, ref _scrollVisibility, value);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ICommand? LoadMoreCommand
    {
        get => GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool IsLoadingMore
    {
        get => GetValue(IsLoadingMoreProperty);
        set => SetValue(IsLoadingMoreProperty, value);
    }

    public bool IsFetchingFromNetwork
    {
        get => GetValue(IsFetchingFromNetworkProperty);
        set => SetValue(IsFetchingFromNetworkProperty, value);
    }

    public bool ReachedEnd
    {
        get => GetValue(ReachedEndProperty);
        set => SetValue(ReachedEndProperty, value);
    }

    public bool UseSearchLoader
    {
        get => GetValue(UseSearchLoaderProperty);
        set => SetValue(UseSearchLoaderProperty, value);
    }

    public bool UseInternalScroll
    {
        get => GetValue(UseInternalScrollProperty);
        set => SetValue(UseInternalScrollProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UseInternalScrollProperty)
        {
            ScrollVisibility = UseInternalScroll
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
        }
    }
}