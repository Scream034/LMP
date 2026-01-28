using System.Diagnostics;
using LibVLCSharp.Shared;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using LMP.Core.Youtube;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Services;

public sealed class AudioEngine : ViewModelBase, IDisposable
{
    private const int ApiCooldownMs = 200;
    private const int QualitySwitchTimeoutSec = 8;
    private const int MaxHistorySize = 100;
    private const int RefreshTimeoutS = 60;

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly StreamCacheManager _cacheManager;
    private readonly HttpClient _httpClient;

    // LibVLC теперь не readonly, так как мы его пересоздаем при смене профиля
    private LibVLC? _libVLC;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _apiLock = new(1, 1);
    private readonly SemaphoreSlim _navigationLock = new(1, 1);

    private readonly List<TrackInfo> _queue = [];
    private int _currentIndex = -1;

    private readonly List<TrackInfo> _history = [];

    private MediaPlayer? _player;
    private Media? _currentMedia;
    private MemoryFirstCachingStream? _currentStream;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _playbackStartedTcs;

    private int _session;
    private int _volumePercent;
    private DateTime _lastApiCall = DateTime.MinValue;
    private DateTime _lastVolumeChange = DateTime.MinValue;

    private string _activeCodec = "";
    private string _activeContainer = "";
    private int _activeBitrate;

    private volatile bool _isDisposed;
    private volatile bool _isPlayerReady;
    private volatile bool _streamInfoReady;
    private volatile bool _volumeSavePending;
    private volatile bool _isNavigating;
    private volatile bool _suppressAutoNext;

    // Конфигурация стриминга (кэшируется при старте / смене профиля)
    private StreamingConfig _currentStreamingConfig;

