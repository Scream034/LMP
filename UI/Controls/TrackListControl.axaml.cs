using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LMP.Core.Services;

namespace LMP.UI.Controls;

/// <summary>
/// Пользовательский элемент управления для отображения списка музыкальных треков.
/// Поддерживает бесконечную прокрутку, скелетоны загрузки и автоматическую локализацию.
/// </summary>
public partial class TrackListControl : UserControl
{
    #region Fields

    // Храним ссылку на обработчик события для корректной отписки
    private readonly EventHandler<string> _languageChangedHandler;

    #endregion

    #region Avalonia Properties

    /// <summary>
    /// Текст, отображаемый при поиске.
    /// </summary>
    public static readonly DirectProperty<TrackListControl, string> SearchingTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(SearchingText), static o => o.SearchingText, static (o, v) => o.SearchingText = v);

    private string _searchingText = "Searching...";

    public string SearchingText
    {
        get => _searchingText;
        private set => SetAndRaise(SearchingTextProperty, ref _searchingText, value);
    }

    /// <summary>
    /// Текст, отображаемый при подгрузке дополнительных элементов.
    /// </summary>
    public static readonly DirectProperty<TrackListControl, string> LoadingMoreTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(LoadingMoreText), static o => o.LoadingMoreText, static (o, v) => o.LoadingMoreText = v);

    private string _loadingMoreText = "Searching for more";

    public string LoadingMoreText
    {
        get => _loadingMoreText;
        private set => SetAndRaise(LoadingMoreTextProperty, ref _loadingMoreText, value);
    }

    /// <summary>
    /// Текст, отображаемый при достижении конца списка.
    /// </summary>
    public static readonly DirectProperty<TrackListControl, string> EndOfListTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(EndOfListText), static o => o.EndOfListText, static (o, v) => o.EndOfListText = v);

    private string _endOfListText = "End of list";

    public string EndOfListText
    {
        get => _endOfListText;
        private set => SetAndRaise(EndOfListTextProperty, ref _endOfListText, value);
    }

    /// <summary>
    /// Включает анимацию плавной загрузки (скелетоны).
    /// </summary>
    public static readonly StyledProperty<bool> EnableSmoothLoadingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableSmoothLoading), true);

    public bool EnableSmoothLoading
    {
        get => GetValue(EnableSmoothLoadingProperty);
        set => SetValue(EnableSmoothLoadingProperty, value);
    }

    /// <summary>
    /// Коллекция элементов для отображения.
    /// </summary>
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TrackListControl, IEnumerable?>(nameof(Items));

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>
    /// Команда для загрузки следующей порции данных.
    /// </summary>
    public static readonly StyledProperty<ICommand?> LoadMoreCommandProperty =
        AvaloniaProperty.Register<TrackListControl, ICommand?>(nameof(LoadMoreCommand));

    public ICommand? LoadMoreCommand
    {
        get => GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    /// <summary>
    /// Флаг первичной загрузки.
    /// </summary>
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoading));

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>
    /// Флаг подгрузки дополнительных данных.
    /// </summary>
    public static readonly StyledProperty<bool> IsLoadingMoreProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoadingMore));

    public bool IsLoadingMore
    {
        get => GetValue(IsLoadingMoreProperty);
        set => SetValue(IsLoadingMoreProperty, value);
    }

    /// <summary>
    /// Флаг активного сетевого запроса.
    /// </summary>
    public static readonly StyledProperty<bool> IsFetchingFromNetworkProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsFetchingFromNetwork));

    public bool IsFetchingFromNetwork
    {
        get => GetValue(IsFetchingFromNetworkProperty);
        set => SetValue(IsFetchingFromNetworkProperty, value);
    }

    /// <summary>
    /// Флаг достижения конца списка.
    /// </summary>
    public static readonly StyledProperty<bool> ReachedEndProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(ReachedEnd));

    public bool ReachedEnd
    {
        get => GetValue(ReachedEndProperty);
        set => SetValue(ReachedEndProperty, value);
    }

    /// <summary>
    /// Использовать специальный лоадер для поиска (с лупой).
    /// </summary>
    public static readonly StyledProperty<bool> UseSearchLoaderProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(UseSearchLoader), false);

    public bool UseSearchLoader
    {
        get => GetValue(UseSearchLoaderProperty);
        set => SetValue(UseSearchLoaderProperty, value);
    }

    /// <summary>
    /// Использовать внутренний ScrollViewer.
    /// </summary>
    public static readonly StyledProperty<bool> UseInternalScrollProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(UseInternalScroll), false);

    public bool UseInternalScroll
    {
        get => GetValue(UseInternalScrollProperty);
        set => SetValue(UseInternalScrollProperty, value);
    }

    /// <summary>
    /// Видимость внутреннего скроллбара.
    /// </summary>
    public static readonly DirectProperty<TrackListControl, ScrollBarVisibility> ScrollVisibilityProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, ScrollBarVisibility>(
            nameof(ScrollVisibility), static o => o.ScrollVisibility);

    private ScrollBarVisibility _scrollVisibility = ScrollBarVisibility.Disabled;

    public ScrollBarVisibility ScrollVisibility
    {
        get => _scrollVisibility;
        private set => SetAndRaise(ScrollVisibilityProperty, ref _scrollVisibility, value);
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="TrackListControl"/>.
    /// </summary>
    public TrackListControl()
    {
        InitializeComponent();

        // Инициализируем обработчик, но НЕ подписываемся здесь.
        // Подписка происходит только когда контрол присоединен к дереву.
        _languageChangedHandler = (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                UpdateLocalizedTexts();
            }
            else
            {
                Dispatcher.UIThread.Post(UpdateLocalizedTexts);
            }
        };

        UpdateLocalizedTexts();
    }

    #endregion

    #region Lifecycle Methods

    // Подписываемся на события только когда контрол активен
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
        UpdateLocalizedTexts();
    }

    // Обязательно отписываемся при удалении из дерева, чтобы избежать утечек памяти
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        base.OnDetachedFromVisualTree(e);
    }

    #endregion

    #region Private Methods

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

    #endregion
}