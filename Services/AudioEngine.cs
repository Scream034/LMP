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
    private readonly LibraryService _library;          // Добавлено для проверки кэша
    private readonly DownloadService _downloadService;  // Добавлено для авто-кэширования

    private readonly string _cacheFolder;
    private IWaveProvider? _currentProvider;
    private CancellationTokenSource? _streamCts;
    private readonly object _lock = new();

    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = new();
    private int _historyIndex = -1;

    private bool _isManualStop = false;
    private Guid _currentSessionId;

    private TimeSpan _currentDuration;
    private TimeSpan _currentPosition;
    private float _volume = 0.5f;

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
    public TimeSpan TotalDuration => _currentDuration > TimeSpan.Zero ? _currentDuration : CurrentTrack?.Duration ?? TimeSpan.Zero;
    public TimeSpan CurrentPosition => _currentPosition;

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;

    // Обновленный конструктор с инъекцией сервисов
    public AudioEngine(YoutubeProvider youtube, LibraryService library, DownloadService downloadService)
    {
        _youtube = youtube;
        _library = library;
        _downloadService = downloadService;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "Cache");
        Directory.CreateDirectory(_cacheFolder);

        _outputDevice = new WaveOutEvent
        {
            DesiredLatency = 200, // Немного уменьшил латентность
            NumberOfBuffers = 3
        };
        _outputDevice.PlaybackStopped += OnDevicePlaybackStopped;

        Task.Run(PositionTrackerLoop);
    }

    private async Task PositionTrackerLoop()
    {
        while (true)
        {
            await Task.Delay(250);
            // Логика трекинга внутри потока воспроизведения, здесь просто заглушка или доп. проверки
        }
    }


    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return;

        var mySessionId = Guid.NewGuid();
        _currentSessionId = mySessionId;

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        IsLoading = true;
        CurrentTrack = track;
        _currentPosition = TimeSpan.Zero;
        OnTrackChanged?.Invoke(CurrentTrack);

        try
        {
            lock (_lock)
            {
                _isManualStop = true;
                StopInternal(false);
                _isManualStop = false;
            }

            // 1. Проверяем наличие локального файла (Кэш или загрузка)
            string localFile = string.Empty;

            // Сначала проверяем в библиотеке
            if (_library.HasTrack(track.Id))
            {
                var existing = _library.GetTrack(track.Id);
                if (existing != null && existing.IsDownloaded && File.Exists(existing.LocalPath))
                {
                    localFile = existing.LocalPath;
                }
            }

            // 2. Логика воспроизведения
            if (!string.IsNullOrEmpty(localFile))
            {
                Debug.WriteLine($"[AudioEngine] Playing from LOCAL CACHE: {localFile}");
                await StartLocalPlaybackAsync(localFile, mySessionId, ct);
            }
            else
            {
                // Нет файла - стримим из интернета
                Debug.WriteLine($"[AudioEngine] Stream requested. Fetching URL...");
                var freshUrl = await _youtube.RefreshStreamUrlAsync(track);

                if (ct.IsCancellationRequested) return;
                if (string.IsNullOrEmpty(freshUrl)) throw new Exception("Could not resolve stream URL");

                _currentDuration = track.Duration;

                // ВАЖНО: Запускаем авто-кэширование (скачивание) в фоне!
                // Проверяем, не скачивается ли уже
                if (!_downloadService.IsDownloading(track.Id))
                {
                    Debug.WriteLine($"[AudioEngine] Triggering auto-cache download for '{track.Title}'");
                    _downloadService.StartDownload(track); // Это асинхронный метод "fire and forget"
                }

                await StartStreamingPlaybackAsync(freshUrl, mySessionId, ct);
            }

            if (_currentSessionId == mySessionId && _outputDevice.PlaybackState == PlaybackState.Playing)
            {
                AddToHistory(track);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Playback failed: {ex.Message}");
            OnError?.Invoke(ex.Message);
            if (_currentSessionId == mySessionId) StopInternal(true);
        }
        finally
        {
            if (_currentSessionId == mySessionId) IsLoading = false;
        }
    }

    private async Task StartLocalPlaybackAsync(string path, Guid sessionId, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            try
            {
                var reader = new AudioFileReader(path);
                _currentDuration = reader.TotalTime;

                lock (_lock)
                {
                    if (ct.IsCancellationRequested) { reader.Dispose(); return; }
                    _currentProvider = reader;
                    _outputDevice.Init(reader);
                    _outputDevice.Volume = _volume;
                    _outputDevice.Play();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Local playback error: {ex.Message}");
            }
        }, ct);

        // Запускаем трекинг для локального файла
        _ = TrackPositionAsync(null, sessionId, ct);
    }

    private async Task StartStreamingPlaybackAsync(string url, Guid sessionId, CancellationToken ct)
    {
        // Используем MediaFoundationReader напрямую - он сам умеет стримить
        // Но нужно использовать правильные настройки

        MediaFoundationReader? reader = null;

        await Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                Debug.WriteLine("[AudioEngine] Creating MediaFoundationReader for streaming...");

                // MediaFoundationReader умеет стримить HTTP, но иногда тормозит на m4a
                reader = new MediaFoundationReader(url);

                Debug.WriteLine($"[AudioEngine] Reader created. Format: {reader.WaveFormat}");
                Debug.WriteLine($"[AudioEngine] Duration: {reader.TotalTime}");

                if (reader.TotalTime.TotalSeconds > 0)
                {
                    _currentDuration = reader.TotalTime;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Reader creation failed: {ex.Message}");
                throw;
            }
        }, ct);

        if (ct.IsCancellationRequested || reader == null)
        {
            reader?.Dispose();
            return;
        }

        lock (_lock)
        {
            _currentProvider = reader;
            _outputDevice.Init(reader);
            _outputDevice.Volume = _volume;
            _outputDevice.Play();
        }

        // Ждем начала воспроизведения
        await Task.Delay(300, ct);

        if (_outputDevice.PlaybackState == PlaybackState.Stopped && !_isManualStop)
        {
            throw new Exception("Playback failed to start");
        }

        // Запускаем отслеживание позиции
        _ = TrackPositionAsync(reader, sessionId, ct);
    }

    private async Task TrackPositionAsync(MediaFoundationReader? mfReader, Guid sessionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _currentSessionId == sessionId)
        {
            await Task.Delay(200, ct);
            try
            {
                if (_outputDevice.PlaybackState == PlaybackState.Playing)
                {
                    // Если используем AudioFileReader (локально)
                    if (_currentProvider is AudioFileReader fileReader)
                    {
                        _currentPosition = fileReader.CurrentTime;
                    }
                    // Если стримим через MediaFoundationReader
                    else if (mfReader != null)
                    {
                        _currentPosition = mfReader.CurrentTime;
                    }

                    OnPositionChanged?.Invoke(_currentPosition);
                }
            }
            catch { }
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
        _streamCts?.Cancel();
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
            if (_currentProvider is IDisposable d)
                d.Dispose();
            _currentProvider = null;
        }
        catch { }

        if (clearCurrent)
        {
            CurrentTrack = null;
            _currentPosition = TimeSpan.Zero;
            OnTrackChanged?.Invoke(null);
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
                // Перезапускаем текущий трек
                _ = PlayTrackAsync(CurrentTrack);
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
            Debug.WriteLine($"[AudioEngine] Exception: {e.Exception.Message}");
            OnError?.Invoke($"Playback Error: {e.Exception.Message}");
        }

        if (_isManualStop || IsLoading) return;

        // Проверяем, дошли ли до конца
        bool isNearEnd = _currentDuration.TotalSeconds > 1 &&
                         (_currentDuration - _currentPosition).TotalSeconds < 2;

        if (CurrentTrack != null && isNearEnd)
        {
            Debug.WriteLine("[AudioEngine] Track finished, playing next...");
            await PlayNextAsync();
        }
        else if (_currentPosition.TotalSeconds < 1 && _currentDuration.TotalSeconds > 5)
        {
            Debug.WriteLine("[AudioEngine] Stream error at beginning, skipping...");
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
                if (_currentProvider is MediaFoundationReader reader && reader.CanSeek)
                {
                    position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, TotalDuration.TotalSeconds));
                    reader.CurrentTime = position;
                    _currentPosition = position;
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
        _streamCts?.Cancel();
        _outputDevice.Dispose();

        if (_currentProvider is IDisposable d)
            d.Dispose();

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