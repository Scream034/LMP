using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Services;
using NAudio;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Бэкенд воспроизведения на базе NAudio (WaveOutEvent).
///
/// <para><b>Never-Stop Pattern:</b></para>
/// <c>waveOut.Play()</c> вызывается ОДИН РАЗ при инициализации и не останавливается
/// до <see cref="Dispose"/>. <see cref="BufferedWaveProvider"/> с <c>ReadFully=true</c>
/// (значение по умолчанию) отдаёт тишину когда буфер пуст — драйвер никогда
/// не видит разрыва потока. <c>waveOut.Stop()</c> вызывается ТОЛЬКО при смене
/// формата (slow path reinit) или потере устройства — это единственные неизбежные разрывы.
///
/// <para><b>Gate Pattern (управление потоком данных):</b></para>
/// <list type="bullet">
///   <item><c>_fillActive=true, _gateOpen=false</c> — fill loop СПИТ (callback не вызывается).
///     PCM данные накапливаются в ring buffer pipeline. Provider пуст → waveOut играет тишину.</item>
///   <item><c>_fillActive=true, _gateOpen=true</c> — fill loop читает callback и пишет в provider.
///     waveOut воспроизводит реальные данные.</item>
/// </list>
/// <para>Это гарантирует что к моменту <see cref="Start"/> provider содержит ТИШИНУ (пуст),
/// а ring buffer pipeline содержит реальные декодированные данные. <see cref="Start"/> открывает
/// gate + fade-in → данные сразу идут в provider без задержки.</para>
///
/// <para><b>Warmup Protocol:</b></para>
/// <para>Warmup проверяется НА УРОВНЕ PIPELINE (ring buffer), не на уровне provider.
/// <see cref="WaitForWarmup"/> является заглушкой — реальное ожидание выполняется через
/// <c>pipeline.WaitForBufferAsync()</c> в AudioPlayer.</para>
/// <code>
/// backend.ActivateFillLoop();          // _fillActive=true, _gateOpen=false → fill спит
/// await pipeline.WaitForBufferAsync(); // ждём данных в ring buffer (не в provider!)
/// backend.Start();                     // _gateOpen=true + fade-in → данные идут в provider
/// </code>
///
/// <para><b>Gain и Provider Buffer:</b></para>
/// <para>Gain применяется в pipeline AudioCallback при чтении из ring buffer.
/// Provider хранит PCM с уже применённым gain. При смене gain новый gain
/// применяется к следующему chunk (≈50ms). Уже буферизованный PCM в provider
/// (до 500ms) доиграет со старым gain. Это компромисс:
/// задержка применения gain ≤ 500ms вместо скачка позиции при flush.</para>
///
/// <para><b>Device Loss Detection:</b></para>
/// Fill loop периодически проверяет <c>waveOut.PlaybackState</c>. Если устройство пропало
/// во время воспроизведения — устанавливает <c>_deviceLost=true</c> и вызывает
/// <c>_onDeviceLost</c> callback. При следующем <see cref="Reinitialize"/> автоматически
/// уходит в slow path для пересоздания waveOut.</para>
///
/// <para><b>NAudio DesiredLatency:</b></para>
/// Суммарный размер всех waveOut буферов: размер одного = DesiredLatency / NumberOfBuffers.
/// 300ms / 3 буфера = 100ms на буфер — стабильный минимум для WaveOutEvent.
///
/// <para><b>Потокобезопасность:</b></para>
/// <list type="bullet">
///   <item>Все публичные методы защищены <see cref="_stateLock"/></item>
///   <item>Fade state (<c>_fadeGain</c>, <c>_fadingIn</c>, <c>_fadingOut</c>) —
///     читается и пишется только из fill loop (single writer), volatile для visibility</item>
///   <item>Fill loop — единственный writer в <see cref="BufferedWaveProvider"/></item>
///   <item><c>_deviceLost</c> — volatile, пишется из fill loop и читается из публичных методов</item>
/// </list>
/// </summary>
public sealed class NAudioBackend : IPlaybackBackend
{
    #region Constants

    /// <summary>
    /// Размер provider буфера в секундах.
    /// 500ms — компромисс между задержкой применения gain (≤500ms)
    /// и устойчивостью к scheduler jitter. При 1s gain задержка до 1s,
    /// при 200ms — риск underrun на слабых системах.
    /// </summary>
    private const double InternalBufferSeconds = 0.5;

