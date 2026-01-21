using System.Diagnostics;
using LibVLCSharp.Shared;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.ViewModels;
using ReactiveUI;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Центральный аудио движок: воспроизведение, очередь, громкость, кэширование потоков.
/// </summary>
public sealed class AudioEngine : ViewModelBase, IDisposable
{
    private const int ApiCooldownMs = 200;
    private const int QualitySwitchTimeoutSec = 8;
    private const int MaxHistorySize = 100;

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly StreamCacheManager _cacheManager;
    private readonly HttpClient _httpClient;
    private readonly LibVLC _libVLC;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _apiLock = new(1, 1);
    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];

    private MediaPlayer? _player;
    private Media? _currentMedia;
    private MemoryFirstCachingStream? _currentStream;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _playbackStartedTcs;

    private int _session;
    private int _volumePercent;
    private int _historyIndex = -1;
    private DateTime _lastApiCall = DateTime.MinValue;
    private DateTime _lastVolumeChange = DateTime.MinValue;

    private string _activeCodec = "";
    private string _activeContainer = "";
    private int _activeBitrate;

    private volatile bool _isDisposed;
    private volatile bool _isPlayerReady;
    private volatile bool _isPlayingOrBuffering;
    private volatile bool _streamInfoReady;
    private volatile bool _volumeSavePending;

    // === Properties ===

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying => _player?.IsPlaying ?? false;
    public bool IsPaused => _player?.State == VLCState.Paused;
    public string VlcStateString => _player?.State.ToString() ?? "NULL";
    public double BufferProgress => _currentStream?.DownloadProgress ?? 0;
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    public TimeSpan CurrentPosition => TryGet(() =>
        _player?.Time is >= 0 and var t ? TimeSpan.FromMilliseconds(t) : TimeSpan.Zero);

    public TimeSpan TotalDuration => TryGet(() =>
        _player?.Length is > 0 and var len
            ? TimeSpan.FromMilliseconds(len)
            : CurrentTrack?.Duration ?? TimeSpan.Zero);

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value, () => RaiseEvent(() => OnLoadingChanged?.Invoke(value)));
    }

    // === Events ===

    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action? OnStreamInfoReady;
    public event Action<bool, bool>? OnPlaybackStateChanged;

    // === Constructor ===

    public AudioEngine(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
        _cacheManager = new StreamCacheManager();

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        }) { Timeout = TimeSpan.FromMinutes(5) };

        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;
        _volumePercent = NormalizeVolume(library.Data.Volume);

        Core.Initialize();
        _libVLC = new LibVLC(
            "--no-video", "--no-embedded-video", "--no-spu", "--no-osd", "--no-stats",
            "--network-caching=1024", "--file-caching=512", "--live-caching=512",
            "--http-reconnect", "--http-continuous",
            "--audio-resampler=speex", "--aout=wasapi",
            "--clock-jitter=0", "--clock-synchro=0",
            "--avcodec-skiploopfilter=0", "--avcodec-skip-frame=0", "--avcodec-skip-idct=0"
        );

        InitializePlayer();
        _ = VolumeSaveLoopAsync();
        Log.Info($"[AudioEngine] Initialized. Volume: {_volumePercent}%");
    }

    private void InitializePlayer()
    {
        _player = new MediaPlayer(_libVLC);
        _player.Playing += (_, _) => OnVlcPlaying();
        _player.Paused += (_, _) => NotifyPlaybackState();
        _player.Stopped += (_, _) => { _isPlayerReady = false; NotifyPlaybackState(); };
        _player.EndReached += (_, _) => OnVlcEndReached();
        _player.EncounteredError += (_, _) => OnVlcError();
        _player.TimeChanged += (_, e) => OnVlcTimeChanged(e.Time);
        ApplyVolume();
    }

    // === Volume ===

    public float GetVolume() => _volumePercent;

    public void SetVolumeInstant(float value)
    {
        _volumePercent = Math.Clamp((int)Math.Round(value), 0, 500);
        _library.Data.Volume = _volumePercent;
        _lastVolumeChange = DateTime.UtcNow;
        _volumeSavePending = true;
        Task.Run(ApplyVolume);
    }

    public void UpdateAudioSettings()
    {
        RaiseEvent(() => OnMaxVolumeChanged?.Invoke(_library.Data.MaxVolumeLimit));
        Task.Run(ApplyVolume);
    }

    public void SaveVolumeNow()
    {
        if (!_volumeSavePending) return;
        _volumeSavePending = false;
        _library.Save();
        Log.Info("[AudioEngine] Volume saved");
    }

    private void ApplyVolume()
    {
        if (_player == null || _isDisposed) return;

        Try(() =>
        {
            float gain = MathF.Pow(10f, Math.Clamp(_library.Data.TargetGainDb, -20f, 20f) / 20f);
            _player.Volume = Math.Clamp((int)Math.Round(_volumePercent * gain), 0, 500);
        });
    }

    private async Task VolumeSaveLoopAsync()
    {
        while (!_isDisposed)
        {
            await Task.Delay(2000);
            if (_volumeSavePending && (DateTime.UtcNow - _lastVolumeChange).TotalSeconds >= 1.5)
                SaveVolumeNow();
        }
    }

    private static int NormalizeVolume(float saved) =>
        Math.Clamp(saved is <= 1f and > 0 ? (int)(saved * 100) : (int)saved, 0, 500);

    // === Playback ===

    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null || _isDisposed) return;
        Log.Info($"[AudioEngine] Play: {track.Title}");

        SyncTrackPreferences(track);

        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        var session = Interlocked.Increment(ref _session);
        Try(() => oldCts?.Cancel());

        ResetStreamInfo();
        IsLoading = true;
        CurrentTrack = track;
        _isPlayerReady = false;
        _isPlayingOrBuffering = true;

        RaiseEvent(() => OnTrackChanged?.Invoke(track));

        _ = Task.Run(async () =>
        {
            if (!await _loadLock.WaitAsync(500)) return;
            try { await PlayTrackInternalAsync(track, session, _cts.Token); }
            finally { _loadLock.Release(); }
        });
    }

    public async Task SwitchQualityAsync(string container, int targetBitrate = 0)
    {
        if (CurrentTrack == null) return;
        Log.Info($"[AudioEngine] Switch quality: {container}/{targetBitrate}kbps");

        var position = CurrentPosition;
        var track = CurrentTrack;

        track.TransientContainer = container;
        track.TransientBitrate = targetBitrate;

        if (_library.Data.RememberTrackFormat)
        {
            track.PreferredContainer = container;
            track.PreferredBitrate = targetBitrate;
            SaveTrackPreference(track);
        }

        track.StreamUrl = string.Empty;
        _playbackStartedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await PlayTrackAsync(track);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(QualitySwitchTimeoutSec));
            await _playbackStartedTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) { Log.Warn("[AudioEngine] Quality switch timeout"); }
        finally { _playbackStartedTcs = null; }

        if (position.TotalSeconds > 1)
        {
            await Task.Delay(200);
            await SeekAsync(position);
        }
    }

    private async Task PlayTrackInternalAsync(TrackInfo track, int session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await StopPlaybackAsync();

        try
        {
            var stream = await GetOrRefreshStreamAsync(track, ct);
            if (stream == null) throw new Exception("Failed to get stream URL");
            if (_session != session || ct.IsCancellationRequested) return;

            SetStreamInfo(stream.Codec, stream.Bitrate, stream.Container);

            long size = stream.Size > 0 ? stream.Size : await TryGetContentLengthAsync(stream.Url, ct);

            if (size <= 0)
            {
                StartPlayback(new Media(_libVLC, stream.Url, FromType.FromLocation), null, track);
                return;
            }

            var cacheStream = new MemoryFirstCachingStream(track.Id, stream.Url, size, _httpClient, _cacheManager);
            await cacheStream.PreBufferAsync(ct);

            if (_session != session || ct.IsCancellationRequested) { cacheStream.Dispose(); return; }

            StartPlayback(new Media(_libVLC, new StreamMediaInput(cacheStream)), cacheStream, track);
            Log.Info($"[AudioEngine] Loaded in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) { _isPlayingOrBuffering = false; }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Error: {ex.Message}");
            RaiseEvent(() => OnError?.Invoke(ex.Message));
            IsLoading = false;
            _isPlayingOrBuffering = false;
        }
    }

    private void StartPlayback(Media media, MemoryFirstCachingStream? stream, TrackInfo track)
    {
        var (oldMedia, oldStream) = (_currentMedia, _currentStream);
        (_currentMedia, _currentStream) = (media, stream);

        Task.Run(() => { Try(() => oldStream?.Dispose()); Try(() => oldMedia?.Dispose()); });

        if (_player == null) return;
        _player.Media = media;
        ApplyVolume();
        _player.Play();
        AddToHistory(track);
    }

    private async Task StopPlaybackAsync()
    {
        if (_player == null) return;

        Try(() => { if (_player.State != VLCState.Stopped) _player.Stop(); });

        var (oldStream, oldMedia) = (_currentStream, _currentMedia);
        (_currentStream, _currentMedia, _isPlayerReady) = (null, null, false);

        if (oldStream != null || oldMedia != null)
        {
            await Task.Run(() =>
            {
                Try(() => oldStream?.Dispose());
                Try(() => oldMedia?.Dispose());
            });
        }
    }

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (_isDisposed || _player == null) return;

        await WithLock(_commandLock, () => Task.Run(() =>
        {
            var state = _player.State;
            if (shouldPlay)
            {
                switch (state)
                {
                    case VLCState.Playing: break;
                    case VLCState.Paused: _player.SetPause(false); break;
                    case VLCState.Stopped or VLCState.Ended or VLCState.Error:
                        if (CurrentTrack != null) _ = PlayTrackAsync(CurrentTrack);
                        else _player.Play();
                        break;
                    default: _player.Play(); break;
                }
            }
            else if (state is VLCState.Playing or VLCState.Buffering or VLCState.Opening)
            {
                _player.Pause();
            }
        }));
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_player == null || !_isPlayerReady || _isDisposed) return;

        await WithLock(_commandLock, () => Task.Run(() =>
            _player.Time = (long)Math.Clamp(position.TotalMilliseconds, 0, _player.Length)));
    }

    public void Stop()
    {
        Interlocked.Increment(ref _session);
        Try(() => _cts?.Cancel());
        _ = StopPlaybackAsync();

        ResetStreamInfo();
        CurrentTrack = null;
        IsLoading = false;
        _isPlayingOrBuffering = false;

        RaiseEvent(() => OnTrackChanged?.Invoke(null));
        RaiseEvent(() => OnPlaybackStopped?.Invoke());
        NotifyPlaybackState();
    }

    // === Navigation ===

    public async Task PlayNextAsync()
    {
        TrackInfo? next = RepeatMode == RepeatMode.RepeatOne ? CurrentTrack : GetNextFromQueue();
        if (next != null) await PlayTrackAsync(next);
        else Stop();
    }

    public async Task PlayPreviousAsync()
    {
        if (_historyIndex > 0)
            await PlayTrackAsync(_history[--_historyIndex]);
        else if (CurrentTrack != null)
            await SeekAsync(TimeSpan.Zero);
    }

    public void Enqueue(TrackInfo track)
    {
        _queue.Enqueue(track);
        if (!IsPlaying && !IsPaused && !IsLoading)
        {
            _ = PlayTrackAsync(track);
            _queue.TryDequeue(out _);
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks) => tracks.ToList().ForEach(Enqueue);
    public void ClearQueue() => _queue.Clear();

    private TrackInfo? GetNextFromQueue()
    {
        if (ShuffleEnabled && _queue.Count > 0)
        {
            var list = _queue.ToList();
            int idx = Random.Shared.Next(list.Count);
            var track = list[idx];
            list.RemoveAt(idx);
            _queue.Clear();
            list.ForEach(_queue.Enqueue);
            return track;
        }
        return _queue.TryDequeue(out var t) ? t : null;
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;
        _history.Add(track);
        if (_history.Count > MaxHistorySize) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    // === Stream Info ===

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo() =>
        CurrentTrack?.IsDownloaded == true
            ? (Path.GetExtension(CurrentTrack.LocalPath)?.TrimStart('.').ToUpper() ?? "FILE", 0, true)
            : (_activeCodec, _activeBitrate, _streamInfoReady);

    public long GetDownloadedBytes() =>
        _currentStream != null ? (long)(_currentStream.DownloadProgress / 100 * _currentStream.Length) : 0;

    public async Task PrefetchAsync(TrackInfo track)
    {
        if (_isPlayingOrBuffering || track.IsDownloaded) return;
        await WithLock(_apiLock, async () =>
        {
            using var cts = new CancellationTokenSource(5000);
            await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            _lastApiCall = DateTime.UtcNow;
        });
    }

    private void ResetStreamInfo() =>
        (_activeCodec, _activeBitrate, _activeContainer, _streamInfoReady) = ("", 0, "", false);

    private void SetStreamInfo(string codec, int bitrate, string container)
    {
        (_activeCodec, _activeBitrate, _activeContainer, _streamInfoReady) = (codec, bitrate, container, true);
        RaiseEvent(() => OnStreamInfoReady?.Invoke());
    }

    private record StreamDetails(string Url, long Size, int Bitrate, string Codec, string Container);

    private async Task<StreamDetails?> GetOrRefreshStreamAsync(TrackInfo track, CancellationToken ct)
    {
        bool needFresh = string.IsNullOrEmpty(track.StreamUrl)
            || string.IsNullOrEmpty(track.CachedCodec)
            || track.CachedBitrate <= 0;

        if (!needFresh)
            return new(track.StreamUrl, -1, track.CachedBitrate, track.CachedCodec, track.CachedContainer);

        return await WithLock(_apiLock, async () =>
        {
            await ThrottleApiCall(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var result = await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            _lastApiCall = DateTime.UtcNow;

            if (!result.HasValue) return null;

            track.CachedCodec = result.Value.Codec;
            track.CachedBitrate = result.Value.Bitrate;
            track.CachedContainer = result.Value.Container;

            return new StreamDetails(result.Value.Url, result.Value.Size,
                result.Value.Bitrate, result.Value.Codec, result.Value.Container);
        });
    }

    private async Task ThrottleApiCall(CancellationToken ct)
    {
        var elapsed = (DateTime.UtcNow - _lastApiCall).TotalMilliseconds;
        if (elapsed < ApiCooldownMs) await Task.Delay(ApiCooldownMs, ct);
    }

    private async Task<long> TryGetContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _httpClient.SendAsync(req, cts.Token);
            return resp.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    // === VLC Events ===

    private void OnVlcPlaying()
    {
        if (_isDisposed) return;
        _isPlayerReady = true;
        IsLoading = false;
        ApplyVolume();
        NotifyPlaybackState();
        _playbackStartedTcs?.TrySetResult(true);
    }

    private void OnVlcEndReached()
    {
        if (_isDisposed) return;
        NotifyPlaybackState();
        _ = Task.Run(async () => { await Task.Delay(50); if (!_isDisposed) await PlayNextAsync(); });
    }

    private void OnVlcError()
    {
        RaiseEvent(() => OnError?.Invoke("VLC playback error"));
        IsLoading = false;
        NotifyPlaybackState();
    }

    private void OnVlcTimeChanged(long time)
    {
        if (_isDisposed || !_isPlayerReady) return;
        RaiseEvent(() => OnPositionChanged?.Invoke(TimeSpan.FromMilliseconds(time)));
    }

    private void NotifyPlaybackState() =>
        RaiseEvent(() => OnPlaybackStateChanged?.Invoke(IsPlaying, IsPaused));

    // === Helpers ===

    private void SyncTrackPreferences(TrackInfo track)
    {
        if (!_library.Data.Tracks.TryGetValue(track.Id, out var saved)) return;
        if (string.IsNullOrEmpty(track.PreferredContainer) && !string.IsNullOrEmpty(saved.PreferredContainer))
        {
            track.PreferredContainer = saved.PreferredContainer;
            track.PreferredBitrate = saved.PreferredBitrate;
        }
    }

    private void SaveTrackPreference(TrackInfo track)
    {
        if (_library.Data.Tracks.TryGetValue(track.Id, out var saved))
        {
            saved.PreferredContainer = track.PreferredContainer;
            saved.PreferredBitrate = track.PreferredBitrate;
        }
        else
        {
            _library.Data.Tracks[track.Id] = track.Clone();
        }
        _library.Save();
    }

    private void SetProperty<T>(ref T field, T value, Action? onChange = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        this.RaisePropertyChanged();
        onChange?.Invoke();
    }

    private static void RaiseEvent(Action action) => Try(action);
    private static void Try(Action action) { try { action(); } catch { } }
    private static T TryGet<T>(Func<T> func, T fallback = default!) { try { return func(); } catch { return fallback; } }

    private static async Task WithLock(SemaphoreSlim sem, Func<Task> action)
    {
        await sem.WaitAsync();
        try { await action(); }
        finally { sem.Release(); }
    }

    private static async Task<T?> WithLock<T>(SemaphoreSlim sem, Func<Task<T?>> action)
    {
        await sem.WaitAsync();
        try { return await action(); }
        finally { sem.Release(); }
    }

    // === Dispose ===

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        SaveVolumeNow();
        Try(() => _cts?.Cancel());
        Try(() => _currentStream?.Dispose());

        if (_player != null)
        {
            Try(_player.Stop);
            Try(_player.Dispose);
        }

        Try(_libVLC.Dispose);
        Try(_loadLock.Dispose);
        Try(_commandLock.Dispose);
        Try(_apiLock.Dispose);
        Try(_httpClient.Dispose);
        Try(_cacheManager.Dispose);

        Log.Info("[AudioEngine] Disposed");
    }
}