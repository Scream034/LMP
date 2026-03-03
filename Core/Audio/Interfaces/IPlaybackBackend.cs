namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Абстракция над аудио API операционной системы (WaveOut, ALSA, PulseAudio, etc).
/// 
/// <para><b>Lifecycle:</b></para>
/// <list type="number">
///   <item><see cref="Initialize"/> — первичная инициализация (создаёт устройство, запускает fill loop)</item>
///   <item><see cref="Reinitialize"/> — переинициализация для нового трека (fast/slow path)</item>
///   <item><see cref="ActivateFillLoop"/> — активирует перекачку данных в буфер БЕЗ воспроизведения</item>
///   <item><see cref="WaitForWarmup"/> — ожидает накопления минимума данных в буфере</item>
///   <item><see cref="Start"/> — запускает воспроизведение (waveOut.Play)</item>
///   <item><see cref="Stop"/> — останавливает с micro fade-out</item>
///   <item><see cref="Flush"/> — очищает буферы (для seek)</item>
/// </list>
/// 
/// <para><b>Warmup Protocol:</b></para>
/// <para>Для предотвращения артефактов при старте трека, вызывающий код должен:</para>
/// <code>
/// backend.ActivateFillLoop();          // Fill loop начинает наполнять BufferedWaveProvider
/// backend.WaitForWarmup(timeoutMs);    // Ждём ≥200ms данных в буфере
/// backend.Start();                     // Теперь безопасно запускать waveOut.Play()
/// </code>
/// </summary>
public interface IPlaybackBackend : IDisposable
{
    /// <summary>
    /// Первичная инициализация backend.
    /// Создаёт аудио-устройство и запускает fill loop.
    /// </summary>
    void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback);

    /// <summary>
    /// Активирует fill loop для перекачки данных из callback в внутренний буфер,
    /// БЕЗ запуска воспроизведения.
    /// 
    /// <para>Вызывается перед <see cref="WaitForWarmup"/> для наполнения буфера
    /// до того, как <see cref="Start"/> запустит аудио-устройство.</para>
    /// 
    /// <para>Если fill loop уже активен — no-op.</para>
    /// </summary>
    void ActivateFillLoop();

    /// <summary>
    /// Ожидает накопления минимального количества данных во внутреннем буфере.
    /// 
    /// <para>Блокирует вызывающий поток. Используется между 
    /// <see cref="ActivateFillLoop"/> и <see cref="Start"/> для гарантии
    /// что аудио-устройство не начнёт читать из пустого буфера.</para>
    /// </summary>
    /// <param name="timeoutMs">Максимальное время ожидания (мс).</param>
    /// <returns>true если буфер прогрет до минимального уровня, false если таймаут.</returns>
    bool WaitForWarmup(int timeoutMs = 3000);

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
    /// <para>После Reinitialize backend находится в остановленном состоянии.
    /// Вызывающий код должен использовать warmup protocol:
    /// <see cref="ActivateFillLoop"/> → <see cref="WaitForWarmup"/> → <see cref="Start"/>.</para>
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

    /// <summary>
    /// Количество байт во внутреннем буфере.
    /// Используется для диагностики warmup.
    /// </summary>
    int BufferedBytes { get; }

    /// <summary>Название бэкенда для диагностики.</summary>
    string Name { get; }
}

/// <summary>
/// Callback для запроса PCM данных от бэкенда.
/// </summary>
/// <param name="buffer">Буфер для заполнения (float32, interleaved).</param>
/// <returns>Количество заполненных фреймов (samples / channels).</returns>
public delegate int AudioDataCallback(Span<float> buffer);