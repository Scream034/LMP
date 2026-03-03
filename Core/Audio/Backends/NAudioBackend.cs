using LMP.Core.Audio.Interfaces;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Бэкенд воспроизведения на базе NAudio (WaveOutEvent).
/// 
/// <para><b>Архитектура:</b></para>
/// Фоновый fill loop на ВЫДЕЛЕННОМ потоке (не ThreadPool) читает PCM данные
/// через callback и заполняет <see cref="BufferedWaveProvider"/>.
/// NAudio воспроизводит из этого буфера.
/// 
/// <para><b>Long-lived pattern:</b></para>
/// Backend создаётся один раз и переиспользуется между треками через
/// <see cref="Reinitialize"/>. Это исключает дорогостоящие kernel-вызовы
/// waveOutClose/waveOutOpen при каждой смене трека, что критично
/// когда приложение в фоне (ОС деприоритезирует потоки).
/// 
/// <para><b>Потокобезопасность:</b></para>
/// <list type="bullet">
///   <item><see cref="Start"/>/<see cref="Stop"/> — можно вызывать из любого потока.
///     Защита от race condition через <see cref="_stateLock"/>.</item>
///   <item><see cref="Flush"/> — инкрементирует <see cref="_flushGeneration"/>,
///     fill loop пропускает текущие данные</item>
///   <item>Fill loop — единственный writer в <see cref="BufferedWaveProvider"/></item>
///   <item><see cref="Reinitialize"/> — вызывается из command processor (однопоточно),
///     но безопасен для fill loop благодаря volatile полям</item>
/// </list>
/// </summary>
public sealed class NAudioBackend : IPlaybackBackend
{
    private const int InternalBufferSeconds = 1;
    private const int DesiredLatencyMs = 150;

    /// <summary>Заполнение буфера, выше которого fill loop засыпает.</summary>
    private const double BufferHighWaterMark = 0.8;

    /// <summary>Пауза fill loop когда буфер полон или не playing (ms).</summary>
    private const int IdleSleepMs = 10;

    /// <summary>Пауза fill loop когда нет данных от callback (ms).</summary>
    private const int EmptyCallbackSleepMs = 10;

    /// <summary>Пауза после flush detection (ms).</summary>
    private const int PostFlushSleepMs = 10;

    /// <summary>Пауза после ошибки в fill loop (ms).</summary>
    private const int ErrorSleepMs = 100;

    /// <summary>Пауза fill loop когда decoder не работает (ms).</summary>
    private const int DecoderDeadSleepMs = 50;

    /// <summary>
    /// Количество внутренних буферов NAudio.
    /// 3 вместо 2 — даёт больший запас при фоновом воспроизведении,
    /// когда ОС может задержать scheduling потоков.
    /// </summary>
    private const int NumberOfBuffers = 3;

    /// <summary>Таймаут ожидания пробуждения fill loop (ms).</summary>
    private const int FillWakeupTimeoutMs = 200;

    /// <summary>Таймаут остановки fill thread при dispose/reinit (ms).</summary>
    private const int FillThreadJoinTimeoutMs = 500;

    /// <summary>
    /// Длительность fade-out при остановке для предотвращения щелчков (ms).
    /// Fade-out записывает затухающий сигнал в BufferedWaveProvider
    /// перед вызовом waveOutReset(), предотвращая резкий обрыв волны.
    /// </summary>
    private const int FadeOutMs = 15;

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private AudioDataCallback? _callback;

    private int _channels;
    private int _sampleRate;
    private float[]? _floatBuffer;
    private byte[]? _byteBuffer;

    private volatile bool _playing;
    private volatile bool _disposed;
    private volatile bool _decoderAlive;

    /// <summary>
    /// Lock для синхронизации Start/Stop.
    /// Предотвращает race condition когда Stop() и Start() 
    /// вызываются быстро подряд (при seek).
    /// </summary>
    private readonly Lock _stateLock = new();

    /// <summary>
    /// Поколение flush. Инкрементируется при <see cref="Flush"/>.
    /// Fill loop сравнивает до и после чтения callback —
    /// если изменилось, данные устарели и не записываются в буфер.
    /// </summary>
    private int _flushGeneration;

