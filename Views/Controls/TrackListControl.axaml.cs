using System;
using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

namespace MyLiteMusicPlayer.Views.Controls;

public partial class TrackListControl : UserControl
{
    public TrackListControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // --- Styled Properties ---

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

    // --- Direct Property (Исправляет ошибку CS7036) ---

    private ScrollBarVisibility _scrollVisibility = ScrollBarVisibility.Disabled;

    public static readonly DirectProperty<TrackListControl, ScrollBarVisibility> ScrollVisibilityProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, ScrollBarVisibility>(
            nameof(ScrollVisibility),
            o => o.ScrollVisibility);

    public ScrollBarVisibility ScrollVisibility
    {
        get => _scrollVisibility;
        private set => SetAndRaise(ScrollVisibilityProperty, ref _scrollVisibility, value);
    }

    // --- Accessors ---

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

    // Перехватываем изменение UseInternalScroll и обновляем ScrollVisibility
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