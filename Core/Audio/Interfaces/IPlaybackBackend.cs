namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Абстракция над аудио API операционной системы (WaveOut, ALSA, PulseAudio, etc).
/// </summary>
public interface IPlaybackBackend : IDisposable
{
    /// <summary>
    /// Первичная инициализация backend.
    /// Создаёт аудио-устройство и запускает fill loop.
    /// </summary>
    void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback);

    /// <summary>Запускает воспроизведение.</summary>
    void Start();

    /// <summary>Останавливает (приостанавливает) воспроизведение.</summary>
    void Stop();

    /// <summary>
    /// Очищает внутренний буфер. Вызывается при seek для немедленного
    /// перехода к новой позиции без проигрывания устаревших данных.
    /// </summary>
    void Flush();

    /// <summary>
    /// Переинициализирует backend для нового трека.
    /// 
    /// <para><b>Fast path</b> (формат совпадает): обновляет callback, flush буферов. ~0ms.</para>
    /// <para><b>Slow path</b> (формат изменился): пересоздаёт аудио-устройство. ~50-200ms.</para>
    /// <para><b>First call</b> (Initialize не вызывался): делегирует к Initialize.</para>
    /// 
    /// <para>Используется shared backend pattern: один backend на весь AudioPlayer,
    /// переиспользуется между треками для исключения дорогостоящих kernel-вызовов
    /// waveOutClose/waveOutOpen при каждой смене трека.</para>
    /// </summary>
    void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback);

    /// <summary>Громкость (0.0 - 1.0).</summary>
    float Volume { get; set; }

    /// <summary>Воспроизводится ли в данный момент.</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Количество семплов (samples × channels), находящихся во внутреннем буфере,
    /// которые были переданы, но ещё не воспроизведены.
    /// </summary>
    int BufferedSamples { get; }

    /// <summary>Название бэкенда для диагностики.</summary>
    string Name { get; }
}

/// <summary>
/// Callback для запроса PCM данных от бэкенда.
/// </summary>
/// <param name="buffer">Буфер для заполнения (float32, interleaved).</param>
/// <returns>Количество заполненных фреймов (samples / channels).</returns>
public delegate int AudioDataCallback(Span<float> buffer);