using NAudio.Wave;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

public class AudioEngine : IDisposable
{
    private readonly IWavePlayer _outputDevice;
    private MediaFoundationReader? _audioReader;
    
    // Блокировка для синхронизации потоков (UI и Audio Thread)
    private readonly object _lock = new();
    
    private readonly Queue<TrackInfo> _playlist = new();
    public TrackInfo? CurrentTrack { get; private set; }
    
    public event Action<TrackInfo>? OnTrackChanged;
    public event Action? OnPlaybackStopped;

    private float _volume = 0.5f;

    public AudioEngine()
    {
        _outputDevice = new WaveOutEvent();
        _outputDevice.PlaybackStopped += OnDevicePlaybackStopped;
    }

    public void PlayTrack(TrackInfo track)
    {
        lock (_lock)
        {
            StopInternal(false); // Останавливаем, но не очищаем событие PlaybackStopped

            try
            {
                CurrentTrack = track;
                // MediaFoundationReader лучше для стриминга URL (поддерживает HTTPS)
                _audioReader = new MediaFoundationReader(track.StreamUrl);
                _outputDevice.Init(_audioReader);
                _outputDevice.Volume = _volume;
                _outputDevice.Play();

                OnTrackChanged?.Invoke(CurrentTrack);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio Error: {ex.Message}");
                // Пытаемся играть следующий, если этот битый
                PlayNext(); 
            }
        }
    }

    public void Enqueue(TrackInfo track)
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState != PlaybackState.Playing && CurrentTrack == null)
            {
                PlayTrack(track);
            }
            else
            {
                _playlist.Enqueue(track);
            }
        }
    }

    public void PlayNext()
    {
        lock (_lock)
        {
            if (_playlist.TryDequeue(out var next))
            {
                PlayTrack(next);
            }
            else
            {
                StopInternal(false);
                CurrentTrack = null;
                OnPlaybackStopped?.Invoke();
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopInternal(true);
        }
    }

    private void StopInternal(bool clearCurrent)
    {
        _outputDevice.Stop();
        if (_audioReader != null)
        {
            _audioReader.Dispose();
            _audioReader = null;
        }
        if (clearCurrent) CurrentTrack = null;
    }

    public void SetVolume(float vol)
    {
        _volume = Math.Clamp(vol, 0f, 1f);
        _outputDevice.Volume = _volume;
    }

    // Возвращаем копию списка для UI, чтобы избежать ошибок "Collection was modified"
    public List<TrackInfo> GetPlaylistCopy()
    {
        lock (_lock)
        {
            return _playlist.ToList();
        }
    }

    private void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Если ошибка потока (e.Exception != null), можно логировать
        // Автоматически переходим к следующему треку
        // PlayNext содержит лок, так что это безопасно
        if (CurrentTrack != null) // Если трек был, но остановился сам (конец песни)
        {
            PlayNext();
        }
    }

    public void Dispose()
    {
        _outputDevice.PlaybackStopped -= OnDevicePlaybackStopped;
        _outputDevice.Dispose();
        _audioReader?.Dispose();
        GC.SuppressFinalize(this);
    }
}