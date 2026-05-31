using Avalonia.Threading;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Контейнер для отображения диалогов поверх контента.
/// </summary>
public sealed class DialogHostViewModel : ViewModelBase
{
    /// <summary>
    /// Текущий отображаемый диалог (ViewModel или UserControl).
    /// </summary>
    [Reactive] public object? CurrentDialog { get; private set; }

    /// <summary>
    /// Есть ли активный диалог.
    /// </summary>
    [Reactive] public bool HasActiveDialog { get; private set; }

    private TaskCompletionSource<object?>? _dialogTcs;
    private readonly Lock _lock = new();

    /// <summary>
    /// Показывает диалог и асинхронно ожидает результата.
    /// </summary>
    public async Task<T?> ShowAsync<T>(object dialogContent)
    {
        TaskCompletionSource<object?> tcs;
        lock (_lock)
        {
            if (HasActiveDialog)
            {
                Log.Warn($"[DialogHost] Closing previous dialog before showing new one. Current: {CurrentDialog?.GetType().Name}, New: {dialogContent.GetType().Name}");
                _dialogTcs?.TrySetResult(default);
            }

            _dialogTcs = new TaskCompletionSource<object?>();
            tcs = _dialogTcs; // Фиксируем TCS текущей сессии для предотвращения race conditions [2]
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CurrentDialog = dialogContent;
            HasActiveDialog = true;
        });

        Log.Debug($"[DialogHost] Showing dialog: {dialogContent.GetType().Name}");

        var result = await tcs.Task.ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_lock)
            {
                // Сбрасываем визуальное состояние только если за это время не открылся новый диалог
                if (_dialogTcs == tcs)
                {
                    HasActiveDialog = false;
                    CurrentDialog = null;
                }
            }
        });

        Log.Debug($"[DialogHost] Dialog closed: {dialogContent.GetType().Name} with result: {result}");

        return result is T typed ? typed : default;
    }

    /// <summary>
    /// Закрывает текущий диалог с указанным результатом.
    /// </summary>
    public void CloseDialog(object? result = null)
    {
        lock (_lock)
        {
            _dialogTcs?.TrySetResult(result);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _dialogTcs?.TrySetCanceled();
                CurrentDialog = null;
            }
        }
        base.Dispose(disposing);
    }
}