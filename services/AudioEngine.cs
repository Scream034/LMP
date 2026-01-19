using NAudio.Wave;
using MyLiteMusicPlayer.Models;
using PlaybackState = NAudio.Wave.PlaybackState;

namespace MyLiteMusicPlayer.Services;

public class AudioEngine : IDisposable
{
    private readonly IWavePlayer _outputDevice;
    private readonly YoutubeProvider _youtube;
    private MediaFoundationReader? _audioReader;
    private readonly object _lock = new();

    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = new();
    private int _historyIndex = -1;
    
    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying => _outputDevice.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _outputDevice.PlaybackState == PlaybackState.Paused;
    
    public TimeSpan CurrentPosition
    {
        get
        {
            lock (_lock)
            {
                return _audioReader?.CurrentTime ?? TimeSpan.Zero;
            }
        }
    }
    
    public TimeSpan TotalDuration
    {
        get
        {
            lock (_lock)
            {
                return _audioReader?.TotalTime ?? CurrentTrack?.Duration ?? TimeSpan.Zero;
            }
        }
    }

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    public event Action<TrackInfo>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;

    private float _volume = 0.5f;

    public AudioEngine(YoutubeProvider youtube)
    {
        _youtube = youtube;
        _outputDevice = new WaveOutEvent();
        _outputDevice.PlaybackStopped += OnDevicePlaybackStopped;
    }

    public void PlayTrack(TrackInfo track)
    {
        lock (_lock)
        {
            StopInternal(false);

            try
            {
                // Добавляем в историю
                if (CurrentTrack != null)
                {
                    // Обрезаем историю если были переходы назад
                    if (_historyIndex < _history.Count - 1)
                    {
                        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                    }
                    _history.Add(CurrentTrack);
                }
                
                CurrentTrack = track;
                _historyIndex = _history.Count;
                
                _audioReader = new MediaFoundationReader(track.StreamUrl);
                _outputDevice.Init(_audioReader);
                _outputDevice.Volume = _volume;
                _outputDevice.Play();

                OnTrackChanged?.Invoke(CurrentTrack);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio Error: {ex.Message}");
                OnError?.Invoke($"Не удалось воспроизвести: {ex.Message}");
                
                // Пробуем обновить URL и повторить
                Task.Run(async () =>
                {
                    var newUrl = await _youtube.RefreshStreamUrlAsync(track);
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        track.StreamUrl = newUrl;
                        PlayTrack(track);
                    }
                    else
                    {
                        PlayNext();
                    }
                });
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
                _queue.Enqueue(track);
            }
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        lock (_lock)
        {
            foreach (var track in tracks)
            {
                _queue.Enqueue(track);
            }
            
            if (CurrentTrack == null && _queue.Count > 0)
            {
                PlayTrack(_queue.Dequeue());
            }
        }
    }

    public void ClearQueue()
    {
        lock (_lock)
        {
            _queue.Clear();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState == PlaybackState.Playing)
                _outputDevice.Pause();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState == PlaybackState.Paused)
                _outputDevice.Play();
            else if (CurrentTrack != null && _outputDevice.PlaybackState == PlaybackState.Stopped)
                PlayTrack(CurrentTrack);
        }
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
            Pause();
        else
            Resume();
    }

    public void PlayNext()
    {
        lock (_lock)
        {
            if (RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
            {
                Seek(TimeSpan.Zero);
                Resume();
                return;
            }
            
            TrackInfo? next = null;
            
            if (ShuffleEnabled && _queue.Count > 1)
            {
                var list = _queue.ToList();
                var randomIndex = Random.Shared.Next(list.Count);
                next = list[randomIndex];
                list.RemoveAt(randomIndex);
                _queue.Clear();
                foreach (var t in list) _queue.Enqueue(t);
            }
            else if (_queue.TryDequeue(out var queued))
            {
                next = queued;
            }
            
            if (next != null)
            {
                PlayTrack(next);
            }
            else if (RepeatMode == RepeatMode.RepeatAll && _history.Count > 0)
            {
                // Переигрываем историю
                foreach (var track in _history)
                    _queue.Enqueue(track);
                _history.Clear();
                _historyIndex = -1;
                
                if (_queue.TryDequeue(out next))
                    PlayTrack(next);
            }
            else
            {
                StopInternal(true);
                OnPlaybackStopped?.Invoke();
            }
        }
    }

    public void PlayPrevious()
    {
        lock (_lock)
        {
            // Если прошло больше 3 секунд - перемотка в начало
            if (_audioReader != null && _audioReader.CurrentTime.TotalSeconds > 3)
            {
                Seek(TimeSpan.Zero);
                return;
            }
            
            if (_historyIndex > 0)
            {
                _historyIndex--;
                var previousTrack = _history[_historyIndex];
                
                // Возвращаем текущий трек в очередь
                if (CurrentTrack != null)
                {
                    var tempQueue = new Queue<TrackInfo>();
                    tempQueue.Enqueue(CurrentTrack);
                    while (_queue.TryDequeue(out var t))
                        tempQueue.Enqueue(t);
                    _queue.Clear();
                    while (tempQueue.TryDequeue(out var t))
                        _queue.Enqueue(t);
                }
                
                CurrentTrack = previousTrack;
                
                StopInternal(false);
                
                try
                {
                    _audioReader = new MediaFoundationReader(previousTrack.StreamUrl);
                    _outputDevice.Init(_audioReader);
                    _outputDevice.Volume = _volume;
                    _outputDevice.Play();
                    
                    OnTrackChanged?.Invoke(CurrentTrack);
                }
                catch
                {
                    PlayNext();
                }
            }
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_audioReader != null && _audioReader.CanSeek)
            {
                try
                {
                    _audioReader.CurrentTime = position;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Seek error: {ex.Message}");
                }
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
        
        if (clearCurrent) 
            CurrentTrack = null;
    }

    public void SetVolume(float vol)
    {
        _volume = Math.Clamp(vol, 0f, 1f);
        _outputDevice.Volume = _volume;
    }

    public float GetVolume() => _volume;

    public List<TrackInfo> GetQueueCopy()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }

    public int QueueCount
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    private void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Console.WriteLine($"Playback error: {e.Exception.Message}");
            OnError?.Invoke(e.Exception.Message);
        }
        
        if (CurrentTrack != null)
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