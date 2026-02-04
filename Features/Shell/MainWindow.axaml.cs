using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using LMP.Core.Helpers;
using LMP.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Features.Shell;

public partial class MainWindow : Window
{
    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;
    private Border? _maximizeIcon;
    private Grid? _restoreIcon;

    private CancellationTokenSource? _cleanupCts;

    public MainWindow()
    {
        InitializeComponent();

        // 1. Отслеживаем состояние окна (Свернуть/Развернуть)
        this.PropertyChanged += MainWindow_PropertyChanged;

        // 2. Отслеживаем потерю и получение фокуса (Alt-Tab)
        this.Deactivated += OnWindowDeactivated;
        this.Activated += OnWindowActivated;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        // Находим элементы управления
        _minimizeButton = this.FindControl<Button>("MinimizeButton");
        _maximizeButton = this.FindControl<Button>("MaximizeButton");
        _closeButton = this.FindControl<Button>("CloseButton");
        _dragArea = this.FindControl<Border>("DragArea");
        _maximizeIcon = this.FindControl<Border>("MaximizeIcon");
        _restoreIcon = this.FindControl<Grid>("RestoreIcon");

        // Привязываем обработчики
        _minimizeButton?.Click += (_, _) => WindowState = WindowState.Minimized;

        _maximizeButton?.Click += (_, _) => ToggleMaximize();

        _closeButton?.Click += (_, _) => Close();

        // Перетаскивание окна за title bar
        var titleBar = this.FindControl<Grid>("TitleBar");
        titleBar?.PointerPressed += OnTitleBarPointerPressed;

        // Двойной клик для maximize/restore
        _dragArea?.DoubleTapped += (_, _) => ToggleMaximize();
    }

    private void MainWindow_PropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            var state = (WindowState)e.NewValue!;
            if (state == WindowState.Minimized)
            {
                // При явном сворачивании чистим быстро (через 200мс)
                ScheduleCleanup(TimeSpan.FromMilliseconds(200));
            }
            else
            {
                // Если развернули - отменяем очистку (если она еще не прошла)
                CancelCleanup();
            }
        }
    }


    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Пользователь переключился на другое окно.
        // Ждем 60 секунд. Если не вернется — чистим память.
        // Если музыка играет, это безопасно.
        ScheduleCleanup(TimeSpan.FromMinutes(1));
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // Пользователь вернулся в окно.
        // Срочно отменяем запланированную очистку!
        CancelCleanup();
    }

    private void ScheduleCleanup(TimeSpan delay)
    {
        // Отменяем предыдущую задачу, если была
        CancelCleanup();
        _cleanupCts = new CancellationTokenSource();

        var token = _cleanupCts.Token;

        Task.Run(async () =>
        {
            try
            {
                // Ждем указанное время
                await Task.Delay(delay, token);

                // Если токен отменили пока мы ждали - выходим
                if (token.IsCancellationRequested) return;

                // Запускаем очистку
                PerformDeepCleanup();
            }
            catch (OperationCanceledException)
            {
                // Игнорируем отмену
            }
        });
    }

    private void PerformDeepCleanup()
    {
        // Логируем для отладки (потом можно убрать)
        Log.Info("[Memory] Triggering background cleanup...");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Получаем сервисы (безопасно делать это в UI потоке или через Program.Services)
            // Но саму тяжелую работу делаем в Task.Run

            Task.Run(() =>
            {
                // 1. Сбрасываем кэш картинок
                var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
                imageCache.ClearMemoryCache();

                // 2. Сбрасываем кэш VM
                var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();
                vmFactory.CleanupCache();

                // 3. Сбрасываем буферы аудио (если играет)
                var audioEngine = Program.Services.GetRequiredService<AudioEngine>();
                audioEngine.NotifyAppMinimized();

                // 4. Жесткий GC
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);

                // 5. Сброс Working Set OS
                MemoryHelpers.TrimWorkingSet();
            });
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

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Проверяем, что это не клик по кнопкам
        if (e.Source is Button) return;

        // Начинаем перетаскивание окна
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged(WindowState state)
    {
        // Переключаем иконки maximize/restore
        if (_maximizeIcon != null && _restoreIcon != null)
        {
            var isMaximized = state == WindowState.Maximized;
            _maximizeIcon.IsVisible = !isMaximized;
            _restoreIcon.IsVisible = isMaximized;
        }

        // Обновляем тултип кнопки maximize
        if (_maximizeButton != null && DataContext is MainWindowViewModel vm)
        {
            var key = state == WindowState.Maximized ? "Window_Restore" : "Window_Maximize";
            ToolTip.SetTip(_maximizeButton, vm.L[key]);
        }
    }
}