using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Material.Icons.Avalonia;

namespace LMP.Features.Shell;

public partial class MainWindow : Window
{
    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;
    private Border? _maximizeIcon;
    private Grid? _restoreIcon;

    public MainWindow()
    {
        InitializeComponent();
        
        // Подписываемся на изменение состояния окна
        this.GetObservable(WindowStateProperty).Subscribe(OnWindowStateChanged);
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