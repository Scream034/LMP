namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Маркерный интерфейс для всех команд аудио плеера.
/// </summary>
public interface IAudioCommand
{
    /// <summary>
    /// Уникальный ID сессии для отмены устаревших команд.
    /// </summary>
    int SessionId { get; }
}

/// <summary>
/// Команда воспроизведения.
/// </summary>
/// <param name="Url">URL аудио потока.</param>
/// <param name="TrackId">ID трека для обновления URL (опционально).</param>
/// <param name="BitrateHint">Битрейт (kbps). 0 = определить автоматически.</param>
/// <param name="SessionId">Уникальный ID сессии.</param>
/// <param name="SeekPosition">
/// Позиция для seek ПЕРЕД стартом воспроизведения (atomic seek-before-play).
/// null = начать с начала трека.
/// 
/// <para><b>Зачем:</b> При переключении качества (SwitchQualityAsync) нужно
/// начать воспроизведение с текущей позиции, а не с начала. Без этого поля
/// между PlayAsync и SeekAsync слышен артефакт — 16-300ms звука с позиции 0.</para>
/// </param>
/// <param name="ExternalCancellationToken">
/// Токен отмены пользовательской сессии воспроизведения.
/// Позволяет мгновенно прервать запуск устаревшего трека, если пользователь уже переключился.
/// </param>
public sealed record PlayCommand(
    string Url,
    string? TrackId,
    int BitrateHint,
    int SessionId,
    TimeSpan? SeekPosition = null,
    CancellationToken ExternalCancellationToken = default) : IAudioCommand;

public sealed record StopCommand(int SessionId) : IAudioCommand;

public sealed record PauseCommand(int SessionId) : IAudioCommand;

public sealed record ResumeCommand(int SessionId) : IAudioCommand;

public sealed record SeekCommand(
    TimeSpan Position,
    int SessionId,
    TaskCompletionSource<bool>? Completion = null) : IAudioCommand;

public sealed record DisposeCommand(int SessionId) : IAudioCommand;