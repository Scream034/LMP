namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Единственный источник истины для определения отмены операций.
/// Заменяет три дублирующие реализации в AudioEngine, AudioPipeline и PlaybackErrorOrchestrator.
/// </summary>
public static class CancellationHelper
{
    /// <summary>
    /// Определяет, является ли исключение (или любое вложенное) отменой операции.
    /// </summary>
    /// <param name="exception">Исключение для проверки. null → false.</param>
    /// <returns>true если найден <see cref="OperationCanceledException"/> или <see cref="TaskCanceledException"/>.</returns>
    public static bool IsCancellationLike(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TaskCanceledException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Определяет, является ли исключение отменой, либо токен уже отменён.
    /// </summary>
    /// <param name="exception">Исключение для проверки.</param>
    /// <param name="ct">Токен отмены для дополнительной проверки.</param>
    /// <returns>true если токен отменён или исключение содержит отмену.</returns>
    public static bool IsCancellationOrTokenCancelled(Exception? exception, CancellationToken ct) =>
        ct.IsCancellationRequested || IsCancellationLike(exception);
}