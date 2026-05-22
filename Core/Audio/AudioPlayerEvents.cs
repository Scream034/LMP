namespace LMP.Core.Audio;

/// <summary>
/// Все события аудио плеера.
/// </summary>
public sealed class AudioPlayerEvents
{
    /// <summary>Изменилась позиция воспроизведения.</summary>
    public event Action<TimeSpan>? PositionChanged;

    /// <summary>Изменилось состояние (Playing, Paused, etc).</summary>
    public event Action<PlaybackState>? StateChanged;

    /// <summary>Трек закончился естественным образом.</summary>
    public event Action? TrackEnded;

    /// <summary>Произошла ошибка.</summary>
    public event Action<AudioPlayerError>? ErrorOccurred;

    /// <summary>Информация о потоке стала доступна или обновилась.</summary>
    public event Action<AudioStreamInfo>? StreamInfoChanged;

    /// <summary>Изменился прогресс буферизации.</summary>
    public event Action<BufferState>? BufferStateChanged;

    /// <summary>Seek завершён.</summary>
    public event Action<TimeSpan>? SeekCompleted;

    /// <summary>
    /// Аудиоустройство отключено во время воспроизведения.
    /// Плеер автоматически переведён в паузу. Pipeline жив, позиция сохранена.
    /// Используется для отображения информационного toast.
    /// </summary>
    public event Action? DeviceLost;

    /// <summary>
    /// Аудиоустройство восстановлено после потери — воспроизведение возобновлено автоматически.
    /// Используется для отображения информационного toast.
    /// </summary>
    public event Action? DeviceRestored;

    // Internal raise methods
    internal void RaisePositionChanged(TimeSpan pos) => PositionChanged?.Invoke(pos);
    internal void RaiseStateChanged(PlaybackState state) => StateChanged?.Invoke(state);
    internal void RaiseTrackEnded() => TrackEnded?.Invoke();
    internal void RaiseError(AudioPlayerError error) => ErrorOccurred?.Invoke(error);
    internal void RaiseStreamInfo(AudioStreamInfo info) => StreamInfoChanged?.Invoke(info);
    internal void RaiseBufferState(BufferState state) => BufferStateChanged?.Invoke(state);
    internal void RaiseSeekCompleted(TimeSpan pos) => SeekCompleted?.Invoke(pos);

    /// <summary>Публикует событие потери устройства (informational, не ошибка).</summary>
    internal void RaiseDeviceLost() => DeviceLost?.Invoke();

    /// <summary>Публикует событие восстановления устройства (informational).</summary>
    internal void RaiseDeviceRestored() => DeviceRestored?.Invoke();
}

public readonly record struct AudioPlayerError(string Message, Exception? Exception = null);

public readonly record struct BufferState(
    double Progress,           // 0-100%
    bool IsFullyBuffered,
    IReadOnlyList<(double Start, double End)> Ranges);