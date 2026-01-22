using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Views;

public partial class QueueView : UserControl
{
    private bool _isDragging;
    private Point _dragStartPoint;
    private int _draggedIndex = -1;
    private readonly ListBox? _listBox;

    public QueueView()
    {
        InitializeComponent();
        
        _listBox = this.FindControl<ListBox>("QueueListBox");
        if (_listBox != null)
        {
            _listBox.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            _listBox.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
            _listBox.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
            _listBox.AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
        _draggedIndex = -1;

        if (_listBox != null && e.Source is Control source)
        {
            var item = source.FindAncestorOfType<ListBoxItem>();
            if (item != null)
            {
                _draggedIndex = _listBox.IndexFromContainer(item);
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedIndex < 0 || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return;

        var currentPos = e.GetPosition(null);
        var diff = _dragStartPoint - currentPos;

        // Порог начала drag: 5 пикселей
        if (!_isDragging && (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5))
        {
            _isDragging = true;
            
            // Захватываем указатель для получения событий за пределами контрола
            if (e.Pointer.Captured == null)
            {
                e.Pointer.Capture(_listBox);
            }
        }

        if (_isDragging)
        {
            // Здесь можно добавить визуальную обратную связь (highlight целевой позиции)
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Освобождаем захват
        if (e.Pointer.Captured == _listBox)
        {
            e.Pointer.Capture(null);
        }

        if (!_isDragging || _draggedIndex < 0 || _listBox == null)
        {
            ResetDragState();
            return;
        }

        // Определяем целевую позицию
        var point = e.GetPosition(_listBox);
        int newIndex = GetTargetIndex(point);

        if (newIndex >= 0 && _draggedIndex != newIndex)
        {
            ExecuteMove(_draggedIndex, newIndex);
        }

        ResetDragState();
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetDragState();
    }

    private int GetTargetIndex(Point point)
    {
        if (_listBox == null) return -1;

        // Ищем элемент под курсором
        var hitResult = _listBox.GetVisualAt(point);
        var targetItem = hitResult?.FindAncestorOfType<ListBoxItem>();

        if (targetItem != null)
        {
            int index = _listBox.IndexFromContainer(targetItem);
            
            // Определяем верхнюю или нижнюю половину элемента для точной вставки
            var itemBounds = targetItem.Bounds;
            var relativeY = point.Y - itemBounds.Y;
            
            // Если курсор в нижней половине — вставляем после
            if (relativeY > itemBounds.Height / 2 && index < _listBox.ItemCount - 1)
            {
                // Если перетаскиваем сверху вниз, индекс остаётся
                // Если снизу вверх, +1
                if (_draggedIndex < index)
                    return index;
                else
                    return index;
            }
            
            return index;
        }

        // Если не попали в элемент — в конец списка
        return _listBox.ItemCount - 1;
    }

    private void ExecuteMove(int oldIndex, int newIndex)
    {
        if (DataContext is QueueViewModel vm)
        {
            vm.MoveTrackCommand.Execute((oldIndex, newIndex)).Subscribe();
        }
    }

    private void ResetDragState()
    {
        _isDragging = false;
        _draggedIndex = -1;
    }
}