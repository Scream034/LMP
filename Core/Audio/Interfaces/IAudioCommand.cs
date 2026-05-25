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

/// <summary>
/// Команда полной остановки воспроизведения.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record StopCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда постановки воспроизведения на паузу.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record PauseCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда возобновления воспроизведения после паузы.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record ResumeCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда seek к новой позиции внутри текущего трека.
/// </summary>
/// <param name="Position">Целевая позиция.</param>
/// <param name="SessionId">Уникальный ID сессии.</param>
/// <param name="Completion">Опциональный completion source для awaitable seek.</param>
public sealed record SeekCommand(
    TimeSpan Position,
    int SessionId,
    TaskCompletionSource<bool>? Completion = null) : IAudioCommand;

/// <summary>
/// Команда финального уничтожения плеера и его инфраструктуры.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record DisposeCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда естественного завершения текущего трека.
/// </summary>
/// <remarks>
/// <para>Команда публикуется из decoder loop в actor channel вместо прямого вызова
/// <c>AudioPlayer.OnTrackEnded()</c> из фонового потока.</para>
/// <para>Это сохраняет single-threaded actor semantics для state machine плеера и
/// устраняет поздние stale-callback'и, которые могли сбрасывать уже новый pipeline.</para>
/// </remarks>
/// <param name="SessionId">Сессия, в рамках которой трек завершился.</param>
public sealed record TrackEndedCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда восстановления аудиоустройства после потери (BT disconnect и т.д.).
/// Pipeline остаётся живым, backend пересоздаётся через retry loop.
/// </summary>
/// <param name="SessionId">Сессия на момент отправки команды.</param>
public sealed record DeviceRecoveryCommand(int SessionId) : IAudioCommand;