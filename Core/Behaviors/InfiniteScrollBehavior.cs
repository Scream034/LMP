using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using System.Windows.Input;

namespace LMP.Core.Behaviors;

/// <summary>
/// Behavior для автоматической подгрузки с защитой от "мёртвой зоны".
/// Поддерживает размещение на ScrollViewer напрямую или на дочернем элементе
/// (например ItemsRepeater внутри ScrollViewer).
/// </summary>
public sealed class InfiniteScrollBehavior : Behavior<Control>
{
    private IDisposable? _offsetSubscription;
    private IDisposable? _extentSubscription;
    private ScrollViewer? _scrollViewer;
    private DispatcherTimer? _retryTimer;

    private volatile bool _isExecuting;
    private volatile bool _needsRetry;

    #region Styled Properties

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

    #endregion

    protected override void OnAttached()
    {
        base.OnAttached();

        if (AssociatedObject is ScrollViewer sv)
        {
            AttachToScrollViewer(sv);
        }
        else
        {
            AssociatedObject?.Loaded += OnAssociatedObjectLoaded;
        }

        _retryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _retryTimer.Tick += OnRetryTimerTick;
    }

    private void OnAssociatedObjectLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AssociatedObject == null) return;

        AssociatedObject.Loaded -= OnAssociatedObjectLoaded;

        // Ищем ScrollViewer: сначала среди потомков, затем среди предков.
        // Предок нужен для случая ItemsRepeater внутри ScrollViewer.
        var scroll = AssociatedObject.FindDescendantOfType<ScrollViewer>()
                  ?? AssociatedObject.FindAncestorOfType<ScrollViewer>();

        if (scroll != null)
            AttachToScrollViewer(scroll);
    }

    private void AttachToScrollViewer(ScrollViewer sv)
    {
        _scrollViewer = sv;

        _offsetSubscription = sv.GetObservable(ScrollViewer.OffsetProperty)
            .Subscribe(OnOffsetChanged);

        _extentSubscription = sv.GetObservable(ScrollViewer.ExtentProperty)
            .Subscribe(_ => CheckAndTrigger());
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();

        _offsetSubscription?.Dispose();
        _offsetSubscription = null;

        _extentSubscription?.Dispose();
        _extentSubscription = null;

        _retryTimer?.Stop();
        _retryTimer = null;

        _scrollViewer = null;
    }

    private void OnOffsetChanged(Vector offset)
    {
        CheckAndTrigger();
    }

    private void OnRetryTimerTick(object? sender, EventArgs e)
    {
        if (!_needsRetry || _isExecuting)
        {
            _retryTimer?.Stop();
            return;
        }

        CheckAndTrigger();
    }

    private void CheckAndTrigger()
    {
        if (!IsEnabled || _scrollViewer == null || Command == null || _isExecuting)
            return;

        var sv = _scrollViewer;

        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= 0)
            return;

        double scrollableHeight = sv.Extent.Height - sv.Viewport.Height;

        if (scrollableHeight <= 0)
        {
            if (Command.CanExecute(null))
                ExecuteWithRetry();
            return;
        }

        double distanceToEnd = scrollableHeight - sv.Offset.Y;

        bool nearEnd = distanceToEnd <= Threshold;
        bool atEnd = distanceToEnd <= 1;

        if ((nearEnd || atEnd) && Command.CanExecute(null))
            ExecuteWithRetry();
    }

    private async void ExecuteWithRetry()
    {
        if (_isExecuting) return;

        _isExecuting = true;
        _needsRetry = false;
        _retryTimer?.Stop();

        try
        {
            Command?.Execute(null);

            await Task.Delay(100);

            if (_scrollViewer != null && Command?.CanExecute(null) == true)
            {
                double scrollableHeight = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;
                double distanceToEnd = scrollableHeight - _scrollViewer.Offset.Y;

                if (distanceToEnd <= Threshold || scrollableHeight <= 0)
                {
                    _needsRetry = true;
                    _retryTimer?.Start();
                }
            }
        }
        finally
        {
            await Task.Delay(50);
            _isExecuting = false;

            if (_needsRetry)
                CheckAndTrigger();
        }
    }
}