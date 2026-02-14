using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Заглушка для тестов без реального аудио вывода.
/// </summary>
public sealed class NullAudioBackend : IPlaybackBackend
{
    private AudioDataCallback? _callback;
    private int _sampleRate;
    private int _channels;
    private volatile float _volume = 1.0f;
    private volatile bool _playing;
    private volatile bool _disposed;
    
    private Task? _consumeTask;
    private CancellationTokenSource? _cts;
    
    public string Name => "Null";
    public float Volume { get => _volume; set => _volume = Math.Clamp(value, 0f, 1f); }
    public bool IsPlaying => _playing;
    public int BufferedSamples => 0;
    
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _callback = dataCallback;
        
        Log.Debug($"[NullAudioBackend] Initialized: {sampleRate}Hz, {channels}ch");
    }
    
    public void Start()
    {
        if (_playing || _callback == null) return;
        
        _playing = true;
        _cts = new CancellationTokenSource();
        _consumeTask = Task.Run(() => ConsumeLoopAsync(_cts.Token));
        
        Log.Debug("[NullAudioBackend] Started");
    }
    
    public void Stop()
    {
        _playing = false;
        _cts?.Cancel();
        
        Log.Debug("[NullAudioBackend] Stopped");
    }
    
    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        // Симулируем потребление аудио данных
        var buffer = new float[1024 * _channels];
        int samplesPerSecond = _sampleRate * _channels;
        int delayMs = 1000 * buffer.Length / samplesPerSecond;
        
        while (!ct.IsCancellationRequested && _playing)
        {
            _callback?.Invoke(buffer);
            await Task.Delay(Math.Max(delayMs, 10), ct);
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _playing = false;
        _cts?.Cancel();
        
        try
        {
            _consumeTask?.Wait(500);
        }
        catch
        {
            // Ignore
        }
        
        _cts?.Dispose();
    }
}