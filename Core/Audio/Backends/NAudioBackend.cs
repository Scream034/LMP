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
/// <para><b>Warmup Protocol:</b></para>
/// <para>После <see cref="Reinitialize"/> или <see cref="Flush"/>,
/// вызывающий код ДОЛЖЕН использовать:</para>
/// <code>
/// backend.ActivateFillLoop();       // Будит fill thread для наполнения буфера
/// backend.WaitForWarmup(3000);      // Ждёт ≥200ms данных
/// backend.Start();                  // waveOut.Play() — буфер уже содержит данные
/// </code>
/// <para>Это предотвращает артефакт "щётки" от чтения пустого буфера.</para>
/// 
/// <para><b>Driver Queue vs BufferedWaveProvider:</b></para>
/// <para>NAudio использует двухуровневую буферизацию:</para>
/// <list type="number">
///   <item><see cref="BufferedWaveProvider"/> — наш управляемый буфер (1 сек)</item>
///   <item>Driver Queue — буферы переданные ОС через waveOutWrite() (~150ms)</item>
/// </list>
/// <para><c>ClearBuffer()</c> очищает только уровень 1. Для очистки уровня 2
/// нужен <c>waveOut.Stop()</c> (= waveOutReset()). При смене трека <see cref="Reinitialize"/>
/// вызывает оба для полной очистки.</para>
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
    #region Constants

    private const int InternalBufferSeconds = 1;
    private const int DesiredLatencyMs = 150;

    /// <summary>Заполнение буфера, выше которого fill loop засыпает.</summary>
    private const double BufferHighWaterMark = 0.8;

    /// <summary>Пауза fill loop когда буфер полон или не активен (ms).</summary>
    private const int IdleSleepMs = 10;

    /// <summary>Пауза fill loop когда нет данных от callback (ms).</summary>
    private const int EmptyCallbackSleepMs = 5;

    /// <summary>Пауза после flush detection (ms).</summary>
    private const int PostFlushSleepMs = 10;

    /// <summary>Пауза после ошибки в fill loop (ms).</summary>
    private const int ErrorSleepMs = 100;

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
    /// </summary>
    private const int FadeOutMs = 15;

    /// <summary>
    /// Минимальное количество миллисекунд данных в BufferedWaveProvider
    /// перед разрешением Play().
    /// </summary>
    private const int WarmupMinMs = 200;

    /// <summary>
    /// Максимальное время ожидания warmup по умолчанию (ms).
    /// </summary>
    private const int WarmupDefaultTimeoutMs = 3000;

    /// <summary>
    /// Интервал проверки в WaitForWarmup (ms).
    /// </summary>
    private const int WarmupPollIntervalMs = 5;

    /// <summary>
    /// Количество последовательных underrun после которых логируется предупреждение.
    /// </summary>
    private const int UnderrunLogThreshold = 50;

    #endregion

    #region Fields

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private AudioDataCallback? _callback;

    private int _channels;
    private int _sampleRate;
    private float[]? _floatBuffer;
    private byte[]? _byteBuffer;

    /// <summary>
    /// Флаг воспроизведения. true = waveOut.Play() вызван.
    /// </summary>
    private volatile bool _playing;

    private volatile bool _disposed;

    /// <summary>
    /// Флаг активности fill loop. true = fill loop перекачивает данные.
    /// Отличие от _playing: _fillActive=true, _playing=false → warmup фаза.
    /// </summary>
    private volatile bool _fillActive;

    private readonly Lock _stateLock = new();

    /// <summary>
    /// Поколение flush. Fill loop пропускает данные при смене generation.
    /// </summary>
    private int _flushGeneration;

    private Thread? _fillThread;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Сигнал пробуждения fill loop.
    /// </summary>
    private readonly ManualResetEventSlim _fillWakeup = new(false);

    /// <summary>
    /// Счётчик последовательных underrun для диагностики.
    /// </summary>
    private int _consecutiveUnderrunCount;

    #endregion

    #region Properties

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
    public int BufferedBytes =>
        _provider?.BufferedBytes ?? 0;

    #endregion

    #region Initialize / Reinitialize

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

    /// <inheritdoc/>
    public void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Первый вызов — делегируем к Initialize
        if (_waveOut == null)
        {
            Initialize(sampleRate, channels, dataCallback);
            return;
        }

        // ═══ ПОЛНАЯ ОСТАНОВКА ═══
        // Критично для обоих путей: waveOut.Stop() вызывает waveOutReset() который
        // очищает driver queue. Без этого fast path оставляет хвост PCM от
        // предыдущего трека в driver buffers → артефакт "щётки" при Play().
        //
        // Почему waveOutReset() а не waveOutPause():
        // - waveOutPause() приостанавливает но НЕ очищает driver queue
        // - waveOutReset() останавливает И возвращает все буферы из driver queue
        // - При смене трека нам нужно ГАРАНТИРОВАТЬ что ни один сэмпл
        //   от предыдущего трека не проиграется
        lock (_stateLock)
        {
            _fillActive = false;

            if (_playing)
            {
                WriteSilenceFadeOut();
                _playing = false;
            }

            try
            {
                _waveOut.Stop(); // waveOutReset() — очищает driver queue
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Warn($"[NAudioBackend] Reinit stop error: {ex.Message}");
            }
        }

        // Обновляем callback
        _callback = dataCallback ?? throw new ArgumentNullException(nameof(dataCallback));

        // Сбрасываем underrun counter для нового трека
        Volatile.Write(ref _consecutiveUnderrunCount, 0);

        if (sampleRate == _sampleRate && channels == _channels)
        {
            // ═══ FAST PATH: формат совпадает ═══
            // WaveOut уже остановлен (waveOutReset), driver queue чист.
            // Очищаем BufferedWaveProvider и обновляем generation.
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

        // Инкрементируем generation ПЕРЕД пересозданием provider
        Interlocked.Increment(ref _flushGeneration);

        // Пересоздаём WaveOut с новым форматом
        // Stop() уже вызван выше, Dispose нужен для освобождения handle
        try { _waveOut.Dispose(); }
        catch { }

        CreateWaveOut(sampleRate, channels);
        AllocateBuffers(sampleRate, channels);
        StartFillThread();

        Log.Debug("[NAudioBackend] Reinit (slow path) complete");
    }

    #endregion

    #region Warmup Protocol

    /// <inheritdoc/>
    public void ActivateFillLoop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null) return;

        _fillActive = true;
        _fillWakeup.Set();

        Log.Debug("[NAudioBackend] Fill loop activated (warmup phase)");
    }

    /// <inheritdoc/>
    public bool WaitForWarmup(int timeoutMs = WarmupDefaultTimeoutMs)
    {
        if (_provider == null || _disposed) return false;
        if (_sampleRate == 0 || _channels == 0) return false;

        int minBytes = _sampleRate * _channels * sizeof(float) * WarmupMinMs / 1000;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_disposed) return false;

            int currentBytes = _provider.BufferedBytes;
            if (currentBytes >= minBytes)
            {
                Log.Debug($"[NAudioBackend] ✓ Warmup complete: " +
                          $"{currentBytes} bytes " +
                          $"({currentBytes * 1000 / (_sampleRate * _channels * sizeof(float))}ms) " +
                          $"in {sw.ElapsedMilliseconds}ms");
                return true;
            }

            Thread.Sleep(WarmupPollIntervalMs);
        }

        int finalBytes = _provider.BufferedBytes;
        int bytesPerMs = Math.Max(1, _sampleRate * _channels * sizeof(float) / 1000);
        Log.Warn($"[NAudioBackend] ✗ Warmup timeout after {timeoutMs}ms: " +
                 $"{finalBytes}/{minBytes} bytes " +
                 $"({finalBytes / bytesPerMs}ms/{WarmupMinMs}ms)");
        return false;
    }

    #endregion

    #region Start / Stop

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null) return;

        lock (_stateLock)
        {
            if (_playing) return;

            _fillActive = true;
            _playing = true;

            // ═══ ДИАГНОСТИКА ═══
            int bufferedBytes = _provider?.BufferedBytes ?? 0;
            int bytesPerMs = Math.Max(1, _sampleRate * _channels * sizeof(float) / 1000);
            int warmupMinBytes = bytesPerMs * WarmupMinMs;

            if (bufferedBytes < warmupMinBytes)
            {
                Log.Warn($"[NAudioBackend] ⚠ Start with LOW buffer: " +
                         $"{bufferedBytes}/{warmupMinBytes} bytes " +
                         $"({bufferedBytes / bytesPerMs}ms/{WarmupMinMs}ms). Artifact risk!");
            }
            else
            {
                Log.Debug($"[NAudioBackend] Start: buffered={bufferedBytes} bytes " +
                          $"({bufferedBytes / bytesPerMs}ms)");
            }

            _waveOut.Play();
            _fillWakeup.Set();
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_waveOut == null) return;

        lock (_stateLock)
        {
            if (!_playing) return;

            WriteSilenceFadeOut();

            _playing = false;
            _fillActive = false;

            try
            {
                // Pause для обычной паузы — сохраняет driver queue position
                // (при Resume данные продолжатся с того же места)
                _waveOut.Pause();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Warn($"[NAudioBackend] Stop error: {ex.Message}");
            }
        }
    }

    #endregion

    #region Flush

    /// <inheritdoc/>
    public void Flush()
    {
        if (_provider == null || _disposed) return;

        Interlocked.Increment(ref _flushGeneration);
        _provider.ClearBuffer();
        Volatile.Write(ref _consecutiveUnderrunCount, 0);

        Log.Debug("[NAudioBackend] Flushed");
    }

    #endregion

    #region Fill Buffer Loop

    /// <summary>
    /// Фоновый цикл заполнения буфера воспроизведения.
    /// </summary>
    private void FillBufferLoop(CancellationToken ct)
    {
        int lastGeneration = Volatile.Read(ref _flushGeneration);

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
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

                // Проверяем flush generation
                int currentGeneration = Volatile.Read(ref _flushGeneration);
                if (currentGeneration != lastGeneration)
                {
                    lastGeneration = currentGeneration;
                    Thread.Sleep(PostFlushSleepMs);
                    continue;
                }

                // Fill loop не активен — спим до пробуждения
                if (!_fillActive)
                {
                    _fillWakeup.Reset();
                    _fillWakeup.Wait(FillWakeupTimeoutMs);
                    continue;
                }

                // Буфер достаточно полон — спим
                if (provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * BufferHighWaterMark)
                {
                    Thread.Sleep(IdleSleepMs * 2);
                    continue;
                }

                // Читаем данные из callback
                int framesRead = callback(floatBuf);

                // Проверяем generation ПОСЛЕ чтения
                int generationAfterRead = Volatile.Read(ref _flushGeneration);
                if (generationAfterRead != lastGeneration)
                {
                    lastGeneration = generationAfterRead;
                    continue;
                }

                if (framesRead > 0)
                {
                    int totalSamples = framesRead * _channels;
                    int bytes = totalSamples * sizeof(float);
                    Buffer.BlockCopy(floatBuf, 0, byteBuf, 0, bytes);
                    provider.AddSamples(byteBuf, 0, bytes);

                    Volatile.Write(ref _consecutiveUnderrunCount, 0);
                }
                else
                {
                    // ═══ UNDERRUN: записываем тишину ═══
                    // Предотвращает полное опустошение BufferedWaveProvider.
                    // WaveOut с пустым буфером → audio driver discontinuity → артефакты.
                    if (_playing)
                    {
                        WriteSilenceBlock(provider, byteBuf);
                    }

                    int underruns = Interlocked.Increment(ref _consecutiveUnderrunCount);
                    if (underruns == UnderrunLogThreshold)
                    {
                        Log.Warn($"[NAudioBackend] ⚠ {underruns} consecutive underruns — " +
                                 $"decoder may be starved. BufferedMs=" +
                                 $"{(int)provider.BufferedDuration.TotalMilliseconds}");
                    }

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

    /// <summary>
    /// Записывает 10ms тишины в BufferedWaveProvider при underrun.
    /// </summary>
    private void WriteSilenceBlock(BufferedWaveProvider provider, byte[] byteBuf)
    {
        int silenceSamples = _sampleRate * _channels / 100; // 10ms
        int silenceBytes = Math.Min(silenceSamples * sizeof(float), byteBuf.Length);

        Array.Clear(byteBuf, 0, silenceBytes);

        try
        {
            provider.AddSamples(byteBuf, 0, silenceBytes);
        }
        catch (Exception ex)
        {
            Log.Debug($"[NAudioBackend] Silence write error: {ex.Message}");
        }
    }

    #endregion

    #region Fade-out

    /// <summary>
    /// Записывает fade-out (затухание) в BufferedWaveProvider перед waveOutReset.
    /// </summary>
    private void WriteSilenceFadeOut()
    {
        if (_provider == null || _sampleRate == 0 || _channels == 0) return;

        try
        {
            int fadeSamples = _sampleRate * _channels * FadeOutMs / 1000;
            if (fadeSamples <= 0) fadeSamples = _channels * 64;

            var fadeBuffer = new byte[fadeSamples * sizeof(float)];
            var floats = new float[fadeSamples];

            int totalFrames = fadeSamples / _channels;
            for (int frame = 0; frame < totalFrames; frame++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    floats[frame * _channels + ch] = 0f;
                }
            }

            Buffer.BlockCopy(floats, 0, fadeBuffer, 0, fadeBuffer.Length);
            _provider.AddSamples(fadeBuffer, 0, fadeBuffer.Length);

            Thread.Sleep(2);
        }
        catch (Exception ex)
        {
            Log.Debug($"[NAudioBackend] Fade-out error: {ex.Message}");
        }
    }

    #endregion

    #region Internal Helpers

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

    private void AllocateBuffers(int sampleRate, int channels)
    {
        int samplesPerRead = sampleRate * channels / 20; // 50ms
        _floatBuffer = new float[samplesPerRead];
        _byteBuffer = new byte[samplesPerRead * sizeof(float)];
    }

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

    private void StopFillThread()
    {
        _cts?.Cancel();
        _fillWakeup.Set();

        if (_fillThread is { IsAlive: true })
        {
            _fillThread.Join(FillThreadJoinTimeoutMs);
        }

        _cts?.Dispose();
        _cts = null;
        _fillThread = null;
    }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playing = false;
        _fillActive = false;

        StopFillThread();

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

    #endregion
}