namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Интерфейс бэкенда воспроизведения аудио.
/// Абстракция над аудио API операционной системы (WaveOut, ALSA, PulseAudio, etc).
/// </summary>
public interface IPlaybackBackend : IDisposable
{
    /// <summary>
    /// Инициализирует аудио устройство.
    /// </summary>
    /// <param name="sampleRate">Частота дискретизации</param>
    /// <param name="channels">Количество каналов</param>
    /// <param name="dataCallback">Callback для запроса PCM данных</param>
    void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback);
    
    /// <summary>Запускает воспроизведение.</summary>
    void Start();
    
    /// <summary>Останавливает (ставит на паузу) воспроизведение.</summary>
    void Stop();
    
    /// <summary>Громкость (0.0 - 1.0).</summary>
    float Volume { get; set; }
    
    /// <summary>Воспроизводится ли в данный момент.</summary>
    bool IsPlaying { get; }
    
    /// <summary>
    /// Количество семплов (samples * channels), находящихся во внутреннем буфере бэкенда,
    /// которые были переданы, но еще не воспроизведены динамиками.
    /// Используется для точной коррекции отображаемого времени.
    /// </summary>
    int BufferedSamples { get; }
    
    /// <summary>Название бэкенда для диагностики.</summary>
    string Name { get; }
}

/// <summary>
/// Callback для запроса PCM данных от бэкенда.
/// </summary>
/// <param name="buffer">Буфер для заполнения PCM данными (float32, interleaved)</param>
/// <returns>Количество заполненных фреймов (samples / channels)</returns>
public delegate int AudioDataCallback(Span<float> buffer);