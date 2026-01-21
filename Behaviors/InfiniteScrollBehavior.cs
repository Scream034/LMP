using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using System.Windows.Input;

namespace MyLiteMusicPlayer.Behaviors;

/// <summary>
/// Behavior для автоматической подгрузки.
/// Можно вешать на ScrollViewer или на ListBox (найдет ScrollViewer внутри).
/// </summary>
public class InfiniteScrollBehavior : Behavior<Control>
{
    private IDisposable? _offsetSubscription;
    private bool _isExecuting;
    private ScrollViewer? _scrollViewer;

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<InfiniteScrollBehavior, ICommand?>(nameof(Command));

    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<InfiniteScrollBehavior, double>(nameof(Threshold), 200);

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();

        // Пытаемся найти ScrollViewer сразу или после загрузки
        if (AssociatedObject is ScrollViewer sv)
        {
            AttachToScrollViewer(sv);
        }
        else if (AssociatedObject != null)
        {
            // Если прицеплен к ListBox, ждем пока он загрузится, чтобы найти внутри шаблонный ScrollViewer
            AssociatedObject.Loaded += OnAssociatedObjectLoaded;
        }
    }

    private void OnAssociatedObjectLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AssociatedObject != null)
        {
            AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
            // Ищем ScrollViewer в визуальном дереве контрола
            var scroll = AssociatedObject.FindDescendantOfType<ScrollViewer>();
            if (scroll != null)
            {
                AttachToScrollViewer(scroll);
            }
        }
    }

    private void AttachToScrollViewer(ScrollViewer sv)
    {
        _scrollViewer = sv;
        _offsetSubscription = sv.GetObservable(ScrollViewer.OffsetProperty)
            .Subscribe(OnOffsetChanged);
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        _offsetSubscription?.Dispose();
        _offsetSubscription = null;
        _scrollViewer = null;
    }

    private void OnOffsetChanged(Vector offset)
    {
        if (_scrollViewer == null || Command == null || _isExecuting)
            return;

        var sv = _scrollViewer;

        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= 0)
            return;

        // Если контент меньше вьюпорта - не грузим (или грузим сразу, зависит от логики, тут не грузим)
        if (sv.Extent.Height <= sv.Viewport.Height)
            return;

        var distanceToEnd = sv.Extent.Height - sv.Viewport.Height - offset.Y;

        if (distanceToEnd <= Threshold && Command.CanExecute(null))
        {
            Log.Info($"Loading more (distance: {distanceToEnd:F0}px)");
            ExecuteWithGuard();
        }
    }

    private async void ExecuteWithGuard()
    {
        _isExecuting = true;
        try
        {
            Command?.Execute(null);
            await Task.Delay(500); // Небольшая задержка перед следующим срабатыванием
        }
        finally
        {
            _isExecuting = false;
        }
    }
}