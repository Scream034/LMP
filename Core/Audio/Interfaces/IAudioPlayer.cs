namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Основной интерфейс аудио плеера.
/// Координирует source, decoder и backend для воспроизведения.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> Все публичные методы потокобезопасны.</para>
/// </remarks>
public interface IAudioPlayer : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Начинает воспроизведение с указанного URL.
    /// </summary>
    /// <param name="url">URL аудио потока</param>
    /// <param name="trackId">ID трека для обновления URL (опционально)</param>
    /// <param name="ct">Токен отмены</param>
    Task PlayAsync(string url, string? trackId = null, CancellationToken ct = default);
    
    /// <summary>Приостанавливает воспроизведение</summary>
    void Pause();
    
    /// <summary>Возобновляет воспроизведение</summary>
    void Resume();
    
    /// <summary>Останавливает воспроизведение и освобождает ресурсы трека</summary>
    void Stop();
    
    /// <summary>
    /// Перемещается к указанной позиции.
    /// </summary>
    ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default);
    
    /// <summary>Громкость (0.0 - 1.0)</summary>
    float Volume { get; set; }
    
    /// <summary>Текущая позиция воспроизведения</summary>
    TimeSpan Position { get; }
    
    /// <summary>Общая длительность трека</summary>
    TimeSpan Duration { get; }
    
    /// <summary>Текущее состояние воспроизведения</summary>
    PlaybackState State { get; }
    
    /// <summary>Событие изменения позиции (вызывается ~4 раза в секунду)</summary>
    event Action<TimeSpan>? PositionChanged;
    
    /// <summary>Событие изменения состояния</summary>
    event Action<PlaybackState>? StateChanged;
    
    /// <summary>Событие окончания трека</summary>
    event Action? TrackEnded;
    
    /// <summary>Событие ошибки</summary>
    event Action<Exception>? ErrorOccurred;
}