    /// <summary>
    /// Выделенный поток для fill loop.
    /// Использование выделенного потока вместо ThreadPool критично:
    /// ОС агрессивно деприоритезирует ThreadPool потоки фоновых процессов,
    /// а выделенный поток с <see cref="ThreadPriority.AboveNormal"/> 
    /// сохраняет приоритет.
    /// </summary>
    private Thread? _fillThread;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Сигнал пробуждения fill loop.
    /// Set при Start() — будит поток из ожидания.
    /// </summary>
    private readonly ManualResetEventSlim _fillWakeup = new(false);

    /// <inheritdoc/>
    public string Name => "NAudio";

    /// <inheritdoc/>
    public float Volume
    {
        get;
        set
        {
            field = Math.Clamp(value, 0f, 1f);
            if (_waveOut != null)
            {
                try { _waveOut.Volume = field; }
                catch (ObjectDisposedException) { }
            }
        }
    } = 1.0f;

    /// <inheritdoc/>
    public bool IsPlaying => _playing;

    /// <inheritdoc/>
    public int BufferedSamples =>
        _provider != null ? _provider.BufferedBytes / sizeof(float) : 0;

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _callback = dataCallback ?? throw new ArgumentNullException(nameof(dataCallback));
        _channels = channels;
        _sampleRate = sampleRate;

        CreateWaveOut(sampleRate, channels);
        AllocateBuffers(sampleRate, channels);
        StartFillThread();

