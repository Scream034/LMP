using NAudio.Wave;
using MyLiteMusicPlayer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using PlaybackState = NAudio.Wave.PlaybackState;

namespace MyLiteMusicPlayer.Services;

public class AudioEngine : IDisposable
{
    private readonly IWavePlayer _outputDevice;
    private readonly YoutubeProvider _youtube;
    private readonly HttpClient _httpClient;
    private readonly string _cacheFolder;

    private MediaFoundationReader? _audioReader;
    private readonly object _lock = new();

    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = new();
    private int _historyIndex = -1;

    private bool _isManualStop = false;
    private Guid _currentSessionId;
    
    // Счётчик попыток для retry-логики
    private int _retryCount = 0;
    private const int MaxRetries = 2;

    public TrackInfo? CurrentTrack { get; private set; }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnLoadingChanged?.Invoke(value);
            }
        }
    }

    public bool IsPlaying => _outputDevice.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _outputDevice.PlaybackState == PlaybackState.Paused;

    public TimeSpan CurrentPosition
    {
        get
        {
            lock (_lock)
            {
                try { return _audioReader?.CurrentTime ?? TimeSpan.Zero; }
                catch { return TimeSpan.Zero; }
            }
        }
    }

    public TimeSpan TotalDuration
    {
        get
        {
            lock (_lock)
            {
                try { return _audioReader?.TotalTime ?? CurrentTrack?.Duration ?? TimeSpan.Zero; }
                catch { return CurrentTrack?.Duration ?? TimeSpan.Zero; }
            }
        }
    }

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;  // Изменили на nullable
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;

    private float _volume = 0.5f;

    public AudioEngine(YoutubeProvider youtube)
    {
        _youtube = youtube;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "Cache");
        Directory.CreateDirectory(_cacheFolder);

        _outputDevice = new WaveOutEvent
        {
            DesiredLatency = 500,
            NumberOfBuffers = 4
        };
        _outputDevice.PlaybackStopped += OnDevicePlaybackStopped;

        Debug.WriteLine($"[AudioEngine] Initialized. Cache: {_cacheFolder}");
    }

    public async Task PlayTrackAsync(TrackInfo track, bool isRetry = false)
    {
        if (track == null)
        {
            Debug.WriteLine("[AudioEngine] PlayTrackAsync: track is null!");
            return;
        }

        var mySessionId = Guid.NewGuid();
        _currentSessionId = mySessionId;
        
        if (!isRetry)
        {
            _retryCount = 0;
        }

        IsLoading = true;
        CurrentTrack = track;
        OnTrackChanged?.Invoke(CurrentTrack);

        try
        {
            Debug.WriteLine($"[AudioEngine] ========================================");
            Debug.WriteLine($"[AudioEngine] >>> PLAY REQUEST <<< (Retry: {_retryCount})");
            Debug.WriteLine($"[AudioEngine] Title: '{track.Title}'");
            Debug.WriteLine($"[AudioEngine] ID: '{track.Id}'");
            Debug.WriteLine($"[AudioEngine] Author: '{track.Author}'");

            lock (_lock)
            {
                _isManualStop = true;
                StopInternal(false);
                _isManualStop = false;
            }

            Debug.WriteLine($"[AudioEngine] Requesting fresh stream URL...");
            var freshUrl = await _youtube.RefreshStreamUrlAsync(track, useAlternativeFormat: _retryCount > 0);

            if (_currentSessionId != mySessionId)
            {
                Debug.WriteLine("[AudioEngine] Session cancelled during URL fetch");
                return;
            }

            if (string.IsNullOrEmpty(freshUrl))
            {
                throw new Exception($"Could not resolve audio stream.");
            }

            Debug.WriteLine($"[AudioEngine] Fresh URL obtained. Length: {freshUrl.Length}");
            Debug.WriteLine($"[AudioEngine] URL preview: {freshUrl.Substring(0, Math.Min(150, freshUrl.Length))}...");

            await StartPlaybackAsync(freshUrl, mySessionId);

            if (_currentSessionId == mySessionId && _outputDevice.PlaybackState == PlaybackState.Playing)
            {
                _retryCount = 0; // Сбрасываем при успехе
                AddToHistory(track);
                Debug.WriteLine($"[AudioEngine] === PLAYBACK STARTED: '{track.Title}' ===");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] PLAYBACK FAILED: {ex.Message}");
            Debug.WriteLine($"[AudioEngine] Stack: {ex.StackTrace}");
            
            // Retry с альтернативным форматом
            if (_retryCount < MaxRetries && _currentSessionId == mySessionId)
            {
                _retryCount++;
                Debug.WriteLine($"[AudioEngine] Retrying with alternative format ({_retryCount}/{MaxRetries})...");
                IsLoading = false;
                await PlayTrackAsync(track, isRetry: true);
                return;
            }
            
            OnError?.Invoke($"Error: {ex.Message}");
            if (_currentSessionId == mySessionId) StopInternal(true);
        }
        finally
        {
            if (_currentSessionId == mySessionId) IsLoading = false;
        }
    }

    private async Task StartPlaybackAsync(string url, Guid sessionId)
    {
        MediaFoundationReader? newReader = null;
        Exception? readerException = null;

        await Task.Run(() =>
        {
            if (_currentSessionId != sessionId) return;

            try
            {
                Debug.WriteLine("[AudioEngine] Creating MediaFoundationReader...");
                newReader = new MediaFoundationReader(url);
                Debug.WriteLine($"[AudioEngine] Reader created. Format: {newReader.WaveFormat}");
                Debug.WriteLine($"[AudioEngine] Duration: {newReader.TotalTime}");
                Debug.WriteLine($"[AudioEngine] CanSeek: {newReader.CanSeek}");
                Debug.WriteLine($"[AudioEngine] Length: {newReader.Length} bytes");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Reader creation failed: {ex.Message}");
                Debug.WriteLine($"[AudioEngine] Exception type: {ex.GetType().Name}");
                readerException = ex;
            }
        });

        if (_currentSessionId != sessionId)
        {
            newReader?.Dispose();
            return;
        }

        if (readerException != null || newReader == null)
        {
            throw new Exception($"Failed to open audio stream: {readerException?.Message ?? "Unknown error"}");
        }

        lock (_lock)
        {
            _audioReader = newReader;
            _outputDevice.Init(_audioReader);
            _outputDevice.Volume = _volume;
            _outputDevice.Play();
        }

        Debug.WriteLine("[AudioEngine] Playback started, waiting for buffer...");
        await Task.Delay(500); // Увеличили время буферизации

        if (_currentSessionId != sessionId) return;

        // Проверяем позицию после буферизации
        TimeSpan posAfterBuffer;
        lock (_lock)
        {
            try { posAfterBuffer = _audioReader?.CurrentTime ?? TimeSpan.Zero; }
            catch { posAfterBuffer = TimeSpan.Zero; }
        }
        
        Debug.WriteLine($"[AudioEngine] Position after buffer: {posAfterBuffer}");
        Debug.WriteLine($"[AudioEngine] Playback state: {_outputDevice.PlaybackState}");

        if (_outputDevice.PlaybackState == PlaybackState.Stopped && !_isManualStop)
        {
            Debug.WriteLine("[AudioEngine] Playback stopped immediately - stream may be broken");
            throw new Exception("Playback failed to start - stream not playable");
        }
    }

    public void PlayTrack(TrackInfo track) => _ = PlayTrackAsync(track);

    private void AddToHistory(TrackInfo track)
    {
        if (_history.Count > 0 && _history.Last().Id == track.Id) return;
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(track);
        _historyIndex = _history.Count - 1;
    }

    public void Enqueue(TrackInfo track)
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState != PlaybackState.Playing && CurrentTrack == null && !IsLoading)
                _ = PlayTrackAsync(track);
            else
                _queue.Enqueue(track);
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        lock (_lock)
        {
            foreach (var track in tracks) _queue.Enqueue(track);
            if (CurrentTrack == null && _queue.Count > 0 && !IsLoading)
                _ = PlayTrackAsync(_queue.Dequeue());
        }
    }

    public void ClearQueue() { lock (_lock) _queue.Clear(); }

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
            else if (CurrentTrack != null && _outputDevice.PlaybackState == PlaybackState.Stopped && !IsLoading)
                _ = PlayTrackAsync(CurrentTrack);
        }
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Resume();
    }

    public void Stop()
    {
        Debug.WriteLine("[AudioEngine] Stop() called");
        lock (_lock)
        {
            _isManualStop = true;
            _currentSessionId = Guid.NewGuid();
            StopInternal(true);
            _isManualStop = false;
        }
    }

    public void SetVolume(float vol)
    {
        _volume = Math.Clamp(vol, 0f, 1f);
        try { _outputDevice.Volume = _volume; } catch { }
    }

    public float GetVolume() => _volume;

    private void StopInternal(bool clearCurrent)
    {
        try
        {
            if (_outputDevice.PlaybackState != PlaybackState.Stopped)
                _outputDevice.Stop();
        }
        catch { }

        try
        {
            _audioReader?.Dispose();
            _audioReader = null;
        }
        catch { }

        if (clearCurrent)
        {
            CurrentTrack = null;
            OnTrackChanged?.Invoke(null); // Явно передаём null
        }
    }

    public async Task PlayNextAsync()
    {
        Debug.WriteLine("[AudioEngine] PlayNextAsync called");

        TrackInfo? next = null;
        lock (_lock)
        {
            if (RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
            {
                try
                {
                    if (_audioReader != null && _audioReader.CanSeek)
                        _audioReader.CurrentTime = TimeSpan.Zero;
                    if (_outputDevice.PlaybackState != PlaybackState.Playing)
                        _outputDevice.Play();
                }
                catch { }
                return;
            }

            if (ShuffleEnabled && _queue.Count > 1)
            {
                var list = _queue.ToList();
                var nextIndex = Random.Shared.Next(list.Count);
                next = list[nextIndex];
                list.RemoveAt(nextIndex);
                _queue.Clear();
                foreach (var t in list) _queue.Enqueue(t);
            }
            else if (_queue.TryDequeue(out var queued))
            {
                next = queued;
            }
            else if (RepeatMode == RepeatMode.RepeatAll && _history.Count > 0)
            {
                foreach (var track in _history) _queue.Enqueue(track);
                if (_queue.TryDequeue(out var first)) next = first;
            }
        }

        if (next != null)
        {
            await PlayTrackAsync(next);
        }
        else
        {
            Debug.WriteLine("[AudioEngine] No next track, stopping...");
            lock (_lock)
            {
                _isManualStop = true;
                StopInternal(true);
                _isManualStop = false;
            }
            OnPlaybackStopped?.Invoke();
        }
    }

    public void PlayNext() => _ = PlayNextAsync();

    public async Task PlayPreviousAsync()
    {
        TrackInfo? prev = null;
        lock (_lock)
        {
            if (_historyIndex > 0)
            {
                _historyIndex--;
                prev = _history[_historyIndex];
            }
        }
        if (prev != null) await PlayTrackAsync(prev);
    }

    public void PlayPrevious() => _ = PlayPreviousAsync();

    private async void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Debug.WriteLine($"[AudioEngine] === OnDevicePlaybackStopped ===");
        Debug.WriteLine($"[AudioEngine] IsManualStop: {_isManualStop}, IsLoading: {IsLoading}");

        if (e.Exception != null)
        {
            Debug.WriteLine($"[AudioEngine] !!! EXCEPTION IN PLAYBACK !!!");
            Debug.WriteLine($"[AudioEngine] Exception Type: {e.Exception.GetType().Name}");
            Debug.WriteLine($"[AudioEngine] Exception Message: {e.Exception.Message}");
            Debug.WriteLine($"[AudioEngine] Exception Stack: {e.Exception.StackTrace}");
            
            if (e.Exception.InnerException != null)
            {
                Debug.WriteLine($"[AudioEngine] Inner Exception: {e.Exception.InnerException.Message}");
            }
            
            OnError?.Invoke($"Playback Error: {e.Exception.Message}");
        }

        if (_isManualStop || IsLoading)
        {
            Debug.WriteLine("[AudioEngine] Ignoring stop event (manual stop or loading)");
            return;
        }

        TimeSpan pos = TimeSpan.Zero;
        TimeSpan dur = TimeSpan.Zero;

        lock (_lock)
        {
            try
            {
                if (_audioReader != null)
                {
                    pos = _audioReader.CurrentTime;
                    dur = _audioReader.TotalTime;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Error reading position: {ex.Message}");
            }

            if (dur == TimeSpan.Zero && CurrentTrack != null)
                dur = CurrentTrack.Duration;
        }

        Debug.WriteLine($"[AudioEngine] Position: {pos}, Duration: {dur}");

        // Если остановились в самом начале - это ошибка потока
        if (pos.TotalSeconds < 1.0 && dur.TotalSeconds > 5)
        {
            Debug.WriteLine("[AudioEngine] Stream error (stopped at beginning)");
            
            // Пробуем retry с альтернативным форматом
            if (_retryCount < MaxRetries && CurrentTrack != null)
            {
                _retryCount++;
                Debug.WriteLine($"[AudioEngine] Attempting retry {_retryCount}/{MaxRetries}...");
                await Task.Delay(500);
                await PlayTrackAsync(CurrentTrack, isRetry: true);
                return;
            }
            
            Debug.WriteLine("[AudioEngine] Max retries reached, skipping to next...");
            await Task.Delay(300);
            await PlayNextAsync();
            return;
        }

        bool isNearEnd = dur.TotalSeconds > 1 && (dur - pos).TotalSeconds < 3;

        if (CurrentTrack != null && (isNearEnd || dur.TotalSeconds <= 1))
        {
            Debug.WriteLine("[AudioEngine] Track finished naturally, playing next...");
            await PlayNextAsync();
        }
        else
        {
            Debug.WriteLine("[AudioEngine] Unexpected stop");
            OnPlaybackStopped?.Invoke();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            try
            {
                if (_audioReader?.CanSeek == true)
                {
                    position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, TotalDuration.TotalSeconds));
                    _audioReader.CurrentTime = position;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Seek error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Debug.WriteLine("[AudioEngine] Disposing...");
        _httpClient.Dispose();
        _outputDevice.Dispose();
        _audioReader?.Dispose();

        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder, "*.m4a"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}