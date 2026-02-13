using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Managed fallback бэкенд для платформ без miniaudio.
/// Использует System.Media на Windows или выводит в null device.
/// </summary>
public sealed class NullAudioBackend : IPlaybackBackend
{
    private AudioDataCallback? _callback;
    private Timer? _timer;
    private float[] _dummyBuffer = [];
    private volatile bool _isPlaying;
    
    public float Volume { get; set; } = 1.0f;
    public bool IsPlaying => _isPlaying;
    public int BufferedSamples => 0;
    
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        _callback = dataCallback;
        // 20ms буфер для симуляции
        _dummyBuffer = new float[sampleRate * channels * 20 / 1000];
    }
    
    public void Start()
    {
        if (_isPlaying || _callback == null) return;
        _isPlaying = true;
        
        // Симулируем вызовы callback каждые 20ms
        _timer = new Timer(_ =>
        {
            if (_isPlaying && _callback != null)
            {
                _callback(_dummyBuffer);
            }
        }, null, 0, 20);
    }
    
    public void Stop()
    {
        _isPlaying = false;
        _timer?.Dispose();
        _timer = null;
    }
    
    public void Dispose()
    {
        Stop();
    }
}