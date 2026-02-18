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

public sealed record PlayCommand(
    string Url,
    string? TrackId,
    int BitrateHint,
    int SessionId) : IAudioCommand;

public sealed record StopCommand(int SessionId) : IAudioCommand;

public sealed record PauseCommand(int SessionId) : IAudioCommand;

public sealed record ResumeCommand(int SessionId) : IAudioCommand;

public sealed record SeekCommand(
    TimeSpan Position,
    int SessionId,
    TaskCompletionSource<bool>? Completion = null) : IAudioCommand;

public sealed record DisposeCommand(int SessionId) : IAudioCommand;