    /// <summary>
    /// Суммарный размер waveOut буферов.
    /// 300ms / 3 буфера = 100ms на буфер — стабильный минимум для WaveOutEvent.
    /// Значения ≤ 100ms дают нестабильность на WASAPI shared mode.
    /// </summary>
    private const int DesiredLatencyMs = 300;

    /// <summary>Количество внутренних буферов NAudio.</summary>
    private const int NumberOfBuffers = 3;

    /// <summary>Заполнение provider, выше которого fill loop делает паузу.</summary>
    private const double BufferHighWaterMark = 0.8;

    /// <summary>Пауза fill loop когда буфер полон (ms).</summary>
    private const int IdleSleepMs = 10;

    /// <summary>Пауза fill loop когда нет данных от callback (ms).</summary>
    private const int EmptyCallbackSleepMs = 5;

    /// <summary>Пауза после flush detection (ms).</summary>
    private const int PostFlushSleepMs = 10;

    /// <summary>Пауза после ошибки в fill loop (ms).</summary>
    private const int ErrorSleepMs = 100;

    /// <summary>Таймаут ожидания пробуждения fill loop (ms).</summary>
    private const int FillWakeupTimeoutMs = 200;

    /// <summary>Таймаут остановки fill thread при dispose/reinit (ms).</summary>
    private const int FillThreadJoinTimeoutMs = 500;

    /// <summary>
    /// Длина fade envelope в фреймах (на канал).
    /// 480 frames @ 48kHz = 10ms.
    /// </summary>
    private const int FadeFrames = 480;

    /// <summary>
    /// Количество последовательных underrun после которых логируется предупреждение.
    /// </summary>
    private const int UnderrunLogThreshold = 50;

    /// <summary>
    /// Каждые N итераций fill loop проверяется состояние waveOut.
    /// ~100 итераций × 5ms = ~0.5s между проверками.
    /// </summary>
    private const int DeviceHealthCheckInterval = 100;

    /// <summary>
    /// Размер chunk для чтения из callback (доля от секунды).
    /// sampleRate * channels / ChunkDivisor = 50ms при делителе 20.
    /// </summary>
    private const int ChunkDivisor = 20;

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
    /// true = fill loop активен (может писать в provider если gate открыт).
    /// false = fill loop спит.
    /// </summary>
    private volatile bool _fillActive;

    /// <summary>
    /// true = fill loop вызывает callback и пишет данные в provider.
    /// false = fill loop спит, callback НЕ вызывается, provider пуст → тишина.
    /// Открывается только через <see cref="Start"/>,
    /// закрывается через <see cref="Stop"/>/<see cref="Flush"/>/<see cref="Reinitialize"/>.
    /// </summary>
    private volatile bool _gateOpen;

    /// <summary>
    /// true = аудиоустройство отключено.
    /// Детектируется в Volume setter и <see cref="CheckDeviceHealth"/>.
    /// При следующем <see cref="Reinitialize"/> переводит в slow path.
    /// Сбрасывается после успешного пересоздания waveOut.
    /// </summary>
    private volatile bool _deviceLost;

    private volatile bool _disposed;

    private readonly Lock _stateLock = new();

    /// <summary>Поколение flush — fill loop пропускает данные при смене generation.</summary>
    private int _flushGeneration;

    private Thread? _fillThread;
    private CancellationTokenSource? _cts;

    private readonly ManualResetEventSlim _fillWakeup = new(false);

    private int _consecutiveUnderrunCount;

    /// <summary>Счётчик итераций fill loop для периодической проверки здоровья устройства.</summary>
    private int _fillLoopIterations;

    /// <summary>
    /// Callback вызываемый когда устройство пропало во время воспроизведения.
    /// Устанавливается через <see cref="SetDeviceLostCallback"/>.
    /// </summary>
    private Action? _onDeviceLost;

