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
    private volatile bool _flushing;
    private volatile bool _disposed;
    private volatile bool _fillLoopActive;
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

        Log.Info($"[NAudioBackend] Initialized: {sampleRate}Hz, {channels}ch, vol={Volume:P0}");
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

        _flushing = true;

        try
        {
            // Ждём пока fill loop завершит текущую итерацию
            int waitCount = 0;
            while (_fillLoopActive && waitCount < 50)
            {
                Thread.Sleep(5);
                waitCount++;
            }

            // Очищаем буфер
            _provider.ClearBuffer();

            // Дополнительная пауза для NAudio
            Thread.Sleep(30);
        }
        finally
        {
            _flushing = false;
        }
    }

    private async Task FillBufferLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (_provider == null || _callback == null
                    || _floatBuffer == null || _byteBuffer == null)
                    break;

                // Пропускаем если flush или не играем
                if (_flushing || !_playing)
                {
                    _fillLoopActive = false;
                    await Task.Delay(10, ct);
                    continue;
                }

                // Не переполняем буфер
                if (_provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * 0.8)
                {
                    _fillLoopActive = false;
                    await Task.Delay(20, ct);
                    continue;
                }

                _fillLoopActive = true;

                // Двойная проверка после установки флага
                if (_flushing || !_playing)
                {
                    _fillLoopActive = false;
                    continue;
                }

                int framesRead = _callback(_floatBuffer);

                // Проверяем ещё раз перед записью
                if (framesRead > 0 && !_flushing && _playing)
                {
                    int totalSamples = framesRead * _channels;
                    int bytes = totalSamples * sizeof(float);
                    Buffer.BlockCopy(_floatBuffer, 0, _byteBuffer, 0, bytes);
                    _provider.AddSamples(_byteBuffer, 0, bytes);
                }

                _fillLoopActive = false;

                if (framesRead <= 0)
                {
                    await Task.Delay(10, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"[NAudioBackend] Fill loop error: {ex.Message}");
                _fillLoopActive = false;
                await Task.Delay(100, ct);
            }
        }

        _fillLoopActive = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playing = false;
        _flushing = true;
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