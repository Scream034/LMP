using System.Runtime.CompilerServices;
using AsyncImageLoader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LMP.Core.Helpers;

/// <summary>
/// Attached property для отложенной загрузки изображений при рециклинге ItemsRepeater.
/// 
/// Проблема: при быстром скролле ItemsRepeater рециклирует ~15-20 элементов за кадр,
/// каждый вызывает AsyncImageLoader.Source → шторм HTTP запросов + bitmap decode → фриз UI.
/// 
/// Решение: debounce загрузки. При смене URL (рециклинг) ждём пока UI thread завершит
/// layout/render (DispatcherPriority.Background), затем ещё ~80ms на случай продолжения скролла.
/// Если элемент рециклирован повторно до истечения таймера — отменяем предыдущую загрузку.
/// 
/// Результат: при быстром скролле грузятся только те изображения, на которых скролл остановился.
/// Старое изображение остаётся видимым до загрузки нового — без мигания placeholder-ов.
/// </summary>
public static class DebouncedImage
{
    /// <summary>
    /// Задержка после yield-а на Background priority.
    /// 80ms — компромисс между responsiveness (пользователь видит картинку быстро при остановке)
    /// и debounce (при 60fps = 16ms/кадр, 80ms = ~5 кадров — достаточно чтобы отфильтровать
    /// промежуточные рециклинги при быстром скролле).
    /// </summary>
    private const int DebounceMs = 80;

    private static readonly ConditionalWeakTable<Image, CancellationTokenSource> _pending = [];

    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(DebouncedImage));

    static DebouncedImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
    }

    public static string? GetSource(Image image) => image.GetValue(SourceProperty);
    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);

    private static async void OnSourceChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        // Отменяем предыдущую отложенную загрузку (элемент рециклирован повторно)
        if (_pending.TryGetValue(image, out var oldCts))
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
            _pending.Remove(image);
        }

        var url = e.NewValue as string;
        if (string.IsNullOrEmpty(url))
        {
            ImageLoader.SetSource(image, null);
            return;
        }

        var cts = new CancellationTokenSource();
        _pending.AddOrUpdate(image, cts);

        try
        {
            // Фаза 1: Yield на Background priority — даём UI thread завершить layout + render.
            // Без этого Dispatcher.UIThread блокируется декодированием bitmap ДО
            // отрисовки placeholder-ов, создавая видимый фриз.
            await Dispatcher.UIThread.InvokeAsync(
                static () => { }, DispatcherPriority.Background);

            if (cts.IsCancellationRequested) return;

            // Фаза 2: Дополнительный debounce — фильтрует промежуточные рециклинги.
            // Если скролл продолжается, элемент будет рециклирован и CTS отменён.
            await Task.Delay(DebounceMs, cts.Token);

            if (!cts.IsCancellationRequested)
                ImageLoader.SetSource(image, url);
        }
        catch (OperationCanceledException)
        {
            // Ожидаемо: элемент рециклирован до истечения debounce
        }
        finally
        {
            if (_pending.TryGetValue(image, out var current) && ReferenceEquals(current, cts))
                _pending.Remove(image);

            cts.Dispose();
        }
    }
}