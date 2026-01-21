using System.Diagnostics;
using LibVLCSharp.Shared;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.ViewModels;
using ReactiveUI;

namespace MyLiteMusicPlayer.Services;

public class AudioEngine : ViewModelBase, IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly StreamCacheManager _cacheManager;
    private readonly HttpClient _streamHttpClient;

    private readonly LibVLC _libVLC;
    private MediaPlayer? _player;
    private Media? _currentMedia;
    private MemoryFirstCachingStream? _currentStream;

    // СЕМАФОРЫ
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _apiLock = new(1, 1);

    private CancellationTokenSource? _cts;

    private int _session;
    private int _volumePercent;

    private volatile bool _isDisposed;
    private volatile bool _isPlayerReady;
    private volatile bool _isPlayingOrBuffering;

    // API Throttling
    private DateTime _lastApiCall = DateTime.MinValue;
    private const int ApiCooldownMs = 200;

    // Queue
    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;
    private (string Format, int Bitrate) _currentStreamInfo;

    // Volume Persistence
    private DateTime _lastVolumeChange = DateTime.MinValue;
    private bool _volumeSavePending;

    // Properties
    public TrackInfo? CurrentTrack { get; private set; }

    // Безопасный доступ к состоянию
    public bool IsPlaying => _player?.IsPlaying ?? false;
    public bool IsPaused => _player?.State == VLCState.Paused;

    // Вспомогательное свойство для логов
    public string VlcStateString => _player?.State.ToString() ?? "NULL";

    public TimeSpan CurrentPosition
    {
        get
        {
            try
            {
                var time = _player?.Time ?? -1;
                return time >= 0 ? TimeSpan.FromMilliseconds(time) : TimeSpan.Zero;
            }
            catch { return TimeSpan.Zero; }
        }
    }

    public TimeSpan TotalDuration
    {
        get
        {
            try
            {
                var length = _player?.Length ?? -1;
                if (length > 0) return TimeSpan.FromMilliseconds(length);
                return CurrentTrack?.Duration ?? TimeSpan.Zero;
            }
            catch { return CurrentTrack?.Duration ?? TimeSpan.Zero; }
        }
    }

    public double BufferProgress => _currentStream?.DownloadProgress ?? 0;

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value) return;
            _isLoading = value;
            this.RaisePropertyChanged();
            SafeInvoke(() => OnLoadingChanged?.Invoke(value));
        }
    }

    // Events
    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<int>? OnMaxVolumeChanged;

    public AudioEngine(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
        _cacheManager = new StreamCacheManager();

        _streamHttpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
            EnableMultipleHttp2Connections = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;

        float savedVolume = library.Data.Volume;
        _volumePercent = savedVolume <= 1.0f && savedVolume > 0
            ? (int)Math.Round(savedVolume * 100f)
            : (int)Math.Round(savedVolume);
        _volumePercent = Math.Clamp(_volumePercent, 0, 500);
        Log.Info($"Loaded Volume: {_volumePercent} (Raw saved: {savedVolume})");

        Core.Initialize();

        // МАКСИМАЛЬНОЕ КАЧЕСТВО ЗВУКА
        _libVLC = new LibVLC(
            // Отключаем ненужное
            "--no-video",
            "--no-spu",
            "--no-osd",
            "--no-stats",

            // Буферизация для стабильности
            "--network-caching=1500",
            "--file-caching=1000",
            "--live-caching=1000",

            // Стриминг
            "--http-reconnect",
            "--http-continuous",
            "--no-http-forward-cookies",
            "--no-metadata-network-access",
            "--no-auto-preparse",

            // Увеличенный prefetch
            "--prefetch-read-size=262144",
            "--prefetch-buffer-size=262144",

            // Качество аудио
            "--audio-resampler=soxr",
            "--no-audio-time-stretch",

            // Декодер - не пропускать ничего
            "--avcodec-skiploopfilter=0",
            "--avcodec-skip-frame=0",
            "--avcodec-skip-idct=0",

            // Синхронизация
            "--clock-jitter=0",
            "--no-drop-late-frames",
            "--no-skip-frames"
        );

        InitializePlayer();
        _ = VolumeSaveLoopAsync();

        Log.Info("Initialized with HIGH QUALITY audio settings.");
    }

    private void InitializePlayer()
    {
        _player = new MediaPlayer(_libVLC);

        _player.Playing += OnVlcPlaying;
        _player.Paused += OnVlcPaused;
        _player.Stopped += OnVlcStopped;
        _player.EndReached += OnVlcEndReached;
        _player.EncounteredError += OnVlcError;
        _player.Buffering += OnVlcBuffering;
        _player.TimeChanged += OnVlcTimeChanged;

        _isPlayerReady = false;

        // ВАЖНО: Применяем громкость сразу, чтобы не орало на 100% при старте
        ApplyVolumeImmediate();
    }

    #region VOLUME

    public void SetVolumeInstant(float value)
    {
        int percent = (int)Math.Round(value);
        _volumePercent = Math.Clamp(percent, 0, 500);

        // Применяем в Task.Run, чтобы не тормозить UI
        Task.Run(ApplyVolumeImmediate);

        _library.Data.Volume = _volumePercent;
        _lastVolumeChange = DateTime.UtcNow;
        _volumeSavePending = true;
    }

    public float GetVolume() => _volumePercent;

    public void SaveVolumeNow()
    {
        if (_volumeSavePending)
        {
            _volumeSavePending = false;
            _library.Save();
            Log.Info("Volume saved to disk.");
        }
    }

    public void UpdateAudioSettings()
    {
        Log.Info("Updating audio settings (MaxVol/Gain)...");
        SafeInvoke(() => OnMaxVolumeChanged?.Invoke(_library.Data.MaxVolumeLimit));
        Task.Run(ApplyVolumeImmediate);
    }

    private void ApplyVolumeImmediate()
    {
        if (_player == null || _isDisposed) return;
        try
        {
            // Расчет: Пользовательский % * Усиление (Gain)
            // Пользовательский % (0-MaxLimit) приходит из UI.

            float dbGain = Math.Clamp(_library.Data.TargetGainDb, -20f, 20f);
            float gainMultiplier = MathF.Pow(10f, dbGain / 20f);

            int finalVolume = (int)Math.Round(_volumePercent * gainMultiplier);
            finalVolume = Math.Clamp(finalVolume, 0, 500);

            _player.Volume = finalVolume;

            Log.Info($"Base: {_volumePercent}%, Gain: {dbGain}dB, Final VLC: {finalVolume}");
        }
        catch (Exception ex)
        {
            Log.Info($"Error: {ex.Message}");
        }
    }

    private async Task VolumeSaveLoopAsync()
    {
        while (!_isDisposed)
        {
            await Task.Delay(2000);
            if (_volumeSavePending && (DateTime.UtcNow - _lastVolumeChange).TotalSeconds >= 1.5)
            {
                SaveVolumeNow();
            }
        }
    }

    #endregion

    #region PLAYBACK LOGIC

    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null || _isDisposed) return;
        Log.Info($"PlayTrackAsync requested: {track.Title} ({track.Id})");

        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        var session = Interlocked.Increment(ref _session);

        try { oldCts?.Cancel(); } catch { }

        IsLoading = true;
        CurrentTrack = track;
        _isPlayerReady = false;
        _isPlayingOrBuffering = true;

        SafeInvoke(() => OnTrackChanged?.Invoke(track));

        Log.Info("Waiting for _loadLock...");
        bool lockAcquired = await _loadLock.WaitAsync(500);
        Log.Info($"_loadLock acquired: {lockAcquired}");
        try
        {
            await PlayTrackInternalAsync(track, session, _cts.Token);
        }
        finally
        {
            if (lockAcquired) _loadLock.Release();
        }
    }

    private async Task PlayTrackInternalAsync(TrackInfo track, int session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        QuickStopPlayback();

        try
        {
            (string Url, long Size)? result = null;

            if (string.IsNullOrEmpty(track.StreamUrl))
            {
                result = await GetStreamUrlWithSizeAsync(track, ct);
                if (!result.HasValue) throw new Exception("Failed to get URL");
            }
            else
            {
                result = (track.StreamUrl, -1);
            }

            if (_session != session || ct.IsCancellationRequested) return;

            string url = result.Value.Url;
            long size = result.Value.Size > 0 ? result.Value.Size : await TryGetContentLengthAsync(url, ct);

            // Если размер неизвестен или очень маленький - играем напрямую
            if (size <= 0)
            {
                Log.Info($"Playing direct URL (size unknown): {url}");
                var media = new Media(_libVLC, url, FromType.FromLocation);
                StartPlayback(media, null, track, session);
                return;
            }

            Log.Info($"Starting MemoryFirst stream. Size: {size / 1024}KB");
            var stream = new MemoryFirstCachingStream(track.Id, url, size, _streamHttpClient, _cacheManager);

            int prebuffer = size > 20 * 1024 * 1024 ? 64 * 1024 : 128 * 1024;
            await stream.PreBufferAsync(prebuffer, ct);

            if (_session != session || ct.IsCancellationRequested)
            {
                stream.Dispose();
                return;
            }

            var streamMedia = new Media(_libVLC, new StreamMediaInput(stream));
            StartPlayback(streamMedia, stream, track, session);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Playback cancelled");
            _isPlayingOrBuffering = false;
        }
        catch (Exception ex)
        {
            Log.Info($"Error in PlayTrackInternal: {ex.Message}");
            SafeInvoke(() => OnError?.Invoke(ex.Message));
            IsLoading = false;
            _isPlayingOrBuffering = false;
        }
    }

    private void StartPlayback(Media media, MemoryFirstCachingStream? stream, TrackInfo track, int session)
    {
        Log.Info("StartPlayback called. Swapping media...");
        var oldMedia = _currentMedia;
        var oldStream = _currentStream;

        _currentMedia = media;
        _currentStream = stream;

        _ = Task.Run(() =>
        {
            try { oldStream?.Dispose(); } catch { }
            try { oldMedia?.Dispose(); } catch { }
        });

        if (_player == null) return;

        _player.Media = media;

        // ВАЖНО: Применяем громкость перед стартом
        ApplyVolumeImmediate();

        var res = _player.Play();
        Log.Info($"_player.Play() result: {res}");

        AddToHistory(track);
    }

    private void QuickStopPlayback()
    {
        if (_player == null) return;
        try
        {
            if (_player.State != VLCState.Stopped)
            {
                Log.Info("QuickStopPlayback: Stopping VLC...");
                _player.Stop();
            }
        }
        catch { }

        var oldStream = _currentStream;
        _currentStream = null;
        if (oldStream != null)
        {
            _ = Task.Run(() => { try { oldStream.Dispose(); } catch { } });
        }
        _isPlayerReady = false;
    }

    #endregion

    #region CONTROLS & EVENTS (Async & Thread-Safe)

    /// <summary>
    /// Главный метод управления состоянием с ЛОГАМИ
    /// </summary>
    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (_isDisposed || _player == null) return;

        Log.Info($"SetPlaybackStateAsync: {(shouldPlay ? "PLAY" : "PAUSE")} requested.");
        Log.Info($"Pre-Lock State -> VLC: {VlcStateString}, IsPlaying: {IsPlaying}");

        await _commandLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var currentState = _player.State;
                Log.Info($"Inside Lock. Current VLC State: {currentState}");

                if (shouldPlay)
                {
                    // Хотим ИГРАТЬ
                    if (currentState == VLCState.Playing)
                    {
                        Log.Info("Already playing. Doing nothing.");
                    }
                    else if (currentState == VLCState.Paused)
                    {
                        Log.Info("State is Paused. Calling SetPause(false)...");
                        _player.SetPause(false);
                    }
                    else if (currentState == VLCState.Stopped || currentState == VLCState.Ended || currentState == VLCState.Error)
                    {
                        Log.Info($"State is {currentState}. Needs restart or full Play().");
                        if (CurrentTrack != null)
                        {
                            Log.Info("Restarting track via PlayTrackAsync...");
                            _ = PlayTrackAsync(CurrentTrack);
                        }
                        else
                        {
                            Log.Info("Calling _player.Play() fallback...");
                            _player.Play();
                        }
                    }
                    else
                    {
                        Log.Info($"State is {currentState} (Buffering/Opening). Calling Play() to be safe.");
                        _player.Play();
                    }
                }
                else
                {
                    // Хотим ПАУЗУ
                    if (currentState == VLCState.Playing || currentState == VLCState.Buffering || currentState == VLCState.Opening)
                    {
                        Log.Info("Calling SetPause(true)...");
                        _player.Pause();
                    }
                    else
                    {
                        Log.Info($"Already not playing ({currentState}). Doing nothing.");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Info($"SetState error: {ex.Message}");
        }
        finally
        {
            _commandLock.Release();
            Log.Info($"SetPlaybackStateAsync FINISHED. Post-State: {VlcStateString}");
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_player == null || !_isPlayerReady || _isDisposed) return;

        Log.Info($"To {position}");

        await _commandLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                long ms = (long)Math.Clamp(position.TotalMilliseconds, 0, _player.Length);
                _player.Time = ms;
            });
        }
        catch (Exception ex)
        {
            Log.Info($"Error: {ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public void Stop()
    {
        Log.Info("Stop requested.");
        Interlocked.Increment(ref _session);
        _cts?.Cancel();

        QuickStopPlayback();

        CurrentTrack = null;
        IsLoading = false;
        _isPlayingOrBuffering = false;
        SafeInvoke(() => OnTrackChanged?.Invoke(null));
        SafeInvoke(() => OnPlaybackStopped?.Invoke());
    }

    // VLC Event Handlers

    private void OnVlcPlaying(object? sender, EventArgs e)
    {
        Log.Info("Playing");
        if (_isDisposed) return;
        _isPlayerReady = true;
        IsLoading = false;
        ApplyVolumeImmediate();

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            _isPlayingOrBuffering = false;
            await PrefetchNextInQueueAsync();
        });
    }

    private void OnVlcPaused(object? sender, EventArgs e)
    {
        Log.Info("Paused");
    }

    private void OnVlcStopped(object? sender, EventArgs e)
    {
        Log.Info("Stopped");
        _isPlayerReady = false;
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        Log.Info("EndReached");
        if (_isDisposed) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            if (!_isDisposed) await PlayNextAsync();
        });
    }

    private void OnVlcError(object? sender, EventArgs e)
    {
        Log.Info("ERROR");
        SafeInvoke(() => OnError?.Invoke("VLC Error"));
        IsLoading = false;
    }

    private void OnVlcBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        // Слишком много спама, если логировать каждое изменение буфера
        // Log.Info($"Buffering {e.NewCache}%");
    }

    private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // Не логируем TimeChanged - убьет консоль
        if (_isDisposed || !_isPlayerReady) return;
        SafeInvoke(() => OnPositionChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time)));
    }

    #endregion

    #region HELPERS (Queue, Prefetch, StreamInfo)

    public (string Format, int Bitrate) GetCurrentStreamInfo()
    {
        string format = "AAC";
        if (CurrentTrack?.StreamUrl.Contains("webm") == true) format = "OPUS";
        else if (CurrentTrack?.IsDownloaded == true) format = Path.GetExtension(CurrentTrack.LocalPath)?.TrimStart('.').ToUpper() ?? "FILE";

        _currentStreamInfo = (format, 128);
        return _currentStreamInfo;
    }

    public long GetDownloadedBytes() => _currentStream != null ? (long)(_currentStream.DownloadProgress / 100.0 * _currentStream.Length) : 0;

    private async Task<(string Url, long Size)?> GetStreamUrlWithSizeAsync(TrackInfo track, CancellationToken ct)
    {
        await _apiLock.WaitAsync(ct);
        try
        {
            if ((DateTime.UtcNow - _lastApiCall).TotalMilliseconds < ApiCooldownMs)
                await Task.Delay(ApiCooldownMs, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var res = await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            _lastApiCall = DateTime.UtcNow;
            return res;
        }
        finally { _apiLock.Release(); }
    }

    private async Task<long> TryGetContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _streamHttpClient.SendAsync(req, cts.Token);
            return resp.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    private async Task PrefetchNextInQueueAsync()
    {
        await Task.Delay(1000);
        if (_queue.Count > 0 && !_isPlayingOrBuffering) await PrefetchAsync(_queue.Peek());
    }

    public async Task PrefetchAsync(TrackInfo track)
    {
        if (_isPlayingOrBuffering || track.IsDownloaded) return;
        try
        {
            await _apiLock.WaitAsync();
            using var cts = new CancellationTokenSource(5000);
            await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            _lastApiCall = DateTime.UtcNow;
            _apiLock.Release();
        }
        catch { _apiLock.Release(); }
    }

    public void Enqueue(TrackInfo track)
    {
        _queue.Enqueue(track);
        if (!IsPlaying && !IsPaused && !IsLoading) { _ = PlayTrackAsync(track); _queue.TryDequeue(out _); }
    }
    public void EnqueueRange(IEnumerable<TrackInfo> tracks) { foreach (var t in tracks) Enqueue(t); }
    public void ClearQueue() => _queue.Clear();

    public async Task PlayNextAsync()
    {
        TrackInfo? next = null;
        if (RepeatMode == RepeatMode.RepeatOne) next = CurrentTrack;
        else if (ShuffleEnabled && _queue.Count > 0)
        {
            var l = _queue.ToList();
            int i = Random.Shared.Next(l.Count);
            next = l[i];
            l.RemoveAt(i);
            _queue.Clear();
            foreach (var t in l) _queue.Enqueue(t);
        }
        else if (_queue.TryDequeue(out var q)) next = q;

        if (next != null) await PlayTrackAsync(next); else Stop();
    }

    public async Task PlayPreviousAsync()
    {
        if (_historyIndex > 0) { _historyIndex--; await PlayTrackAsync(_history[_historyIndex]); }
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;
        _history.Add(track);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    #endregion

    private static void SafeInvoke(Action action) { try { action(); } catch { } }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        SaveVolumeNow();
        _cts?.Cancel();

        try { _currentStream?.Dispose(); } catch { }

        if (_player != null)
        {
            _player.Playing -= OnVlcPlaying;
            _player.Paused -= OnVlcPaused;
            _player.Stopped -= OnVlcStopped;
            _player.EndReached -= OnVlcEndReached;
            _player.EncounteredError -= OnVlcError;
            _player.Buffering -= OnVlcBuffering;
            _player.TimeChanged -= OnVlcTimeChanged;
            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
        }

        try { _libVLC.Dispose(); } catch { }
        try { _loadLock.Dispose(); } catch { }
        try { _commandLock.Dispose(); } catch { }
        try { _apiLock.Dispose(); } catch { }
        try { _streamHttpClient.Dispose(); } catch { }
        try { _cacheManager.Dispose(); } catch { }
    }
}