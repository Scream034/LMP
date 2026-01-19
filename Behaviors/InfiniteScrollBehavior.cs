// Behaviors/InfiniteScrollBehavior.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;
using System.Diagnostics;
using System.Windows.Input;

namespace MyLiteMusicPlayer.Behaviors;

/// <summary>
/// Behavior для автоматической подгрузки при прокрутке к концу списка.
/// Прикрепляется к ScrollViewer.
/// </summary>
public class InfiniteScrollBehavior : Behavior<ScrollViewer>
{
    private IDisposable? _offsetSubscription;
    private bool _isExecuting;

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

        if (AssociatedObject != null)
        {
            _offsetSubscription = AssociatedObject
                .GetObservable(ScrollViewer.OffsetProperty)
                .Subscribe(OnOffsetChanged);
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        _offsetSubscription?.Dispose();
        _offsetSubscription = null;
    }

    private void OnOffsetChanged(Vector offset)
    {
        if (AssociatedObject is not ScrollViewer sv || Command == null)
            return;

        if (_isExecuting) return;

        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= 0)
            return;

        if (sv.Extent.Height <= sv.Viewport.Height)
            return;

        var distanceToEnd = sv.Extent.Height - sv.Viewport.Height - offset.Y;

        if (distanceToEnd <= Threshold && Command.CanExecute(null))
        {
            Debug.WriteLine($"[Scroll] Loading more (distance: {distanceToEnd:F0}px)");
            ExecuteWithGuard();
        }
    }

    private async void ExecuteWithGuard()
    {
        _isExecuting = true;

        try
        {
            Command?.Execute(null);
            await Task.Delay(100);
        }
        finally
        {
            _isExecuting = false;
        }
    }
}