    // Fade state — пишется только из fill loop (нет гонки по _fadeGain).
    // volatile для visibility из Start()/Stop().
    private float _fadeGain;
    private volatile bool _fadingIn;
    private volatile bool _fadingOut;

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
                try
                {
                    _waveOut.Volume = field;
                    _deviceLost = false;
                }
                catch (MmException ex)
                {
                    _deviceLost = true;
                    Log.Warn($"[NAudioBackend] Audio device lost: {ex.Message}");
                }
                catch (ObjectDisposedException) { }
            }
        }
    } = 1.0f;

    /// <inheritdoc/>
    public bool IsPlaying => _gateOpen && !_fadingOut;

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

        try
        {
            CreateWaveOut(sampleRate, channels);
        }
        catch (Exception ex)
        {
            _deviceLost = true;
            DisposeWaveOutSafe();
            Log.Error($"[NAudioBackend] Failed to open audio device: {ex.Message}");
            throw new AudioDeviceException(GetDeviceErrorMessage(), ex);
        }

        AllocateBuffers(sampleRate, channels);
        StartFillThread();

        try
        {
            _waveOut!.Play();
        }
        catch (Exception ex)
        {
            StopFillThread();
            _deviceLost = true;
            DisposeWaveOutSafe();
            Log.Error($"[NAudioBackend] Failed to start audio device: {ex.Message}");
            throw new AudioDeviceException(GetDeviceErrorMessage(), ex);
        }

        _deviceLost = false;
        Log.Info($"[NAudioBackend] Initialized (never-stop): {sampleRate}Hz, {channels}ch");
    }

    /// <inheritdoc/>
    public void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_waveOut == null || _deviceLost)
        {
            Initialize(sampleRate, channels, dataCallback);
            return;
        }

        _callback = dataCallback ?? throw new ArgumentNullException(nameof(dataCallback));

        bool waveOutDead;
        try
        {
            waveOutDead = _waveOut.PlaybackState == NAudio.Wave.PlaybackState.Stopped;
        }
        catch (Exception)
        {
            waveOutDead = true;
        }

        if (waveOutDead)
        {
            Log.Info("[NAudioBackend] Reinit: waveOut is stopped, forcing slow path");
            _deviceLost = true;
            Initialize(sampleRate, channels, dataCallback);
            return;
        }

        lock (_stateLock)
        {
            _fillActive = false;
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        Volatile.Write(ref _consecutiveUnderrunCount, 0);
        Interlocked.Increment(ref _flushGeneration);

        if (sampleRate == _sampleRate && channels == _channels)
        {
            _provider?.ClearBuffer();
            Log.Info($"[NAudioBackend] Reinit fast path: {sampleRate}Hz, {channels}ch");
            return;
        }

        // Slow path: смена формата → пересоздаём waveOut.
        Log.Info($"[NAudioBackend] Reinit slow path: " +
                 $"{_sampleRate}Hz/{_channels}ch → {sampleRate}Hz/{channels}ch");

        _channels = channels;
        _sampleRate = sampleRate;

        StopFillThread();

        try { _waveOut.Stop(); } catch { }
        try { _waveOut.Dispose(); } catch { }

        try
        {
            CreateWaveOut(sampleRate, channels);
        }
        catch (Exception ex)
        {
            _deviceLost = true;
            DisposeWaveOutSafe();
            Log.Error($"[NAudioBackend] Failed to recreate audio device: {ex.Message}");
            throw new AudioDeviceException(GetDeviceErrorMessage(), ex);
        }

        AllocateBuffers(sampleRate, channels);
        StartFillThread();

        try
        {
            _waveOut!.Play();
            _deviceLost = false;
        }
        catch (Exception ex)
        {
            StopFillThread();
            _deviceLost = true;
            DisposeWaveOutSafe();
            Log.Error($"[NAudioBackend] Failed to start audio device: {ex.Message}");
            throw new AudioDeviceException(GetDeviceErrorMessage(), ex);
        }

        Log.Debug("[NAudioBackend] Reinit slow path complete");
    }

    /// <summary>
    /// Устанавливает callback для уведомления о потере устройства во время воспроизведения.
    /// Вызывается из AudioPipeline сразу после Reinitialize.
    /// </summary>
    public void SetDeviceLostCallback(Action? callback)
    {
        _onDeviceLost = callback;
    }

    #endregion

    #region Warmup Protocol

    /// <inheritdoc/>
    public void ActivateFillLoop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null) return;

        lock (_stateLock)
        {
            _fillActive = true;
            _gateOpen = false; // gate закрыт: fill loop спит, callback не вызывается
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        _fillWakeup.Set();
        Log.Debug("[NAudioBackend] Fill loop activated (gate closed, provider silent)");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Заглушка — реальный warmup выполняется через <c>pipeline.WaitForBufferAsync()</c>.
    /// </remarks>
    public bool WaitForWarmup(int timeoutMs = 100)
    {
        return !_disposed && _waveOut != null;
    }

    #endregion

    #region Start / Stop

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null) return;

        if (_deviceLost)
        {
            Log.Error("[NAudioBackend] Cannot start: audio device lost");
            throw new AudioDeviceException(GetDeviceErrorMessage());
        }

        lock (_stateLock)
        {
            if (_gateOpen && !_fadingOut) return;

            _fillActive = true;
            _gateOpen = true;
            _fadingOut = false;
            _fadingIn = true;
            if (_fadeGain <= 0f) _fadeGain = 0f;
        }

        _fillWakeup.Set();
        Log.Debug("[NAudioBackend] Gate opened, fade-in started");
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_waveOut == null) return;

        lock (_stateLock)
        {
            if (!_gateOpen && !_fadingIn) return;

            // Запускаем fade-out. Fill loop завершит его и закроет gate самостоятельно.
            _fadingIn = false;
            _fadingOut = true;
        }

        Log.Debug("[NAudioBackend] Fade-out started");
    }

    #endregion

    #region Flush

    /// <inheritdoc/>
    public void Flush()
    {
        if (_provider == null || _disposed) return;

        lock (_stateLock)
        {
            _fillActive = false;
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        Interlocked.Increment(ref _flushGeneration);
        _provider.ClearBuffer();
        Volatile.Write(ref _consecutiveUnderrunCount, 0);

        Log.Debug("[NAudioBackend] Flushed");
    }

    #endregion

    #region Fill Buffer Loop

    /// <summary>
    /// Фоновый цикл заполнения провайдера.
    ///
    /// <para>Состояния:</para>
    /// <list type="bullet">
    ///   <item><c>!_fillActive</c> — спим на <see cref="_fillWakeup"/></item>
    ///   <item><c>_fillActive &amp;&amp; !_gateOpen</c> — спим, callback не вызываем.
    ///     Provider пуст → ReadFully=true → waveOut играет тишину.</item>
    ///   <item><c>_fillActive &amp;&amp; _gateOpen</c> — читаем callback, применяем
    ///     fade envelope, пишем в provider.</item>
    /// </list>
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

                if (provider == null || callback == null || floatBuf == null || byteBuf == null)
                {
                    Thread.Sleep(IdleSleepMs);
                    continue;
                }

                int currentGeneration = Volatile.Read(ref _flushGeneration);
                if (currentGeneration != lastGeneration)
                {
                    lastGeneration = currentGeneration;
                    _fadingIn = false;
                    _fadingOut = false;
                    _fadeGain = 0f;
                    Thread.Sleep(PostFlushSleepMs);
                    continue;
                }

                if (!_fillActive)
                {
                    _fillWakeup.Reset();
                    _fillWakeup.Wait(FillWakeupTimeoutMs, ct);
                    continue;
                }

                // Периодическая проверка здоровья устройства пока gate открыт
                if (_gateOpen && !_deviceLost)
                {
                    _fillLoopIterations++;
                    if (_fillLoopIterations >= DeviceHealthCheckInterval)
                    {
                        _fillLoopIterations = 0;
                        CheckDeviceHealth();
                    }
                }

                if (!_gateOpen)
                {
                    _fillWakeup.Reset();
                    _fillWakeup.Wait(FillWakeupTimeoutMs, ct);
                    continue;
                }

                if (provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * BufferHighWaterMark)
                {
                    Thread.Sleep(IdleSleepMs);
                    continue;
                }

                int framesRead = callback(floatBuf);

                // Проверяем generation после decode — seek мог произойти внутри
                int generationAfterRead = Volatile.Read(ref _flushGeneration);
                if (generationAfterRead != lastGeneration)
                {
                    lastGeneration = generationAfterRead;
                    _fadingIn = false;
                    _fadingOut = false;
                    _fadeGain = 0f;
                    continue;
                }

                if (framesRead <= 0)
                {
                    int underruns = Interlocked.Increment(ref _consecutiveUnderrunCount);
                    if (underruns == UnderrunLogThreshold)
                    {
                        Log.Warn($"[NAudioBackend] ⚠ {underruns} underruns. " +
                                 $"BufferedMs={(int)provider.BufferedDuration.TotalMilliseconds}");
                    }
                    Thread.Sleep(EmptyCallbackSleepMs);
                    continue;
                }

                Volatile.Write(ref _consecutiveUnderrunCount, 0);

                bool fadeOutDone = ApplyFadeEnvelope(floatBuf, framesRead);

                int bytes = framesRead * _channels * sizeof(float);
                Buffer.BlockCopy(floatBuf, 0, byteBuf, 0, bytes);

                try
                {
                    provider.AddSamples(byteBuf, 0, bytes);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[NAudioBackend] AddSamples failed: {ex.Message}");
                    Thread.Sleep(EmptyCallbackSleepMs);
                    continue;
                }

                if (fadeOutDone)
                {
                    lock (_stateLock)
                    {
                        _gateOpen = false;
                        _fadingOut = false;
                        _fadeGain = 0f;
                    }
                    provider.ClearBuffer();
                    Log.Debug("[NAudioBackend] Fade-out complete, gate closed");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Error($"[NAudioBackend] Fill loop error: {ex.Message}");
                Thread.Sleep(ErrorSleepMs);
            }
        }
    }

    /// <summary>
    /// Проверяет состояние waveOut во время воспроизведения.
    /// Если waveOut неожиданно остановился — устанавливает <c>_deviceLost</c>
    /// и вызывает <c>_onDeviceLost</c> callback на отдельном потоке.
    /// Вызывается только из fill loop (нет конкурентного доступа).
    /// </summary>
    private void CheckDeviceHealth()
    {
        if (_waveOut == null || _disposed) return;

        try
        {
            var state = _waveOut.PlaybackState;
            if (state == NAudio.Wave.PlaybackState.Stopped && _gateOpen && !_fadingOut)
            {
                _deviceLost = true;
                Log.Error("[NAudioBackend] Device lost during playback (waveOut stopped unexpectedly)");

                lock (_stateLock)
                {
                    _gateOpen = false;
                    _fillActive = false;
                }

                var cb = _onDeviceLost;
                if (cb != null)
                    Task.Run(cb);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Warn($"[NAudioBackend] Health check error: {ex.Message}");
        }
    }

    #endregion

    #region Fade Envelope

    /// <summary>
    /// Применяет линейный fade-in или fade-out к буферу in-place.
    /// Вызывается только из fill loop — нет конкурентного доступа к <c>_fadeGain</c>.
    /// </summary>
    /// <returns><c>true</c> если fade-out достиг нуля — fill loop должен закрыть gate.</returns>
    private bool ApplyFadeEnvelope(float[] buffer, int frames)
    {
        if (!_fadingIn && !_fadingOut)
            return false;

        float gain = _fadeGain;
        float step = 1.0f / FadeFrames;

        for (int frame = 0; frame < frames; frame++)
        {
            if (_fadingIn)
            {
                gain = MathF.Min(1f, gain + step);
                if (gain >= 1f)
                {
                    _fadingIn = false;
                    _fadeGain = 1f;
                    break;
                }
            }
            else // _fadingOut
            {
                gain = MathF.Max(0f, gain - step);
                if (gain <= 0f)
                {
                    int remainingSamples = (frames - frame) * _channels;
                    Array.Clear(buffer, frame * _channels, remainingSamples);
                    _fadeGain = 0f;
                    return true;
                }
            }

            for (int ch = 0; ch < _channels; ch++)
            {
                buffer[frame * _channels + ch] *= gain;
            }
        }

        _fadeGain = gain;
        return false;
    }

    #endregion

    #region Internal Helpers

    private void CreateWaveOut(int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        // ReadFully=true (дефолт): возвращает тишину когда буфер пуст.
        // 500ms buffer: gain задержка ≤500ms, достаточно для сглаживания jitter.
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
    /// Безопасно освобождает waveOut и provider, обнуляя ссылки.
    /// Предотвращает ситуацию когда битый waveOut остаётся доступным
    /// для fast path в <see cref="Reinitialize"/>.
    /// </summary>
    private void DisposeWaveOutSafe()
    {
        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }
        _waveOut = null;
        _provider = null;
    }

    private void AllocateBuffers(int sampleRate, int channels)
    {
        int samplesPerRead = sampleRate * channels / ChunkDivisor;
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
            _fillThread.Join(FillThreadJoinTimeoutMs);

        _cts?.Dispose();
        _cts = null;
        _fillThread = null;
    }

    private static string GetDeviceErrorMessage() =>
        LocalizationService.Instance.Get("Error_NoAudioDevice", "Audio output device is not available. Please connect headphones or speakers.");

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fillActive = false;
        _gateOpen = false;

        StopFillThread();

        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }

        _waveOut = null;
        _provider = null;
        _floatBuffer = null;
        _byteBuffer = null;

        _fillWakeup.Dispose();

        Log.Debug("[NAudioBackend] Disposed");
    }

    #endregion
}