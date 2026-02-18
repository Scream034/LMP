using LMP.Core.Audio.Interfaces;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

public sealed class NAudioBackend : IPlaybackBackend
{
    private const int InternalBufferSeconds = 1;
    private const int DesiredLatencyMs = 150;

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private AudioDataCallback? _callback;

    private int _channels;
    private float[]? _floatBuffer;
    private byte[]? _byteBuffer;

    private volatile bool _playing;
    private volatile bool _disposed;
    
    // НЕ volatile — используем только через Interlocked
    private int _flushGeneration;
    
    private Task? _fillTask;
    private CancellationTokenSource? _cts;

    public string Name => "NAudio";

    public float Volume
    {
        get;
        set
        {
            field = Math.Clamp(value, 0f, 1f);
            if (_waveOut != null)
                _waveOut.Volume = field;
        }
    } = 1.0f;

    public bool IsPlaying => _playing;

    public int BufferedSamples =>
        _provider != null ? _provider.BufferedBytes / sizeof(float) : 0;

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

        int samplesPerRead = sampleRate * channels / 20;
        _floatBuffer = new float[samplesPerRead];
        _byteBuffer = new byte[samplesPerRead * sizeof(float)];

        _cts = new CancellationTokenSource();
        _fillTask = Task.Factory.StartNew(
            () => FillBufferLoopAsync(_cts.Token),
            TaskCreationOptions.LongRunning);

        Log.Info($"[NAudioBackend] Initialized: {sampleRate}Hz, {channels}ch");
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null || _playing) return;

        _waveOut.Play();
        _playing = true;
    }

    public void Stop()
    {
        if (_waveOut == null || !_playing) return;

        _waveOut.Pause();
        _playing = false;
    }

    public void Flush()
    {
        if (_provider == null || _disposed) return;

        // Инкрементируем generation чтобы fill loop знал что нужно пропустить текущие данные
        Interlocked.Increment(ref _flushGeneration);
        
        // Очищаем буфер
        _provider.ClearBuffer();
        
        Log.Debug("[NAudioBackend] Flushed");
    }

    private async Task FillBufferLoopAsync(CancellationToken ct)
    {
        // Читаем начальное значение через Interlocked
        int lastGeneration = Interlocked.CompareExchange(ref _flushGeneration, 0, 0);
        
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (_provider == null || _callback == null
                    || _floatBuffer == null || _byteBuffer == null)
                    break;

                // Проверяем flush generation через Interlocked
                int currentGeneration = Interlocked.CompareExchange(ref _flushGeneration, 0, 0);
                if (currentGeneration != lastGeneration)
                {
                    lastGeneration = currentGeneration;
                    await Task.Delay(10, ct);
                    continue;
                }

                if (!_playing)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                if (_provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * 0.8)
                {
                    await Task.Delay(20, ct);
                    continue;
                }

                int framesRead = _callback(_floatBuffer);
                
                // Проверяем generation ещё раз после чтения через Interlocked
                int generationAfterRead = Interlocked.CompareExchange(ref _flushGeneration, 0, 0);
                if (generationAfterRead != lastGeneration)
                {
                    lastGeneration = generationAfterRead;
                    continue;
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
                    await Task.Delay(10, ct);
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
                await Task.Delay(100, ct);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playing = false;
        _cts?.Cancel();

        if (_fillTask != null)
        {
            try { _fillTask.Wait(TimeSpan.FromSeconds(1)); }
            catch { }
        }

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _provider = null;
        _floatBuffer = null;
        _byteBuffer = null;

        _cts?.Dispose();
        _cts = null;

        Log.Debug("[NAudioBackend] Disposed");
    }
}