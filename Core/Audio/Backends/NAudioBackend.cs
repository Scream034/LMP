// Core/Audio/Backends/NAudioBackend.cs

using LMP.Core.Audio.Interfaces;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Бэкенд на NAudio (managed, работает из коробки).
/// </summary>
public sealed class NAudioBackend : IPlaybackBackend
{
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private AudioDataCallback? _callback;
    
    private volatile float _volume = 1.0f;
    private volatile bool _playing;
    private bool _disposed;
    
    private Task? _fillTask;
    private CancellationTokenSource? _cts;
    
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_waveOut != null) _waveOut.Volume = _volume;
        }
    }
    
    public bool IsPlaying => _playing;
    public int BufferedSamples => _provider?.BufferedBytes / 4 ?? 0;
    
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        _callback = dataCallback;
        
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        
        _provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };
        
        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        
        _waveOut.Init(_provider);
        _waveOut.Volume = _volume;
        
        _cts = new CancellationTokenSource();
        _fillTask = Task.Run(() => FillBufferLoop(_cts.Token));
        
        Log.Info($"[NAudioBackend] Initialized: {sampleRate}Hz, {channels}ch");
    }
    
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (_waveOut == null || _playing) return;
        
        _waveOut.Play();
        _playing = true;
        
        Log.Debug("[NAudioBackend] Started");
    }
    
    public void Stop()
    {
        if (_waveOut == null || !_playing) return;
        
        _waveOut.Pause();
        _playing = false;
        
        Log.Debug("[NAudioBackend] Stopped");
    }
    
    private async Task FillBufferLoop(CancellationToken ct)
    {
        if (_provider == null || _callback == null) return;
        
        var buffer = new float[1024 * _provider.WaveFormat.Channels];
        var bytes = new byte[buffer.Length * sizeof(float)];
        
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                // Если буфер почти полон - ждём
                if (_provider.BufferedBytes > _provider.BufferLength / 2)
                {
                    await Task.Delay(10, ct);
                    continue;
                }
                
                // Запрашиваем данные
                int framesWritten = _callback(buffer);
                
                if (framesWritten == 0)
                {
                    await Task.Delay(10, ct);
                    continue;
                }
                
                // float[] → byte[]
                int totalSamples = framesWritten * _provider.WaveFormat.Channels;
                Buffer.BlockCopy(buffer, 0, bytes, 0, totalSamples * sizeof(float));
                
                // Добавляем в NAudio буфер
                _provider.AddSamples(bytes, 0, totalSamples * sizeof(float));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error($"[NAudioBackend] Fill error: {ex.Message}");
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
            try { _fillTask.Wait(1000); }
            catch { }
        }
        
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        
        _provider = null;
        
        _cts?.Dispose();
        _cts = null;
        
        Log.Debug("[NAudioBackend] Disposed");
    }
}