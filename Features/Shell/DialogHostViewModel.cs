using Avalonia.Threading;
using LMP.Core.ViewModels;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Shell;

/// <summary>
/// Контейнер для отображения диалогов поверх контента (но под TopBar и PlayerBar).
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Рендерится в MainWindow над контентом (ZIndex: 900)</item>
///   <item>TopBar и PlayerBar остаются активными</item>
///   <item>Навигационные кнопки заблокированы когда диалог открыт</item>
/// </list>
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
        lock (_lock)
        {
            if (HasActiveDialog)
            {
                Log.Warn("[DialogHost] Closing previous dialog before showing new one");
                _dialogTcs?.TrySetResult(default);
            }

            _dialogTcs = new TaskCompletionSource<object?>();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CurrentDialog = dialogContent;
            HasActiveDialog = true;
        });

        Log.Debug($"[DialogHost] Showing dialog: {dialogContent.GetType().Name}");

        var result = await _dialogTcs.Task;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HasActiveDialog = false;
            CurrentDialog = null;
        });

        Log.Debug($"[DialogHost] Dialog closed with result: {result?.GetType().Name ?? "null"}");

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