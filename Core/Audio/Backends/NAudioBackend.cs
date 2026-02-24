using LMP.Core.Audio.Interfaces;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Бэкенд воспроизведения на базе NAudio (WaveOutEvent).
/// 
/// <para><b>Архитектура:</b></para>
/// Фоновый fill loop читает PCM данные через callback и заполняет
/// <see cref="BufferedWaveProvider"/>. NAudio воспроизводит из этого буфера.
/// 
/// <para><b>Потокобезопасность:</b></para>
/// <list type="bullet">
///   <item><see cref="Start"/>/<see cref="Stop"/> — можно вызывать из любого потока</item>
///   <item><see cref="Flush"/> — инкрементирует <see cref="_flushGeneration"/>,
///     fill loop пропускает текущие данные</item>
///   <item>Fill loop — единственный writer в <see cref="BufferedWaveProvider"/></item>
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

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private AudioDataCallback? _callback;

    private int _channels;
    private float[]? _floatBuffer;
    private byte[]? _byteBuffer;

    private volatile bool _playing;
    private volatile bool _disposed;

    /// <summary>
    /// Поколение flush. Инкрементируется при <see cref="Flush"/>.
    /// Fill loop сравнивает до и после чтения callback —
    /// если изменилось, данные устарели и не записываются в буфер.
    /// </summary>
    private int _flushGeneration;

    private Task? _fillTask;
    private CancellationTokenSource? _cts;

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

        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        _provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(InternalBufferSeconds),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = DesiredLatencyMs,
            NumberOfBuffers = 2
        };

        _waveOut.Init(_provider);
        _waveOut.Volume = Volume;

        // Буфер: 50ms @ sampleRate*channels
        int samplesPerRead = sampleRate * channels / 20;
        _floatBuffer = new float[samplesPerRead];
        _byteBuffer = new byte[samplesPerRead * sizeof(float)];

        _cts = new CancellationTokenSource();

        // ИСПРАВЛЕНИЕ: Task.Run вместо Task.Factory.StartNew
        // Task.Factory.StartNew с async lambda возвращает Task<Task>,
        // и _fillTask.Wait() ждёт только внешний Task (завершается мгновенно).
        _fillTask = Task.Run(() => FillBufferLoopAsync(_cts.Token), _cts.Token);

        Log.Info($"[NAudioBackend] Initialized: {sampleRate}Hz, {channels}ch");
    }

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null || _playing) return;

        _waveOut.Play();
        _playing = true;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_waveOut == null || !_playing) return;

        _waveOut.Pause();
        _playing = false;
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
    /// </summary>
    private async Task FillBufferLoopAsync(CancellationToken ct)
    {
        int lastGeneration = Interlocked.CompareExchange(ref _flushGeneration, 0, 0);

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (_provider == null || _callback == null
                    || _floatBuffer == null || _byteBuffer == null)
                    break;

                // Проверяем flush
                int currentGeneration = Interlocked.CompareExchange(ref _flushGeneration, 0, 0);
                if (currentGeneration != lastGeneration)
                {
                    lastGeneration = currentGeneration;
                    await Task.Delay(PostFlushSleepMs, ct);
                    continue;
                }

                // Не playing — спим
                if (!_playing)
                {
                    await Task.Delay(IdleSleepMs, ct);
                    continue;
                }

                // Буфер достаточно полон — спим
                if (_provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * BufferHighWaterMark)
                {
                    await Task.Delay(IdleSleepMs * 2, ct);
                    continue;
                }

                // Читаем данные из callback (PCM ring buffer)
                int framesRead = _callback(_floatBuffer);

                // Проверяем generation ПОСЛЕ чтения —
                // если flush произошёл во время callback, данные устарели
                int generationAfterRead = Interlocked.CompareExchange(ref _flushGeneration, 0, 0);
                if (generationAfterRead != lastGeneration)
                {
                    lastGeneration = generationAfterRead;
                    continue; // Отбрасываем прочитанные данные
                }

                if (framesRead > 0 && _playing)
                {
                    int totalSamples = framesRead * _channels;
                    int bytes = totalSamples * sizeof(float);
                    Buffer.BlockCopy(_floatBuffer, 0, _byteBuffer, 0, bytes);
                    _provider.AddSamples(_byteBuffer, 0, bytes);
                }
                else if (framesRead <= 0)
                {
                    await Task.Delay(EmptyCallbackSleepMs, ct);
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
                await Task.Delay(ErrorSleepMs, ct);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playing = false;

        // Отменяем fill loop
        _cts?.Cancel();

        if (_fillTask != null)
        {
            try { _fillTask.Wait(TimeSpan.FromSeconds(1)); }
            catch { /* Timeout OK */ }
        }

        // Останавливаем и dispose'им WaveOut
        try { _waveOut?.Stop(); }
        catch { }

        try { _waveOut?.Dispose(); }
        catch { }

        _waveOut = null;
        _provider = null;
        _floatBuffer = null;
        _byteBuffer = null;

        _cts?.Dispose();
        _cts = null;

        Log.Debug("[NAudioBackend] Disposed");
    }
}