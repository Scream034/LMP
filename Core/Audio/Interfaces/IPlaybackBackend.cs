namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Интерфейс бэкенда воспроизведения аудио.
/// Абстракция над аудио API операционной системы.
/// </summary>
public interface IPlaybackBackend : IDisposable
{
    /// <summary>
    /// Инициализирует аудио устройство.
    /// </summary>
    /// <param name="sampleRate">Частота дискретизации</param>
    /// <param name="channels">Количество каналов</param>
    /// <param name="dataCallback">Callback для запроса данных</param>
    void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback);
    
    /// <summary>Запускает воспроизведение</summary>
    void Start();
    
    /// <summary>Останавливает воспроизведение</summary>
    void Stop();
    
    /// <summary>Громкость (0.0 - 1.0)</summary>
    float Volume { get; set; }
    
    /// <summary>Воспроизводится ли в данный момент</summary>
    bool IsPlaying { get; }
    
    /// <summary>Количество семплов в буфере устройства</summary>
    int BufferedSamples { get; }
}

/// <summary>
/// Callback для запроса PCM данных от бэкенда.
/// </summary>
/// <param name="buffer">Буфер для заполнения PCM данными (float32, interleaved)</param>
/// <returns>Количество заполненных семплов (на канал)</returns>
public delegate int AudioDataCallback(Span<float> buffer);