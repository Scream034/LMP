using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
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

    // Состояние окна
    private volatile bool _isMinimized;
    private DateTime _lastCleanupTime = DateTime.MinValue;
    private const int MinCleanupIntervalMs = 30_000; // Не чаще раза в 30 секунд

    public MainWindow()
    {
        InitializeComponent();

        this.PropertyChanged += MainWindow_PropertyChanged;
        this.Deactivated += OnWindowDeactivated;
        this.Activated += OnWindowActivated;
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

    //  LIFECYCLE: Minimize / Restore / Deactivate / Activate

    private void MainWindow_PropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;

        var state = (WindowState)e.NewValue!;

        if (state == WindowState.Minimized)
        {
            _isMinimized = true;

            // 1. Уведомляем ВСЕ ViewModel-и (UI-поток)
            ViewModelBase.BroadcastSuspend();

            // 2. Замедляем мониторинг памяти
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(30));

            // 3. Планируем глубокую очистку через 500мс
            ScheduleCleanup(TimeSpan.FromMilliseconds(500), aggressive: true);
        }
        else if (_isMinimized)
        {
            _isMinimized = false;

            // 1. Отменяем очистку (если не успела)
            CancelCleanup();

            // 2. Восстанавливаем мониторинг
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(5));

            // 3. Уведомляем ВСЕ ViewModel-и (UI-поток)
            ViewModelBase.BroadcastResume();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Пользователь переключился на другое окно.
        // Ждём 2 минуты. Если не вернётся — чистим память.
        ScheduleCleanup(TimeSpan.FromMinutes(2), aggressive: false);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        CancelCleanup();
    }

    //  CLEANUP: Очистка памяти

    private void ScheduleCleanup(TimeSpan delay, bool aggressive)
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

                // Проверяем интервал между очистками
                var now = DateTime.UtcNow;
                if (!aggressive && (now - _lastCleanupTime).TotalMilliseconds < MinCleanupIntervalMs)
                    return;

                _lastCleanupTime = now;
                PerformCleanup(aggressive);
            }
            catch (OperationCanceledException) { /* Ожидаемо */ }
        });
    }

    private static void PerformCleanup(bool aggressive)
    {
        Log.Info($"[Memory] Cleanup (aggressive={aggressive})");

        // Тяжёлую работу — в фоновый поток
        _ = Task.Run(() =>
        {
            try
            {
                // 1. Кэш картинок
                var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
                if (aggressive)
                    imageCache.ClearMemoryCache();
                else
                    imageCache.EnforceLimits();

                // 2. Кэш VM
                var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();
                vmFactory.CleanupCache();

                // 3. Буферы аудио
                var audioEngine = Program.Services.GetRequiredService<AudioEngine>();
                audioEngine.NotifyAppMinimized();

                // 4. Мёртвые ссылки в TrackRegistry
                var registry = Program.Services.GetRequiredService<TrackRegistry>();
                registry.CleanupDeadReferences();

                // 5. GC
                if (aggressive)
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                }
                else
                {
                    GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                }

                // 6. Сброс Working Set
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

    //  WINDOW CHROME

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
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
        this.PropertyChanged -= MainWindow_PropertyChanged;
        this.Deactivated -= OnWindowDeactivated;
        this.Activated -= OnWindowActivated;
        base.OnClosed(e);
    }
}