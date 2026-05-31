using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace LMP.UI.Helpers;

/// <summary>
/// Сервис для работы с системным буфером обмена.
/// 
/// <para><b>Зачем нужен:</b></para>
/// <para>
/// В Avalonia нет статического доступа к Clipboard — он доступен только через Window.
/// Этот сервис инкапсулирует получение Clipboard из главного окна и предоставляет
/// удобный API для копирования текста.
/// </para>
/// 
/// <para><b>Использование:</b></para>
/// <code>
/// await _clipboard.SetTextAsync("https://youtube.com/...");
/// </code>
/// 
/// <para><b>Потокобезопасность:</b></para>
/// <para>
/// Методы безопасны для вызова из любого потока — внутри происходит
/// автоматическое переключение на UI-поток если необходимо.
/// </para>
/// </summary>
public static class Clipboard
{
    /// <summary>
    /// Копирует текст в системный буфер обмена.
    /// </summary>
    /// <param name="text">Текст для копирования.</param>
    /// <returns>
    /// Task, завершающийся после успешного копирования.
    /// Если главное окно недоступно — завершается без ошибки (silent fail).
    /// </returns>
    /// <remarks>
    /// <para>Метод безопасен для вызова когда окно ещё не создано или уже закрыто.</para>
    /// <para>Не выбрасывает исключений — логирует ошибки внутри.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Копирование ссылки на плейлист
    /// await _clipboard.SetTextAsync($"https://music.youtube.com/playlist?list={playlistId}");
    /// 
    /// // Показать уведомление после копирования
    /// await _notifications.ShowToastAsync(
    ///     titleKey: "Track_Copied",
    ///     messageKey: "Track_Copied",
    ///     severity: NotificationSeverity.Success);
    /// </code>
    /// </example>
    public static async Task SetTextAsync(string text)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                await SetTextInternalAsync(text);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetTextInternalAsync(text));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Clipboard] SetTextAsync failed: {ex.Message}");
        }
    }

    private static async Task SetTextInternalAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window?.Clipboard != null)
            {
                await window.Clipboard.SetTextAsync(text);
                Log.Debug($"[Clipboard] Copied {text.Length} chars");
            }
            else
            {
                Log.Warn("[Clipboard] MainWindow or Clipboard is null");
            }
        }
        else
        {
            Log.Warn("[Clipboard] Application lifetime is not desktop");
        }
    }

    /// <summary>
    /// Получает текст из системного буфера обмена.
    /// </summary>
    /// <returns>
    /// Текст из буфера обмена, или <c>null</c> если буфер пуст или недоступен.
    /// </returns>
    /// <remarks>
    /// Метод безопасен для вызова когда окно недоступно — возвращает <c>null</c>.
    /// </remarks>
    public static async Task<string?> GetTextAsync()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return await GetTextInternalAsync();
            }
            else
            {
                return await Dispatcher.UIThread.InvokeAsync(GetTextInternalAsync);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Clipboard] GetTextAsync failed: {ex.Message}");
        }

        return null;
    }

    private static async Task<string?> GetTextInternalAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window?.Clipboard != null)
            {
                return await window.Clipboard.TryGetTextAsync();
            }
        }
        return null;
    }
}