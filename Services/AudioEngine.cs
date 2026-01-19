using NAudio.Wave;
using MyLiteMusicPlayer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// Alias to avoid conflict
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

    // Свойство для UI
    public bool IsLoading { get; private set; }
    public event Action<bool>? OnLoadingChanged;

    public TimeSpan CurrentPosition
    {
        get { lock (_lock) return _audioReader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan TotalDuration
    {
        get { lock (_lock) return _audioReader?.TotalTime ?? CurrentTrack?.Duration ?? TimeSpan.Zero; }
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

    /// <summary>
    /// Асинхронный метод воспроизведения. Не блокирует UI.
    /// </summary>
    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return;

        SetLoading(true);
        try
        {
            // 1. Если ссылки нет, получаем её
            if (string.IsNullOrEmpty(track.StreamUrl))
            {
                Console.WriteLine($"[AudioEngine] Resolving URL for: {track.Title}");
                var url = await _youtube.RefreshStreamUrlAsync(track);
                if (string.IsNullOrEmpty(url))
                {
                    throw new Exception("Не удалось получить ссылку на аудиопоток.");
                }
            }

            // 2. Воспроизведение
            lock (_lock)
            {
                StopInternal(false);

                if (CurrentTrack != null && CurrentTrack.Id != track.Id)
                {
                    if (_historyIndex < _history.Count - 1)
                        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                    _history.Add(CurrentTrack);
                }

                CurrentTrack = track;
                _historyIndex = _history.Count;

                try
                {
                    _audioReader = new MediaFoundationReader(track.StreamUrl);
                    _outputDevice.Init(_audioReader);
                    _outputDevice.Volume = _volume;
                    _outputDevice.Play();

                    OnTrackChanged?.Invoke(CurrentTrack);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Playback failed: {ex.Message}");
                    StopInternal(true);
                    OnError?.Invoke($"Playback error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Resolve failed: {ex.Message}");
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            SetLoading(false);
        }
    }

    // Обертка для старого кода (fire-and-forget)
    public void PlayTrack(TrackInfo track)
    {
        _ = PlayTrackAsync(track);
    }

    private void SetLoading(bool loading)
    {
        IsLoading = loading;
        OnLoadingChanged?.Invoke(loading);
    }

    public void Enqueue(TrackInfo track)
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState != PlaybackState.Playing && CurrentTrack == null && !IsLoading)
            {
                _ = PlayTrackAsync(track);
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
            foreach (var track in tracks) _queue.Enqueue(track);

            if (CurrentTrack == null && _queue.Count > 0 && !IsLoading)
            {
                _ = PlayTrackAsync(_queue.Dequeue());
            }
        }
    }

    public void ClearQueue() { lock (_lock) _queue.Clear(); }
    public void Pause() { lock (_lock) if (_outputDevice.PlaybackState == PlaybackState.Playing) _outputDevice.Pause(); }
    public void Resume()
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState == PlaybackState.Paused) _outputDevice.Play();
            else if (CurrentTrack != null && _outputDevice.PlaybackState == PlaybackState.Stopped && !IsLoading)
                _ = PlayTrackAsync(CurrentTrack);
        }
    }
    public void TogglePlayPause() { if (IsPlaying) Pause(); else Resume(); }
    public void Stop() { lock (_lock) StopInternal(true); }
    public void SetVolume(float vol) { _volume = Math.Clamp(vol, 0f, 1f); _outputDevice.Volume = _volume; }
    public float GetVolume() => _volume;

    private void StopInternal(bool clearCurrent)
    {
        if (_outputDevice.PlaybackState != PlaybackState.Stopped) _outputDevice.Stop();
        if (_audioReader != null) { _audioReader.Dispose(); _audioReader = null; }
        if (clearCurrent) CurrentTrack = null;
    }

    public async Task PlayNextAsync()
    {
        TrackInfo? next = null;

        lock (_lock)
        {
            if (RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
            {
                if (_audioReader != null && _audioReader.CanSeek)
                    _audioReader.CurrentTime = TimeSpan.Zero;
                if (_outputDevice.PlaybackState != PlaybackState.Playing)
                    _outputDevice.Play();
                return;
            }

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
            else if (RepeatMode == RepeatMode.RepeatAll && _history.Count > 0)
            {
                foreach (var track in _history) _queue.Enqueue(track);
                _history.Clear();
                _historyIndex = -1;
                if (_queue.TryDequeue(out var first)) next = first;
            }
        }

        if (next != null)
        {
            await PlayTrackAsync(next);
        }
        else
        {
            lock (_lock) { StopInternal(true); }
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

                if (CurrentTrack != null)
                {
                    var temp = new List<TrackInfo> { CurrentTrack };
                    temp.AddRange(_queue);
                    _queue.Clear();
                    foreach (var t in temp) _queue.Enqueue(t);
                }
            }
        }

        if (prev != null) await PlayTrackAsync(prev);
    }

    public void PlayPrevious() => _ = PlayPreviousAsync();

    private async void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (CurrentTrack != null && !IsLoading)
        {
            await PlayNextAsync();
        }
    }

    public void Seek(TimeSpan position) { lock (_lock) if (_audioReader?.CanSeek == true) _audioReader.CurrentTime = position; }
    public void Dispose() { _outputDevice.Dispose(); _audioReader?.Dispose(); }
}