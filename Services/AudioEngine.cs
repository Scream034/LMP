using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MyLiteMusicPlayer.Models;
using PlaybackState = NAudio.Wave.PlaybackState;

namespace MyLiteMusicPlayer.Services;

public class AudioEngine : IDisposable
{
    private readonly IWavePlayer _outputDevice;
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly DownloadService _downloadService;
    private readonly HttpClient _httpClient;

    private WaveStream? _currentStream;
    private VolumeSampleProvider? _volumeControl;

    private CancellationTokenSource? _streamCts;
    private readonly object _lock = new();
    private Guid _currentSessionId;
    private bool _isManualStop;
    private float _userVolumeMultiplier = 1.0f;

    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying => _outputDevice.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _outputDevice.PlaybackState == PlaybackState.Paused;

    private TimeSpan _currentPosition;
    public TimeSpan CurrentPosition => _currentPosition;
    public TimeSpan TotalDuration => _currentStream?.TotalTime ?? CurrentTrack?.Duration ?? TimeSpan.Zero;

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

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;

    public AudioEngine(
        YoutubeProvider youtube,
        LibraryService library,
        DownloadService downloadService)
    {
        _youtube = youtube;
        _library = library;
        _downloadService = downloadService;

        // HTTP клиент для стриминга
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30) // Долгие треки
        };

        _outputDevice = new WaveOutEvent
        {
            DesiredLatency = 150, // Уменьшаем для быстрого старта
            NumberOfBuffers = 3
        };
        _outputDevice.PlaybackStopped += OnDevicePlaybackStopped;

        _userVolumeMultiplier = _library.Data.Volume;
        ShuffleEnabled = _library.Data.ShuffleEnabled;
        RepeatMode = _library.Data.RepeatMode;
    }

    #region Volume Control

    public void SetVolume(float multiplier)
    {
        lock (_lock)
        {
            _userVolumeMultiplier = Math.Clamp(multiplier, 0f, 4f);

            _library.Data.Volume = _userVolumeMultiplier;
            _library.Save();

            if (_volumeControl != null)
            {
                float dbGain = _library.Data.TargetGainDb;
                float dbMultiplier = (float)Math.Pow(10, dbGain / 20.0);
                _volumeControl.Volume = _userVolumeMultiplier * dbMultiplier;
            }
        }
    }

    public float GetVolume() => _userVolumeMultiplier;

    #endregion

    #region Playback

    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return;

        var sessionId = Guid.NewGuid();
        lock (_lock) _currentSessionId = sessionId;

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        IsLoading = true;
        CurrentTrack = track;
        _currentPosition = TimeSpan.Zero;
        OnTrackChanged?.Invoke(track);

        try
        {
            StopInternal(false);

            var sw = Stopwatch.StartNew();

            WaveStream reader = await Task.Run(async () =>
            {
                Debug.WriteLine($"[AudioEngine-Thread] Starting stream creation on thread {Environment.CurrentManagedThreadId}");
                return await CreateStreamReaderAsync(track, ct);
            }, ct);

            sw.Stop();
            Debug.WriteLine($"[AudioEngine] Stream ready in {sw.ElapsedMilliseconds}ms");

            if (ct.IsCancellationRequested || _currentSessionId != sessionId)
            {
                reader.Dispose();
                return;
            }

            lock (_lock)
            {
                _currentStream = reader;

                var sampleProvider = reader.ToSampleProvider();
                _volumeControl = new VolumeSampleProvider(sampleProvider);
                SetVolume(_userVolumeMultiplier);

                _outputDevice.Init(_volumeControl);
                _outputDevice.Play();
            }

            AddToHistory(track);
            _ = TrackPositionLoopAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Playback Error: {ex.Message}");
            OnError?.Invoke(ex.Message);
            StopInternal(true);
        }
        finally
        {
            if (_currentSessionId == sessionId)
                IsLoading = false;
        }
    }

    private async Task<WaveStream> CreateStreamReaderAsync(TrackInfo track, CancellationToken ct)
    {
        // 1. Локальный файл
        if (track.IsDownloaded && File.Exists(track.LocalPath))
        {
            return new AudioFileReader(track.LocalPath);
        }

        // 2. Получаем stream URL (асинхронно, не блочит)
        string? streamUrl = track.StreamUrl;
        if (string.IsNullOrEmpty(streamUrl))
        {
            streamUrl = await _youtube.RefreshStreamUrlAsync(track, ct);
        }

        if (string.IsNullOrEmpty(streamUrl))
            throw new Exception("Не удалось получить ссылку на аудиопоток.");

        // 3. Создание MediaFoundationReader (синхронное, но мы уже в Task.Run)
        Debug.WriteLine($"[AudioEngine-Thread] Creating MediaFoundationReader...");
        var sw = Stopwatch.StartNew();

        // ВАЖНО: MediaFoundationReader конструктор блокирует поток!
        // Поэтому он ДОЛЖЕН быть внутри Task.Run
        var reader = new MediaFoundationReader(streamUrl, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true // Лучшее качество
        });

        sw.Stop();
        Debug.WriteLine($"[AudioEngine-Thread] MediaFoundationReader created in {sw.ElapsedMilliseconds}ms");

        return reader;
    }

    private async Task TrackPositionLoopAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _currentSessionId == sessionId)
            {
                if (_outputDevice.PlaybackState == PlaybackState.Playing && _currentStream != null)
                {
                    try
                    {
                        _currentPosition = _currentStream.CurrentTime;
                        OnPositionChanged?.Invoke(_currentPosition);
                    }
                    catch { }
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Queue Management

    public void Enqueue(TrackInfo track)
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState == PlaybackState.Stopped && !IsLoading)
                _ = PlayTrackAsync(track);
            else
                _queue.Enqueue(track);
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var t in tracks) Enqueue(t);
    }

    public void ClearQueue() { lock (_lock) _queue.Clear(); }

    public async Task PlayNextAsync()
    {
        TrackInfo? next = null;
        lock (_lock)
        {
            if (RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
            {
                next = CurrentTrack;
            }
            else if (ShuffleEnabled && _queue.Count > 0)
            {
                var list = _queue.ToList();
                int idx = Random.Shared.Next(list.Count);
                next = list[idx];
                list.RemoveAt(idx);
                _queue.Clear();
                foreach (var t in list) _queue.Enqueue(t);
            }
            else if (_queue.TryDequeue(out var queued))
            {
                next = queued;
            }
        }

        if (next != null) await PlayTrackAsync(next);
        else Stop();
    }

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

    private void AddToHistory(TrackInfo track)
    {
        if (_history.Count > 0 && _history.Last().Id == track.Id) return;

        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(track);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    #endregion

    #region Transport Controls

    public void TogglePlayPause()
    {
        if (IsPlaying) _outputDevice.Pause();
        else if (IsPaused) _outputDevice.Play();
        else if (CurrentTrack != null) _ = PlayTrackAsync(CurrentTrack);
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_currentStream == null) return;

            var target = position;
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (target > _currentStream.TotalTime) target = _currentStream.TotalTime;

            try
            {
                _currentStream.CurrentTime = target;
                _currentPosition = target;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Seek error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isManualStop = true;
            _currentSessionId = Guid.NewGuid();
            StopInternal(true);
            _isManualStop = false;
        }
    }

    private void StopInternal(bool clearCurrent)
    {
        try
        {
            _outputDevice.Stop();
        }
        catch { }

        try
        {
            _currentStream?.Dispose();
        }
        catch { }

        _currentStream = null;
        _volumeControl = null;

        if (clearCurrent)
        {
            CurrentTrack = null;
            _currentPosition = TimeSpan.Zero;
            OnTrackChanged?.Invoke(null);
        }
    }

    #endregion

    #region Event Handlers

    private void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_isManualStop || IsLoading) return;

        if (e.Exception != null)
        {
            Debug.WriteLine($"[AudioEngine] Device Error: {e.Exception.Message}");
            OnError?.Invoke(e.Exception.Message);
        }

        bool finished = _currentStream != null &&
                        (_currentStream.TotalTime - _currentStream.CurrentTime).TotalSeconds < 1.5;

        if (finished)
        {
            _ = PlayNextAsync();
        }
        else
        {
            OnPlaybackStopped?.Invoke();
        }
    }

    #endregion

    public void Dispose()
    {
        _streamCts?.Cancel();
        _outputDevice.PlaybackStopped -= OnDevicePlaybackStopped;
        _outputDevice.Dispose();
        _currentStream?.Dispose();
        _streamCts?.Dispose();
        _httpClient.Dispose();
    }
}