    // === Properties ===

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }

    public IReadOnlyList<TrackInfo> Queue
    {
        get
        {
            lock (_queue) return _queue.ToList();
        }
    }

    public int CurrentQueueIndex => _currentIndex;

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

    // === Events ===

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action? OnStreamInfoReady;
    public event Action<bool, bool>? OnPlaybackStateChanged;
    public event Action? OnQueueChanged;

    public AudioEngine(YoutubeProvider youtube, LibraryService library, StreamCacheManager cacheManager)
    {
        _youtube = youtube;
        _library = library;
        _cacheManager = cacheManager;

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
        { Timeout = TimeSpan.FromMinutes(5) };

        // Без этого YouTube может возвращать 403 Forbidden на запросы скачивания.
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", YoutubeHttpHandler.UserAgentAndroid);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", YoutubeHttpHandler.GetHl());
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br, zstd");

        ShuffleEnabled = library.Settings.ShuffleEnabled;
        RepeatMode = library.Settings.RepeatMode;
        _volumePercent = NormalizeVolume(library.Settings.Volume);

        // Инициализация профиля
        _currentStreamingConfig = GetConfigForProfile(library.Settings.InternetProfile);

        InitializeLibVLC();
        InitializePlayer();

        _ = VolumeSaveLoopAsync();
        Log.Info($"[AudioEngine] Initialized. Volume: {_volumePercent}%, Profile: {library.Settings.InternetProfile}");
    }

    private void InitializeLibVLC()
    {
        _libVLC?.Dispose();

        LibVLCSharp.Shared.Core.Initialize();

        // Динамические параметры на основе профиля
        var caching = _currentStreamingConfig.VlcNetworkCachingMs;

        _libVLC = new LibVLC(
            "--no-video", "--no-embedded-video", "--no-spu", "--no-osd", "--no-stats",
            $"--network-caching={caching}",
            $"--file-caching={caching}",
            $"--live-caching={caching}",
            "--http-reconnect", "--http-continuous",
            "--audio-resampler=speex", "--aout=wasapi",
            "--clock-jitter=0", "--clock-synchro=0",
            "--avcodec-skiploopfilter=0", "--avcodec-skip-frame=0", "--avcodec-skip-idct=0"
        );
    }

    private void InitializePlayer()
    {
        _player?.Dispose();

        if (_libVLC != null)
        {
            _player = new MediaPlayer(_libVLC);
            _player.Playing += (_, _) => OnVlcPlaying();
            _player.Paused += OnVlcPaused;
            _player.Stopped += OnVlcStopped;
            _player.EndReached += (_, _) => OnVlcEndReached();
            _player.EncounteredError += (_, _) => OnVlcError();
            _player.TimeChanged += (_, e) => OnVlcTimeChanged(e.Time);
            ApplyVolume();
        }
    }

    public void ReinitializeWithProfile(InternetProfile profile)
    {
        Log.Info($"[AudioEngine] Switching profile to {profile}...");

        // 1. Stop gracefully
        Stop();

        // 2. Update config
        _currentStreamingConfig = GetConfigForProfile(profile);

        // 3. Re-init engine (requires UI thread usually for cleanup, but LibVLC handles it)
        InitializeLibVLC();
        InitializePlayer();

        Log.Info("[AudioEngine] Profile switched.");
    }

    private static StreamingConfig GetConfigForProfile(InternetProfile profile)
    {
        return profile switch
        {
            InternetProfile.Low => new StreamingConfig
            {
                ChunkSize = 64 * 1024,
                ReadAheadChunks = 2,
                MaxConcurrentDownloads = 2,
                VlcNetworkCachingMs = 4000,
                MaxRamChunks = 150 // ~9 MB
            },
            InternetProfile.Medium => new StreamingConfig
            {
                ChunkSize = 128 * 1024,
                ReadAheadChunks = 4,
                MaxConcurrentDownloads = 3,
                VlcNetworkCachingMs = 2000,
                MaxRamChunks = 100 // ~12 MB
            },
            InternetProfile.High => new StreamingConfig
            {
                ChunkSize = 256 * 1024,
                ReadAheadChunks = 6,
                MaxConcurrentDownloads = 4,
                VlcNetworkCachingMs = 1000,
                MaxRamChunks = 80 // ~20 MB
            },
            InternetProfile.Ultra => new StreamingConfig
            {
                ChunkSize = 512 * 1024,
                ReadAheadChunks = 10,
                MaxConcurrentDownloads = 6,
                VlcNetworkCachingMs = 500,
                MaxRamChunks = 60 // ~30 MB
            },
            _ => new StreamingConfig
            {
                ChunkSize = 128 * 1024,
                MaxRamChunks = 100
            }
        };
    }

    // === Volume ===

    public void ToggleMute()
    {
        if (_volumePercent > 0)
        {
            _library.UpdateSettings(s => s.LastVolume = _volumePercent);
            SetVolumeInstant(0);
        }
        else
        {
            int restoreVol = _library.Settings.LastVolume > 0 ? _library.Settings.LastVolume : 50;
            SetVolumeInstant(restoreVol);
        }
    }

    public float GetVolume() => _volumePercent;

    public void SetVolumeInstant(float value)
    {
        _volumePercent = Math.Clamp((int)Math.Round(value), 0, 500);
        _library.UpdateSettings(s => s.LastVolume = _volumePercent);
        _lastVolumeChange = DateTime.UtcNow;
        _volumeSavePending = true;
        Task.Run(ApplyVolume);
    }

    public void UpdateAudioSettings()
    {
        RaiseEvent(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
        Task.Run(ApplyVolume);
    }

    public void SaveVolumeNow()
    {
        if (!_volumeSavePending) return;
        _volumeSavePending = false;
        _library.UpdateSettings(s => s.Volume = _volumePercent);
    }

    private void ApplyVolume()
    {
        if (_player == null || _isDisposed) return;
        Try(() =>
        {
            float gain = MathF.Pow(10f, Math.Clamp(_library.Settings.TargetGainDb, -20f, 20f) / 20f);
            int finalVolume = Math.Clamp((int)Math.Round(_volumePercent * gain), 0, 500);

            if (_player.Volume != finalVolume)
            {
                _player.Volume = finalVolume;
            }
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

    // === Playback Core ===

    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null || _isDisposed) return;

        bool needsEvent = false;

        lock (_queue)
        {
            int existingIndex = _queue.FindIndex(t => t.Id == track.Id);

            if (existingIndex >= 0)
            {
                _currentIndex = existingIndex;
                _queue[existingIndex] = track;
            }
            else
            {
                _queue.Clear();
                _queue.Add(track);
                _currentIndex = 0;
                needsEvent = true;
            }
        }

        if (needsEvent) RaiseEvent(() => OnQueueChanged?.Invoke());

        await PlayCurrentIndexAsync();
    }

    private async Task PlayCurrentIndexAsync()
    {
        if (_isDisposed) return;

        if (!await _navigationLock.WaitAsync(500)) return;

        TrackInfo? trackToPlay;
        int session;

        try
        {
            if (_isNavigating) return;
            _isNavigating = true;

            lock (_queue)
            {
                if (_currentIndex < 0 || _currentIndex >= _queue.Count)
                {
                    _isNavigating = false;
                    return;
                }
                trackToPlay = _queue[_currentIndex];
            }

            if (trackToPlay == null)
            {
                _isNavigating = false;
                return;
            }

            session = Interlocked.Increment(ref _session);
        }
        finally
        {
            _navigationLock.Release();
        }

        await LoadAndPlayTrackAsync(trackToPlay, session);
    }

    private bool TryAdvanceQueue(bool userInitiated)
    {
        lock (_queue)
        {
            if (_queue.Count == 0) return false;

            if (!userInitiated && RepeatMode == RepeatMode.RepeatOne)
                return true;

            if (_currentIndex + 1 < _queue.Count)
            {
                _currentIndex++;
                return true;
            }

            if (RepeatMode == RepeatMode.RepeatAll)
            {
                _currentIndex = 0;
                return true;
            }

            return false;
        }
    }

    private bool TryRetreatQueue()
    {
        lock (_queue)
        {
            if (_queue.Count == 0) return false;

            if (_currentIndex - 1 >= 0)
            {
                _currentIndex--;
                return true;
            }

            if (RepeatMode == RepeatMode.RepeatAll)
            {
                _currentIndex = _queue.Count - 1;
                return true;
            }

            return false;
        }
    }

    private async Task LoadAndPlayTrackAsync(TrackInfo trackToPlay, int session)
    {
        try
        {
            // Identity Map Refactoring:
            // Нам не нужно загружать из словаря, т.к. trackToPlay - это и есть живой объект из Registry.
            // Но мы можем убедиться, что если это новый трек, он будет корректно зарегистрирован в персистентности при изменении.
            // SyncTrackPreferences был удален как устаревший, так как мы работаем с Identity Map.

            _suppressAutoNext = true;

            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            if (oldCts != null) Try(oldCts.Cancel);

            await CleanupCurrentMediaAsync();

            if (_session != session) return;

            ResetStreamInfo();

            IsLoading = true;
            CurrentTrack = trackToPlay;
            _isPlayerReady = false;

            RaiseEvent(() => OnTrackChanged?.Invoke(trackToPlay));
            RaiseEvent(() => OnQueueChanged?.Invoke());

            var cts = _cts;

            _ = Task.Run(async () =>
            {
                if (!await _loadLock.WaitAsync(2000)) return;

                try
                {
                    if (_session != session || cts.IsCancellationRequested) return;
                    await PlayTrackInternalAsync(trackToPlay, session, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error($"[AudioEngine] PlayTrackInternal error: {ex.Message}");
                    IsLoading = false;
                }
                finally
                {
                    _loadLock.Release();
                }
            });
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private async Task RestartCurrentTrackInternalAsync()
    {
        TrackInfo? trackToPlay = CurrentTrack;
        if (trackToPlay == null) return;

        var session = Interlocked.Increment(ref _session);
        _suppressAutoNext = true;

        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        if (oldCts != null) Try(oldCts.Cancel);

        await CleanupCurrentMediaAsync();

        if (_session != session) return;

        ResetStreamInfo();
        _isPlayerReady = false;

        var cts = _cts;

        _ = Task.Run(async () =>
        {
            if (!await _loadLock.WaitAsync(2000)) return;
            try
            {
                if (_session != session || cts.IsCancellationRequested) return;
                await PlayTrackInternalAsync(trackToPlay, session, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error($"[AudioEngine] Restart error: {ex.Message}"); }
            finally { _loadLock.Release(); }
        });
    }

    private async Task CleanupCurrentMediaAsync()
    {
        var oldMedia = _currentMedia;
        var oldStream = _currentStream;
        var player = _player;

        _currentMedia = null;
        _currentStream = null;

        if (oldStream == null && oldMedia == null) return;

        try { oldStream?.CancelPendingReads(); } catch { }

        if (player != null && player.Media == oldMedia) player.Media = null;

        if (player != null && player.State != VLCState.Stopped && player.State != VLCState.Error)
        {
            await Task.Run(() => Try(player.Stop));
        }

        _ = Task.Run(() =>
        {
            try
            {
                Thread.Sleep(100);
                oldStream?.Dispose();
                oldMedia?.Dispose();
            }
            catch { }
        });
    }

    public async Task SwitchQualityAsync(string container, int targetBitrate = 0)
    {
        if (CurrentTrack == null) return;

        var position = CurrentPosition;
        var track = CurrentTrack;

        // Обновляем состояние текущего объекта (Identity Map)
        track.TransientContainer = container;
        track.TransientBitrate = targetBitrate;

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredContainer = container;
            track.PreferredBitrate = targetBitrate;

            // Сохраняем изменения. Так как объект canonical, просто говорим библиотеке обновить его статус.
            _ = _library.AddOrUpdateTrackAsync(track);
        }

        track.StreamUrl = string.Empty;
        _playbackStartedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await PlayCurrentIndexAsync();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(QualitySwitchTimeoutSec));
            await _playbackStartedTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
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

        MemoryFirstCachingStream? cacheStream = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            StreamDetails? stream = null;
            try
            {
                stream = await GetOrRefreshStreamAsync(track, forceRefresh: false, ct);
            }
            catch (OperationCanceledException) { return; }

            if (stream == null)
            {
                if (ct.IsCancellationRequested) return;
                throw new Exception("Failed to get stream URL");
            }

            if (_session != session) return;
            ct.ThrowIfCancellationRequested();

            SetStreamInfo(stream.Codec, stream.Bitrate, stream.Container);

            bool isManualQualityOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);

            long size = stream.Size;
            if (size <= 0 && !isManualQualityOverride)
            {
                size = await TryGetContentLengthAsync(stream.Url, ct);
            }

            if (_session != session || ct.IsCancellationRequested) return;

            if (size > 0)
            {
                string cacheId = isManualQualityOverride
                    ? $"{track.Id}_{stream.Container}_{stream.Bitrate}"
                    : track.Id;

                cacheStream = new MemoryFirstCachingStream(
                    cacheId,
                    stream.Url,
                    size,
                    _httpClient,
                    _cacheManager,
                    _currentStreamingConfig,
                    urlRefresher: async (token) =>
                    {
                        var newStream = await GetOrRefreshStreamAsync(track, forceRefresh: true, token);
                        return newStream?.Url;
                    }
                );

                var preBufferResult = await cacheStream.PreBufferAsync(ct);
                if (!preBufferResult)
                {
                    cacheStream.Dispose();
                    cacheStream = null;
                    if (ct.IsCancellationRequested || _session != session) return;
                }
            }

            if (_session != session || ct.IsCancellationRequested)
            {
                cacheStream?.Dispose();
                return;
            }

            if (cacheStream != null)
            {
                StartPlayback(new Media(_libVLC!, new StreamMediaInput(cacheStream)), cacheStream, track);
                cacheStream = null;
            }
            else
            {
                StartPlayback(new Media(_libVLC!, stream.Url, FromType.FromLocation), null, track);
            }

            Log.Info($"[AudioEngine] Loaded in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) { cacheStream?.Dispose(); }
        catch (Exception ex)
        {
            cacheStream?.Dispose();
            if (!ct.IsCancellationRequested && _session == session)
            {
                Log.Error($"[AudioEngine] Error: {ex.Message}");
                RaiseEvent(() => OnError?.Invoke(ex.Message));
            }
        }
        finally { IsLoading = false; }
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
        await CleanupCurrentMediaAsync();
        _isPlayerReady = false;
    }

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (_isDisposed || _player == null) return;

        await WithLock(_commandLock, () => Task.Run(() =>
        {
            var state = _player.State;
            if (shouldPlay)
            {
                if (state == VLCState.Paused) _player.SetPause(false);
                else if (state is VLCState.Stopped or VLCState.Ended or VLCState.Error)
                {
                    if (CurrentTrack != null) _ = PlayCurrentIndexAsync();
                }
                else _player.Play();

                IsPlaying = true;
                IsPaused = false;
            }
            else
            {
                if (state is VLCState.Playing or VLCState.Buffering or VLCState.Opening) _player.Pause();
                IsPlaying = false;
                IsPaused = true;
            }
            NotifyPlaybackState();
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
        _suppressAutoNext = true;
        Interlocked.Increment(ref _session);
        Try(() => _cts?.Cancel());
        _ = StopPlaybackAsync();

        ResetStreamInfo();

        CurrentTrack = null;
        IsLoading = false;
        IsPlaying = false;
        IsPaused = false;

        RaiseEvent(() => OnTrackChanged?.Invoke(null));
        RaiseEvent(() => OnPlaybackStopped?.Invoke());
        NotifyPlaybackState();
    }

    // === Navigation / Queue Management ===

    public async Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        if (_isDisposed) return;

        lock (_queue)
        {
            _queue.Clear();
            _queue.AddRange(tracks);
            _currentIndex = _queue.FindIndex(t => t.Id == startTrack.Id);
            if (_currentIndex == -1 && _queue.Count > 0) _currentIndex = 0;
        }

        RaiseEvent(() => OnQueueChanged?.Invoke());
        await PlayCurrentIndexAsync();
    }

    public async Task PlayNextAsync()
    {
        if (_isDisposed || _isNavigating) return;
        if (TryAdvanceQueue(userInitiated: true)) await PlayCurrentIndexAsync();
        else Stop();
    }

    public async Task PlayPreviousAsync()
    {
        if (_isDisposed || _isNavigating) return;

        if (CurrentPosition.TotalSeconds > 3 && _isPlayerReady)
        {
            await PlayCurrentIndexAsync();
            return;
        }

        if (TryRetreatQueue()) await PlayCurrentIndexAsync();
        else if (_isPlayerReady && _player != null) _player.Time = 0;
    }

    public void Enqueue(TrackInfo track)
    {
        lock (_queue)
        {
            if (_queue.Any(t => t.Id == track.Id)) return;
            _queue.Add(track);
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());

        if (CurrentTrack == null && !IsPlaying && !IsLoading)
        {
            lock (_queue) _currentIndex = _queue.Count - 1;
            _ = PlayCurrentIndexAsync();
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;

        lock (_queue)
        {
            var existingIds = _queue.Select(t => t.Id).ToHashSet();
            var newTracks = list.Where(t => !existingIds.Contains(t.Id)).ToList();
            if (newTracks.Count == 0) return;
            _queue.AddRange(newTracks);
        }

        RaiseEvent(() => OnQueueChanged?.Invoke());
        if (CurrentTrack == null && !IsPlaying && !IsLoading) _ = PlayNextAsync();
    }

    public void ClearQueue()
    {
        lock (_queue)
        {
            _queue.Clear();
            _currentIndex = -1;
            if (CurrentTrack != null)
            {
                _queue.Add(CurrentTrack);
                _currentIndex = 0;
            }
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());
    }

    public void ShuffleQueue()
    {
        lock (_queue)
        {
            if (_queue.Count < 2) return;
            var current = (_currentIndex >= 0 && _currentIndex < _queue.Count) ? _queue[_currentIndex] : null;

            var rng = Random.Shared;
            int n = _queue.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }

            if (current != null)
            {
                _currentIndex = _queue.IndexOf(current);
                if (_currentIndex == -1)
                {
                    _currentIndex = 0;
                    _queue.Insert(0, current);
                }
            }
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());
    }

    public void RemoveFromQueue(TrackInfo track)
    {
        bool changed = false;
        bool needStop = false;

        lock (_queue)
        {
            int index = _queue.FindIndex(t => t.Id == track.Id);
            if (index == -1) return;

            if (index == _currentIndex)
            {
                if (index == _queue.Count - 1) _currentIndex--;
                needStop = _queue.Count == 1;
            }
            else if (index < _currentIndex) _currentIndex--;

            _queue.RemoveAt(index);
            changed = true;
        }

        if (changed) RaiseEvent(() => OnQueueChanged?.Invoke());
        if (needStop) Stop();
    }

    public void MoveQueueItem(int oldIndex, int newIndex)
    {
        lock (_queue)
        {
            if (oldIndex < 0 || oldIndex >= _queue.Count || newIndex < 0 || newIndex >= _queue.Count) return;
            if (oldIndex == newIndex) return;

            var item = _queue[oldIndex];
            _queue.RemoveAt(oldIndex);
            _queue.Insert(newIndex, item);

            if (_currentIndex == oldIndex) _currentIndex = newIndex;
            else if (oldIndex < _currentIndex && newIndex >= _currentIndex) _currentIndex--;
            else if (oldIndex > _currentIndex && newIndex <= _currentIndex) _currentIndex++;
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;
        _history.Add(track);
        if (_history.Count > MaxHistorySize) _history.RemoveAt(0);
    }

    // === Stream Info & API ===

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo() =>
        CurrentTrack?.IsDownloaded == true
            ? (Path.GetExtension(CurrentTrack.LocalPath)?.TrimStart('.').ToUpper() ?? "FILE", 0, true)
            : (_activeCodec, _activeBitrate, _streamInfoReady);

    public long GetDownloadedBytes() =>
        _currentStream != null ? (long)(_currentStream.DownloadProgress / 100 * _currentStream.Length) : 0;

    private void ResetStreamInfo() =>
        (_activeCodec, _activeBitrate, _activeContainer, _streamInfoReady) = ("", 0, "", false);

    private void SetStreamInfo(string codec, int bitrate, string container)
    {
        (_activeCodec, _activeBitrate, _activeContainer, _streamInfoReady) = (codec, bitrate, container, true);
        RaiseEvent(() => OnStreamInfoReady?.Invoke());
    }

    private record StreamDetails(string Url, long Size, int Bitrate, string Codec, string Container);

    private async Task<StreamDetails?> GetOrRefreshStreamAsync(TrackInfo track, bool forceRefresh, CancellationToken ct)
    {
        bool hasUserOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);

        // Используем кэшированные метаданные, если трек есть на диске
        if (!forceRefresh && !hasUserOverride && _cacheManager.IsFullyCached(track.Id))
        {
            var meta = _cacheManager.TryGetMetadata(track.Id);
            if (meta != null && meta.ContentLength > 0 && !string.IsNullOrEmpty(meta.Codec))
            {
                Log.Info($"[AudioEngine] Track {track.Id} is fully cached.");
                track.CachedBitrate = meta.Bitrate;
                track.CachedCodec = meta.Codec;
                track.CachedContainer = meta.Container;
                return new StreamDetails(meta.SourceUrl, meta.ContentLength, meta.Bitrate, meta.Codec, meta.Container);
            }
        }

        bool needFresh = forceRefresh || string.IsNullOrEmpty(track.StreamUrl) || string.IsNullOrEmpty(track.CachedCodec) || hasUserOverride;

        if (!needFresh)
            return new(track.StreamUrl, -1, track.CachedBitrate, track.CachedCodec, track.CachedContainer);

        return await WithLock(_apiLock, async () =>
        {
            await ThrottleApiCall(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(RefreshTimeoutS));

            var result = await _youtube.RefreshStreamUrlAsync(track, forceRefresh, cts.Token);
            _lastApiCall = DateTime.UtcNow;

            if (!result.HasValue) return null;

            if (!hasUserOverride)
                _cacheManager.UpdateStreamInfo(track.Id, result.Value.Codec, result.Value.Bitrate, result.Value.Container);

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
            // Добавляем UA и здесь, на всякий случай, хотя _httpClient уже настроен
            // Но HeadAsync может использовать внутренний клиент, если это Extension метод на HttpClient
            // В данном случае мы используем _httpClient.SendAsync
            using var resp = await _httpClient.SendAsync(req, cts.Token);
            return resp.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    // === VLC Events ===

    private void OnVlcPlaying()
    {
        if (_isDisposed) return;
        _suppressAutoNext = false;
        _isPlayerReady = true;
        IsLoading = false;
        IsPlaying = true;
        IsPaused = false;

        ApplyVolume();

        // Workaround for VLC resetting volume on start
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250);
                if (!_isDisposed && IsPlaying && _player != null)
                {
                    ApplyVolume();
                }
            }
            catch { }
        });

        NotifyPlaybackState();
        _playbackStartedTcs?.TrySetResult(true);
    }

    private void OnVlcPaused(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        IsPlaying = false;
        IsPaused = true;
        NotifyPlaybackState();
    }

    private void OnVlcStopped(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        _isPlayerReady = false;
        IsPlaying = false;
        IsPaused = false;
        NotifyPlaybackState();
    }

    private void OnVlcEndReached()
    {
        if (_isDisposed) return;
        if (_suppressAutoNext) return;

        IsPlaying = false;
        IsPaused = false;
        _isPlayerReady = false;
        NotifyPlaybackState();

        var session = Interlocked.Increment(ref _session);
        _ = Task.Run(async () =>
        {
            try
            {
                if (_isDisposed || _session != session) return;
                if (TryAdvanceQueue(userInitiated: false))
                {
                    if (_session == session) await PlayCurrentIndexAsync();
                }
                else Stop();
            }
            catch (Exception ex) { Log.Error($"[AudioEngine] Error in OnVlcEndReached: {ex.Message}"); }
        });
    }

    private void OnVlcError()
    {
        IsLoading = false;
        IsPlaying = false;
        IsPaused = false;
        RaiseEvent(() => OnError?.Invoke("VLC playback error"));
        NotifyPlaybackState();
    }

    private void OnVlcTimeChanged(long time)
    {
        if (_isDisposed || !_isPlayerReady) return;
        long length = _player?.Length ?? 0;
        if (length > 0 && time > length) time = length;
        RaiseEvent(() => OnPositionChanged?.Invoke(TimeSpan.FromMilliseconds(time)));
    }

    private void NotifyPlaybackState() => RaiseEvent(() => OnPlaybackStateChanged?.Invoke(IsPlaying, IsPaused));

    // === Helpers ===

    private static void RaiseEvent(Action action)
    {
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
            else Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
        catch (Exception ex) { Log.Error($"[AudioEngine] Event error: {ex.Message}"); }
    }

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

        Try(_libVLC!.Dispose);
        Try(_loadLock.Dispose);
        Try(_commandLock.Dispose);
        Try(_navigationLock.Dispose);
        Try(_apiLock.Dispose);
        Try(_httpClient.Dispose);

        Log.Info("[AudioEngine] Disposed");
    }
}