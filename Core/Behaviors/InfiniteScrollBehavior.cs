using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using System.Windows.Input;

namespace LMP.Core.Behaviors;

/// <summary>
/// Behavior для автоматической подгрузки с защитой от "мёртвой зоны".
/// </summary>
public sealed class InfiniteScrollBehavior : Behavior<Control>
{
    private IDisposable? _offsetSubscription;
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

        // НОВОЕ: Таймер для повторных проверок
        _retryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _retryTimer.Tick += OnRetryTimerTick;
    }

    private void OnAssociatedObjectLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AssociatedObject != null)
        {
            AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
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
        
        // Также подписываемся на изменение размера контента
        sv.GetObservable(ScrollViewer.ExtentProperty)
            .Subscribe(_ => CheckAndTrigger());
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        
        _offsetSubscription?.Dispose();
        _offsetSubscription = null;
        
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

        // Защита от невалидных значений
        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= 0)
            return;

        double scrollableHeight = sv.Extent.Height - sv.Viewport.Height;
        
        //  Если контент меньше или равен вьюпорту, 
        // и команда может выполниться — выполняем (нужно подгрузить ещё)
        if (scrollableHeight <= 0)
        {
            if (Command.CanExecute(null))
            {
                ExecuteWithRetry();
            }
            return;
        }

        double distanceToEnd = scrollableHeight - sv.Offset.Y;

        //  Более агрессивный триггер
        // Срабатываем если близко к концу ИЛИ уже в самом конце
        bool nearEnd = distanceToEnd <= Threshold;
        bool atEnd = distanceToEnd <= 1; // Практически в конце

        if ((nearEnd || atEnd) && Command.CanExecute(null))
        {
            ExecuteWithRetry();
        }
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
            
            // КЛЮЧЕВОЕ: Даём время на обновление UI и проверяем снова
            await Task.Delay(100);
            
            // Проверяем, нужно ли ещё грузить
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
            //  Короткая задержка вместо длинной
            await Task.Delay(50);
            _isExecuting = false;
            
            // Проверяем сразу после разблокировки
            if (_needsRetry)
            {
                CheckAndTrigger();
            }
        }
    }
}