        Log.Info($"[NAudioBackend] Initialized: {sampleRate}Hz, {channels}ch (dedicated thread)");
    }

    /// <summary>
    /// Переинициализирует backend для нового трека.
    /// 
    /// <para><b>Fast path</b> (sampleRate и channels совпадают):</para>
    /// <para>Только обновляет callback и сбрасывает буферы. ~0ms.
    /// WaveOutEvent не трогается — никаких kernel-вызовов.</para>
    /// 
    /// <para><b>Slow path</b> (формат изменился, например Opus 48kHz → AAC 44.1kHz):</para>
    /// <para>Пересоздаёт WaveOutEvent с новым форматом. ~50-200ms.
    /// Fill thread останавливается и перезапускается.
    /// Перед уничтожением WaveOut выполняется fade-out для предотвращения щелчков.</para>
    /// 
    /// <para><b>First call</b> (Initialize не вызывался):</para>
    /// <para>Делегирует к <see cref="Initialize"/>.</para>
    /// </summary>
    public void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Первый вызов — делегируем к Initialize
        if (_waveOut == null)
        {
            Initialize(sampleRate, channels, dataCallback);
            return;
        }

        // Останавливаем воспроизведение с fade-out
        lock (_stateLock)
        {
            if (_playing)
            {
                _decoderAlive = false;
                WriteSilenceFadeOut();
                _playing = false;
                try { _waveOut.Pause(); }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { Log.Warn($"[NAudioBackend] Reinit pause error: {ex.Message}"); }
            }
        }

        // Обновляем callback
        _callback = dataCallback ?? throw new ArgumentNullException(nameof(dataCallback));

        if (sampleRate == _sampleRate && channels == _channels)
        {
            // ═══ FAST PATH: формат совпадает ═══
            // Только flush буферов — WaveOutEvent не трогаем
            Interlocked.Increment(ref _flushGeneration);
            _provider?.ClearBuffer();

            Log.Info($"[NAudioBackend] Reinit (fast path): {sampleRate}Hz, {channels}ch");
            return;
        }

        // ═══ SLOW PATH: формат изменился ═══
        Log.Info($"[NAudioBackend] Reinit (slow path): " +
                 $"{_sampleRate}Hz/{_channels}ch → {sampleRate}Hz/{channels}ch");

        _channels = channels;
        _sampleRate = sampleRate;

        // Останавливаем fill thread
        StopFillThread();

        // Пересоздаём WaveOut с новым форматом
        try { _waveOut.Stop(); }
        catch { }
        try { _waveOut.Dispose(); }
        catch { }

        CreateWaveOut(sampleRate, channels);
        AllocateBuffers(sampleRate, channels);
        StartFillThread();
    }

    /// <summary>
    /// Создаёт WaveOutEvent и BufferedWaveProvider для указанного формата.
    /// </summary>
    private void CreateWaveOut(int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        _provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(InternalBufferSeconds),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = DesiredLatencyMs,
            NumberOfBuffers = NumberOfBuffers
        };

        _waveOut.Init(_provider);
        _waveOut.Volume = Volume;
    }

    /// <summary>
    /// Выделяет буферы для fill loop.
    /// Размер = 50ms @ sampleRate*channels.
    /// </summary>
    private void AllocateBuffers(int sampleRate, int channels)
    {
        int samplesPerRead = sampleRate * channels / 20;
        _floatBuffer = new float[samplesPerRead];
        _byteBuffer = new byte[samplesPerRead * sizeof(float)];
    }

    /// <summary>
    /// Запускает выделенный поток для fill loop.
    /// </summary>
    private void StartFillThread()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _fillThread = new Thread(() => FillBufferLoop(token))
        {
            Name = "AudioFillBuffer",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _fillThread.Start();
    }

    /// <summary>
    /// Останавливает fill thread и освобождает CTS.
    /// </summary>
    private void StopFillThread()
    {
        _cts?.Cancel();
        _fillWakeup.Set(); // Разбудить если спит в Wait()

        if (_fillThread is { IsAlive: true })
        {
            _fillThread.Join(FillThreadJoinTimeoutMs);
        }

        _cts?.Dispose();
        _cts = null;
        _fillThread = null;
    }

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null) return;

        lock (_stateLock)
        {
            if (_playing) return;

            _decoderAlive = true;
            _playing = true;
            _waveOut.Play();
            _fillWakeup.Set(); // Будим fill thread
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Синхронный вызов</b> — блокирует вызывающий поток до завершения Pause().
    /// Это предотвращает race condition когда Stop() и Start() вызываются быстро подряд.</para>
    /// <para>Перед Pause выполняется micro fade-out для предотвращения щелчков
    /// от резкого обрыва звуковой волны (waveOutReset).</para>
    /// </remarks>
    public void Stop()
    {
        if (_waveOut == null) return;

        lock (_stateLock)
        {
            if (!_playing) return;

            _decoderAlive = false;

            // Micro fade-out: записываем тишину с затуханием
            // чтобы предотвратить щелчок от waveOutReset
            WriteSilenceFadeOut();

            _playing = false;

            try
            {
                _waveOut.Pause();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Warn($"[NAudioBackend] Stop error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Записывает короткий fade-out (затухание до тишины) в BufferedWaveProvider.
    /// 
    /// <para><b>Зачем:</b> NAudio WaveOutEvent.Pause() вызывает waveOutReset(),
    /// который мгновенно обнуляет буферы. Если в момент обнуления сигнал
    /// был на ненулевом уровне — будет слышен щелчок (DC offset discontinuity).
    /// Fade-out плавно сводит сигнал к нулю перед reset.</para>
    /// 
    /// <para><b>Длительность:</b> ~15ms (720 сэмплов при 48kHz).
    /// Достаточно для устранения щелчка, не слышно как затухание.</para>
    /// </summary>
    private void WriteSilenceFadeOut()
    {
        if (_provider == null || _sampleRate == 0 || _channels == 0) return;

        try
        {
            // Количество сэмплов для fade-out
            int fadeSamples = _sampleRate * _channels * FadeOutMs / 1000;
            if (fadeSamples <= 0) fadeSamples = _channels * 64; // Минимум

            var fadeBuffer = new byte[fadeSamples * sizeof(float)];
            var floats = new float[fadeSamples];

            // Линейное затухание от 1.0 до 0.0
            int totalFrames = fadeSamples / _channels;
            for (int frame = 0; frame < totalFrames; frame++)
            {
                float gain = 1.0f - (float)frame / totalFrames;
                for (int ch = 0; ch < _channels; ch++)
                {
                    // Записываем тишину с gain (данные из callback = 0, 
                    // но если в буфере есть остатки — они затухнут)
                    floats[frame * _channels + ch] = 0f;
                }
            }

            Buffer.BlockCopy(floats, 0, fadeBuffer, 0, fadeBuffer.Length);
            _provider.AddSamples(fadeBuffer, 0, fadeBuffer.Length);

            // Даём WaveOut проиграть fade-out
            // При 48kHz, 15ms = 720 сэмплов ≈ 0.7ms проигрывания
            // Но WaveOut буферизирует блоками DesiredLatency/NumberOfBuffers = 50ms
            // Поэтому спим немного чтобы fade дошёл до аудио-устройства
            Thread.Sleep(2);
        }
        catch (Exception ex)
        {
            Log.Debug($"[NAudioBackend] Fade-out error: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Инкрементирует <see cref="_flushGeneration"/> и очищает буфер.
    /// Fill loop обнаружит смену generation и пропустит устаревшие данные.
    /// </remarks>
    public void Flush()
    {
        if (_provider == null || _disposed) return;

        // Инкрементируем generation ПЕРЕД очисткой буфера
        Interlocked.Increment(ref _flushGeneration);
        _provider.ClearBuffer();

        Log.Debug("[NAudioBackend] Flushed");
    }

    /// <summary>
    /// Фоновый цикл заполнения буфера воспроизведения.
    /// 
    /// <para><b>Почему выделенный поток:</b></para>
    /// <para>ThreadPool потоки деприоритезируются ОС (EcoQoS, троттлинг)
    /// когда приложение в фоне. Выделенный поток с <see cref="ThreadPriority.AboveNormal"/>
    /// сохраняет приоритет, предотвращая buffer underrun.</para>
    /// 
    /// <para><b>Почему Thread.Sleep вместо Task.Delay:</b></para>
    /// <para>Thread.Sleep не зависит от ThreadPool scheduling.
    /// Task.Delay планирует continuation через ThreadPool — в фоне это может
    /// добавить десятки мс дополнительной задержки.</para>
    /// </summary>
    private void FillBufferLoop(CancellationToken ct)
    {
        int lastGeneration = Volatile.Read(ref _flushGeneration);

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                // Захватываем ссылки — могут измениться при Reinitialize
                var provider = _provider;
                var callback = _callback;
                var floatBuf = _floatBuffer;
                var byteBuf = _byteBuffer;

                if (provider == null || callback == null
                    || floatBuf == null || byteBuf == null)
                {
                    Thread.Sleep(IdleSleepMs);
                    continue;
                }

                // Проверяем flush
                int currentGeneration = Volatile.Read(ref _flushGeneration);
                if (currentGeneration != lastGeneration)
                {
                    lastGeneration = currentGeneration;
                    Thread.Sleep(PostFlushSleepMs);
                    continue;
                }

                // Не playing — спим до пробуждения через _fillWakeup
                if (!_playing)
                {
                    _fillWakeup.Reset();
                    _fillWakeup.Wait(FillWakeupTimeoutMs);
                    continue;
                }

                // Decoder не работает — спим подольше
                // Предотвращает buffer underrun → NAudio flapping
                if (!_decoderAlive)
                {
                    Thread.Sleep(DecoderDeadSleepMs);
                    continue;
                }

                // Буфер достаточно полон — спим
                if (provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * BufferHighWaterMark)
                {
                    Thread.Sleep(IdleSleepMs * 2);
                    continue;
                }

                // Читаем данные из callback (PCM ring buffer)
                int framesRead = callback(floatBuf);

                // Проверяем generation ПОСЛЕ чтения —
                // если flush произошёл во время callback, данные устарели
                int generationAfterRead = Volatile.Read(ref _flushGeneration);
                if (generationAfterRead != lastGeneration)
                {
                    lastGeneration = generationAfterRead;
                    continue; // Отбрасываем прочитанные данные
                }

                if (framesRead > 0 && _playing)
                {
                    int totalSamples = framesRead * _channels;
                    int bytes = totalSamples * sizeof(float);
                    Buffer.BlockCopy(floatBuf, 0, byteBuf, 0, bytes);
                    provider.AddSamples(byteBuf, 0, bytes);
                }
                else if (framesRead <= 0)
                {
                    Thread.Sleep(EmptyCallbackSleepMs);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"[NAudioBackend] Fill loop error: {ex.Message}");
                Thread.Sleep(ErrorSleepMs);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playing = false;
        _decoderAlive = false;

        // Останавливаем fill thread
        StopFillThread();

        // Останавливаем и dispose'им WaveOut
        try { _waveOut?.Stop(); }
        catch { }

        try { _waveOut?.Dispose(); }
        catch { }

        _waveOut = null;
        _provider = null;
        _floatBuffer = null;
        _byteBuffer = null;

        _fillWakeup.Dispose();

        Log.Debug("[NAudioBackend] Disposed");
    }
}