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

    private readonly SemaphoreSlim _playLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private int _session;
    private int _volumePercent;
    private volatile bool _isDisposed;
    private volatile bool _isPlayerReady;
    
    // === PREFETCH CONTROL ===
    private volatile bool _isPlayingOrBuffering;  // Блокирует prefetch!
    private readonly SemaphoreSlim _apiLock = new(1, 1);  // Один запрос к YouTube API за раз
    private DateTime _lastApiCall = DateTime.MinValue;
    private const int ApiCooldownMs = 500;  // Минимум 500ms между API вызовами

    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;

    public TrackInfo? CurrentTrack { get; private set; }

    public bool IsPlaying
    {
        get
        {
            try { return _player?.IsPlaying ?? false; }
            catch { return false; }
        }
    }

    public bool IsPaused
    {
        get
        {
            try { return _player?.State == VLCState.Paused; }
            catch { return false; }
        }
    }

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

    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;

    public AudioEngine(YoutubeProvider youtube, LibraryService library, DownloadService _)
    {
        _youtube = youtube;
        _library = library;
        _cacheManager = new StreamCacheManager();
        
        // HTTP Client для streaming - ОТДЕЛЬНЫЙ от YouTube API!
        _streamHttpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 1,  // ТОЛЬКО 1 соединение!
            EnableMultipleHttp2Connections = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        
        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;

        float savedVolume = library.Data.Volume;
        _volumePercent = savedVolume <= 1.0f 
            ? (int)Math.Round(savedVolume * 100f) 
            : (int)Math.Round(savedVolume);
        _volumePercent = Math.Clamp(_volumePercent, 0, 200);

        Debug.WriteLine($"[AudioEngine] Initial volume: {_volumePercent}%");

        Core.Initialize();

        _libVLC = new LibVLC(
            "--no-video",
            "--no-spu",
            "--network-caching=500",
            "--file-caching=300",
            "--live-caching=500",
            "--http-reconnect",
            "--no-http-forward-cookies",
            "--no-metadata-network-access",
            "--no-auto-preparse"
        );

        InitializePlayer();

        Debug.WriteLine("[AudioEngine] Initialized v4 (Memory-First)");
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

        _isPlayerReady = false;
    }

    #region VOLUME

    public void SetVolume(float value)
    {
        int percent = value <= 1.0f 
            ? (int)Math.Round(value * 100f) 
            : (int)Math.Round(value);

        _volumePercent = Math.Clamp(percent, 0, 200);

        Debug.WriteLine($"[Audio] SetVolume: {value:F2} → {_volumePercent}%");

        _library.Data.Volume = Math.Clamp(value, 0f, 2f);
        _library.Save();

        ApplyVolume();
    }

    public float GetVolume() => _volumePercent;
    public float GetVolumeNormalized() => _volumePercent / 100f;

    private void ApplyVolume()
    {
        if (_player == null || _isDisposed) return;

        try
        {
            float dbGain = Math.Clamp(_library.Data.TargetGainDb, -20f, 20f);
            float gainMultiplier = MathF.Pow(10f, dbGain / 20f);

            int finalVolume = (int)Math.Round(_volumePercent * gainMultiplier);
            finalVolume = Math.Clamp(finalVolume, 0, 200);

            _player.Volume = finalVolume;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] Volume error: {ex.Message}");
            try { _player.Volume = _volumePercent; } catch { }
        }
    }

    #endregion

    #region PLAYBACK

    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null || _isDisposed) return;

        if (!await _playLock.WaitAsync(2000))
        {
            Debug.WriteLine("[Audio] Lock timeout - forcing");
        }

        try
        {
            await PlayTrackInternalAsync(track);
        }
        finally
        {
            try { _playLock.Release(); } catch { }
        }
    }

    private async Task PlayTrackInternalAsync(TrackInfo track)
    {
        int session = Interlocked.Increment(ref _session);
        var sw = Stopwatch.StartNew();

        Debug.WriteLine($"[Audio] #{session} → {track.Title}");

        // === БЛОКИРУЕМ PREFETCH ===
        _isPlayingOrBuffering = true;

        IsLoading = true;
        CurrentTrack = track;
        _isPlayerReady = false;
        SafeInvoke(() => OnTrackChanged?.Invoke(track));

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        StopPlaybackInternal();

        try
        {
            // === ПОЛУЧАЕМ URL С RATE LIMITING ===
            string url = track.StreamUrl;
            
            if (string.IsNullOrEmpty(url))
            {
                url = await GetStreamUrlWithRateLimitAsync(track, ct);
                
                if (string.IsNullOrEmpty(url))
                    throw new Exception("Failed to get stream URL");
            }

            if (_session != session || ct.IsCancellationRequested) return;

            // === СОЗДАЁМ STREAM ===
            long contentLength = await GetContentLengthAsync(url, ct);
            
            if (contentLength <= 0)
            {
                // Fallback: прямой URL
                Debug.WriteLine("[Audio] Source: direct URL (no content-length)");
                var media = new Media(_libVLC, url, FromType.FromLocation);
                PlayMedia(media, null, track, session, sw);
                return;
            }

            var stream = new MemoryFirstCachingStream(
                track.Id,
                url,
                contentLength,
                _streamHttpClient,
                _cacheManager);

            // Pre-buffer
            Debug.WriteLine("[Audio] Pre-buffering...");
            await stream.PreBufferAsync(256 * 1024, ct);

            if (_session != session || ct.IsCancellationRequested)
            {
                stream.Dispose();
                return;
            }

            var streamMedia = new Media(_libVLC, new StreamMediaInput(stream));
            PlayMedia(streamMedia, stream, track, session, sw);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[Audio] #{session} Cancelled");
            _isPlayingOrBuffering = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] #{session} Error: {ex.Message}");
            SafeInvoke(() => OnError?.Invoke(ex.Message));
            IsLoading = false;
            _isPlayingOrBuffering = false;
        }
    }

    private void PlayMedia(Media media, MemoryFirstCachingStream? stream, TrackInfo track, int session, Stopwatch sw)
    {
        var oldMedia = _currentMedia;
        var oldStream = _currentStream;
        _currentMedia = media;
        _currentStream = stream;
        oldMedia?.Dispose();
        oldStream?.Dispose();

        _player!.Media = media;
        _player.Play();

        Debug.WriteLine($"[Audio] #{session} Play() ({sw.ElapsedMilliseconds}ms)");

        AddToHistory(track);

        // Position loop
        _ = PositionUpdateLoopAsync(session, _cts!.Token);
    }

    /// <summary>
    /// Получение URL с rate limiting - НЕ параллелим запросы!
    /// </summary>
    private async Task<string?> GetStreamUrlWithRateLimitAsync(TrackInfo track, CancellationToken ct)
    {
        await _apiLock.WaitAsync(ct);
        try
        {
            // Ждём cooldown
            var elapsed = DateTime.UtcNow - _lastApiCall;
            if (elapsed.TotalMilliseconds < ApiCooldownMs)
            {
                await Task.Delay(ApiCooldownMs - (int)elapsed.TotalMilliseconds, ct);
            }

            Debug.WriteLine("[Audio] Fetching stream URL...");
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            
            var url = await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            
            _lastApiCall = DateTime.UtcNow;
            
            return url;
        }
        finally
        {
            _apiLock.Release();
        }
    }

    private async Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _streamHttpClient.SendAsync(request, ct);
            
            if (response.Content.Headers.ContentLength.HasValue)
            {
                return response.Content.Headers.ContentLength.Value;
            }
            
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            
            using var rangeResponse = await _streamHttpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            
            if (rangeResponse.Content.Headers.ContentRange?.Length.HasValue == true)
            {
                return rangeResponse.Content.Headers.ContentRange.Length.Value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] GetContentLength error: {ex.Message}");
        }
        
        return -1;
    }

    private void StopPlaybackInternal()
    {
        if (_player == null) return;

        try
        {
            var state = _player.State;
            if (state == VLCState.Playing || state == VLCState.Paused || state == VLCState.Buffering)
            {
                _player.Stop();
            }
        }
        catch { }

        _isPlayerReady = false;
    }

    private async Task PositionUpdateLoopAsync(int session, CancellationToken ct)
    {
        while (!_isPlayerReady && !ct.IsCancellationRequested && _session == session)
        {
            await Task.Delay(50, ct);
        }

        try
        {
            while (!ct.IsCancellationRequested && _session == session && !_isDisposed)
            {
                if (_isPlayerReady && IsPlaying)
                {
                    SafeInvoke(() => OnPositionChanged?.Invoke(CurrentPosition));
                }
                await Task.Delay(250, ct);
            }
        }
        catch { }
    }

    #endregion

    #region PREFETCH

    /// <summary>
    /// Prefetch - ТОЛЬКО когда не играем и не буферизируем!
    /// </summary>
    public async Task PrefetchAsync(TrackInfo track)
    {
        // НЕ prefetch'им во время воспроизведения/буферизации!
        if (_isPlayingOrBuffering)
        {
            return;
        }
        
        if (_isDisposed || track.IsDownloaded) return;
        if (!string.IsNullOrEmpty(track.StreamUrl)) return;

        try
        {
            await _apiLock.WaitAsync();
            try
            {
                // Ещё раз проверяем - вдруг начали играть
                if (_isPlayingOrBuffering) return;
                
                // Ждём cooldown
                var elapsed = DateTime.UtcNow - _lastApiCall;
                if (elapsed.TotalMilliseconds < ApiCooldownMs)
                {
                    await Task.Delay(ApiCooldownMs - (int)elapsed.TotalMilliseconds);
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _youtube.RefreshStreamUrlAsync(track, cts.Token);
                
                _lastApiCall = DateTime.UtcNow;
                
                Debug.WriteLine($"[Audio] Prefetched URL: {track.Title}");
            }
            finally
            {
                _apiLock.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] Prefetch error: {ex.Message}");
        }
    }

    private async Task PrefetchNextInQueueAsync()
    {
        // Задержка перед prefetch
        await Task.Delay(1000);
        
        if (_queue.Count > 0 && !_isPlayingOrBuffering)
        {
            await PrefetchAsync(_queue.Peek());
        }
    }

    public void PrefetchRange(IEnumerable<TrackInfo> tracks)
    {
        // Отложенный prefetch после загрузки
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);  // Ждём 2 сек
            
            foreach (var track in tracks.Take(2))
            {
                if (_isPlayingOrBuffering) break;
                await PrefetchAsync(track);
                await Task.Delay(500);  // Между prefetch'ами
            }
        });
    }

    #endregion

    #region VLC EVENTS

    private void OnVlcPlaying(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        Debug.WriteLine("[Audio] VLC: Playing");
        _isPlayerReady = true;
        IsLoading = false;
        
        // Разрешаем prefetch через 2 секунды после старта
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            _isPlayingOrBuffering = false;
            await PrefetchNextInQueueAsync();
        });

        ApplyVolume();
    }

    private void OnVlcPaused(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        Debug.WriteLine("[Audio] VLC: Paused");
    }

    private void OnVlcStopped(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        Debug.WriteLine("[Audio] VLC: Stopped");
        _isPlayerReady = false;
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        Debug.WriteLine("[Audio] VLC: EndReached");

        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            if (!_isDisposed)
            {
                await PlayNextAsync();
            }
        });
    }

    private void OnVlcError(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        Debug.WriteLine("[Audio] VLC: Error");

        _isPlayerReady = false;
        IsLoading = false;
        _isPlayingOrBuffering = false;
        SafeInvoke(() => OnError?.Invoke("Playback error"));
    }

    private void OnVlcBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        if (_isDisposed) return;

        if (e.Cache < 100 && (int)e.Cache % 25 == 0)
        {
            Debug.WriteLine($"[Audio] VLC Buffering: {e.Cache:F0}%");
        }
    }

    #endregion

    #region CONTROLS

    public void TogglePlayPause()
    {
        if (_isDisposed) return;

        if (_player == null)
        {
            if (CurrentTrack != null)
                _ = PlayTrackAsync(CurrentTrack);
            return;
        }

        try
        {
            if (IsPlaying)
            {
                _player.Pause();
            }
            else if (IsPaused)
            {
                _player.Play();
                Task.Delay(50).ContinueWith(_ => ApplyVolume());
            }
            else if (CurrentTrack != null)
            {
                _ = PlayTrackAsync(CurrentTrack);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] TogglePlayPause error: {ex.Message}");
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_player == null || !_isPlayerReady || _isDisposed) return;

        try
        {
            var length = _player.Length;
            if (length <= 0) return;

            var ms = (long)position.TotalMilliseconds;
            ms = Math.Clamp(ms, 0, length);

            _player.Time = ms;
            Debug.WriteLine($"[Audio] Seek to {position:mm\\:ss}");

            Task.Delay(100).ContinueWith(_ => ApplyVolume());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] Seek error: {ex.Message}");
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _session);
        _cts?.Cancel();

        StopPlaybackInternal();

        var stream = _currentStream;
        _currentStream = null;
        stream?.Dispose();

        CurrentTrack = null;
        IsLoading = false;
        _isPlayerReady = false;
        _isPlayingOrBuffering = false;

        SafeInvoke(() => OnTrackChanged?.Invoke(null));
        SafeInvoke(() => OnPlaybackStopped?.Invoke());
    }

    #endregion

    #region QUEUE

    public void Enqueue(TrackInfo track)
    {
        _queue.Enqueue(track);

        if (!IsPlaying && !IsPaused && !IsLoading)
        {
            _ = PlayTrackAsync(track);
            _queue.TryDequeue(out _);
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var t in tracks) Enqueue(t);
    }

    public void ClearQueue() => _queue.Clear();

    public async Task PlayNextAsync()
    {
        if (_isDisposed) return;

        TrackInfo? next = null;

        if (RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            next = CurrentTrack;
        }
        else if (ShuffleEnabled && _queue.Count > 0)
        {
            var list = _queue.ToList();
            int i = Random.Shared.Next(list.Count);
            next = list[i];
            list.RemoveAt(i);
            _queue.Clear();
            foreach (var t in list) _queue.Enqueue(t);
        }
        else if (_queue.TryDequeue(out var q))
        {
            next = q;
        }

        if (next != null)
            await PlayTrackAsync(next);
        else
            Stop();
    }

    public async Task PlayPreviousAsync()
    {
        if (_isDisposed) return;

        if (_historyIndex > 0)
        {
            _historyIndex--;
            await PlayTrackAsync(_history[_historyIndex]);
        }
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;

        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(track);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    #endregion

    private void SafeInvoke(Action action)
    {
        try { action(); } catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Debug.WriteLine("[AudioEngine] Disposing...");

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

            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
        }

        try { _currentMedia?.Dispose(); } catch { }
        try { _libVLC.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _playLock.Dispose(); } catch { }
        try { _apiLock.Dispose(); } catch { }
        try { _streamHttpClient.Dispose(); } catch { }
        try { _cacheManager.Dispose(); } catch { }

        Debug.WriteLine("[AudioEngine] Disposed");
    }
}