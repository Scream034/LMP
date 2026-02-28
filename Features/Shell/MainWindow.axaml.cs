using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using LMP.Core.Helpers;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Features.Shell;

public partial class MainWindow : Window
{
    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;

    private CancellationTokenSource? _cleanupCts;

    private volatile bool _isMinimized;
    private DateTime _lastCleanupTime = DateTime.MinValue;
    private const int MinCleanupIntervalMs = 30_000;

    public MainWindow()
    {
        InitializeComponent();

        PropertyChanged += MainWindow_PropertyChanged;
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _minimizeButton = this.FindControl<Button>("MinimizeButton");
        _maximizeButton = this.FindControl<Button>("MaximizeButton");
        _closeButton = this.FindControl<Button>("CloseButton");
        _dragArea = this.FindControl<Border>("DragArea");

        _minimizeButton?.Click += (_, _) => WindowState = WindowState.Minimized;
        _maximizeButton?.Click += (_, _) => ToggleMaximize();
        _closeButton?.Click += (_, _) => Close();

        var titleBar = this.FindControl<Grid>("TitleBar");
        titleBar?.PointerPressed += OnTitleBarPointerPressed;
        _dragArea?.DoubleTapped += (_, _) => ToggleMaximize();
    }

    private void MainWindow_PropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;

        var state = (WindowState)e.NewValue!;

        if (state == WindowState.Minimized)
        {
            _isMinimized = true;

            ViewModelBase.BroadcastSuspend();
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(30));
            ScheduleCleanup(TimeSpan.FromSeconds(2));
        }
        else if (_isMinimized)
        {
            _isMinimized = false;

            CancelCleanup();
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(5));
            ViewModelBase.BroadcastResume();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        ScheduleCleanup(TimeSpan.FromMinutes(2));
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        CancelCleanup();
    }

    private void ScheduleCleanup(TimeSpan delay)
    {
        CancelCleanup();
        _cleanupCts = new CancellationTokenSource();
        var token = _cleanupCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested) return;

                var now = DateTime.UtcNow;
                if ((now - _lastCleanupTime).TotalMilliseconds < MinCleanupIntervalMs)
                    return;

                _lastCleanupTime = now;
                PerformCleanup();
            }
            catch (OperationCanceledException) { }
        });
    }

    private static void PerformCleanup()
    {
        Log.Info($"[Memory] Cleanup");

        _ = Task.Run(() =>
        {
            try
            {
                var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
                imageCache.ClearMemoryCache();

                var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();
                vmFactory.CleanupCache();

                var registry = Program.Services.GetRequiredService<TrackRegistry>();
                registry.CleanupDeadReferences();

                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

                MemoryHelpers.TrimWorkingSet();

                var afterMb = GC.GetTotalMemory(false) / 1024 / 1024;
                Log.Info($"[Memory] After cleanup: {afterMb} MB");
            }
            catch (Exception ex)
            {
                Log.Error($"[Memory] Cleanup error: {ex.Message}");
            }
        });
    }

    private void CancelCleanup()
    {
        if (_cleanupCts != null)
        {
            _cleanupCts.Cancel();
            _cleanupCts.Dispose();
            _cleanupCts = null;
        }
    }

    /// <summary>
    /// Обработка нажатия на TitleBar для перетаскивания окна.
    /// Игнорирует клики на Button (навигационные табы, window controls).
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Не начинаем drag если кликнули на кнопку (nav-tab, window-btn и т.д.)
        if (e.Source is Button) return;

        // Проверяем родительские элементы — клик внутри кнопки (на иконке/тексте)
        if (e.Source is Avalonia.Visual visual)
        {
            var parent = visual;
            while (parent != null)
            {
                if (parent is Button) return;
                parent = parent.GetVisualParent();
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelCleanup();
        PropertyChanged -= MainWindow_PropertyChanged;
        Deactivated -= OnWindowDeactivated;
        Activated -= OnWindowActivated;
        base.OnClosed(e);
    }
}