using System.Diagnostics;
using LibVLCSharp.Shared;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Core.Services;

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
        })
        { Timeout = TimeSpan.FromMinutes(5) };

        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;
        _volumePercent = NormalizeVolume(library.Data.Volume);

        LibVLCSharp.Shared.Core.Initialize();
        _libVLC = new LibVLC(
            "--no-video", "--no-embedded-video", "--no-spu", "--no-osd", "--no-stats",
            "--network-caching=1024", "--file-caching=1024", "--live-caching=1024",
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
        _player.Paused += OnVlcPaused;
        _player.Stopped += OnVlcStopped;
        _player.EndReached += (_, _) => OnVlcEndReached();
        _player.EncounteredError += (_, _) => OnVlcError();
        _player.TimeChanged += (_, e) => OnVlcTimeChanged(e.Time);
        ApplyVolume();
    }

    // === Volume ===

    public void ToggleMute()
    {
        Log.Info($"[AudioEngine] ToggleMute: {_volumePercent} ({_library.Data.LastVolume})");

        if (_volumePercent > 0)
        {
            _library.Data.LastVolume = _volumePercent;
            SetVolumeInstant(0);
        }
        else
        {
            int restoreVol = _library.Data.LastVolume > 0 ? _library.Data.LastVolume : 50;
            SetVolumeInstant(restoreVol);
        }
    }

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
        if (_isNavigating) return;

        if (!await _navigationLock.WaitAsync(500))
        {
            Log.Warn("[AudioEngine] PlayCurrentIndexAsync timeout waiting for lock");
            return;
        }

        try
        {
            await PlayCurrentIndexInternalAsync();
        }
        finally
        {
            _navigationLock.Release();
        }
    }

    private async Task PlayCurrentIndexInternalAsync()
    {
        TrackInfo? trackToPlay;
        lock (_queue)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count)
            {
                Log.Warn($"[AudioEngine] Invalid index: {_currentIndex}, queue size: {_queue.Count}");
                return;
            }
            trackToPlay = _queue[_currentIndex];
        }

        if (trackToPlay == null)
        {
            Log.Warn("[AudioEngine] Track at current index is null");
            return;
        }

        Log.Info($"[AudioEngine] Playing index {_currentIndex}: {trackToPlay.Title}");
        SyncTrackPreferences(trackToPlay);

        // Отменяем все предыдущие операции
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        var session = Interlocked.Increment(ref _session);

        if (oldCts != null)
        {
            Try(oldCts.Cancel);
            // Не Dispose сразу - пусть задачи завершатся
        }

        // Ждём освобождения ресурсов
        await CleanupCurrentMediaAsync();

        ResetStreamInfo();

        IsLoading = true;
        CurrentTrack = trackToPlay;
        _isPlayerReady = false;

        RaiseEvent(() => OnTrackChanged?.Invoke(trackToPlay));
        RaiseEvent(() => OnQueueChanged?.Invoke());

        var cts = _cts;

        // Запускаем загрузку в фоне
        _ = Task.Run(async () =>
        {
            // Ждём с таймаутом
            if (!await _loadLock.WaitAsync(2000))
            {
                Log.Warn("[AudioEngine] Could not acquire load lock - forcing release");
                return;
            }

            try
            {
                if (_session != session || cts.IsCancellationRequested)
                {
                    Log.Info("[AudioEngine] Session changed, aborting load");
                    return;
                }

                await PlayTrackInternalAsync(trackToPlay, session, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Info("[AudioEngine] Playback cancelled");
            }
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

    /// <summary>
    /// Асинхронно очищает текущий медиа и стрим с гарантированным освобождением ресурсов
    /// </summary>
    private async Task CleanupCurrentMediaAsync()
    {
        var oldMedia = _currentMedia;
        var oldStream = _currentStream;

        _currentMedia = null;
        _currentStream = null;

        if (oldStream == null && oldMedia == null) return;

        // Останавливаем плеер
        if (_player != null && _player.State != VLCState.Stopped)
        {
            Try(_player.Stop);
        }

        // Очищаем асинхронно но БЕЗ ожидания - ресурсы освободятся сами
        _ = Task.Run(() =>
        {
            // Даём VLC время отпустить ресурсы
            Thread.Sleep(50);

            Try(() => oldStream?.Dispose());
            Try(() => oldMedia?.Dispose());
        });

        // Небольшая пауза для UI
        await Task.Delay(30);
    }

    public async Task SwitchQualityAsync(string container, int targetBitrate = 0)
    {
        if (CurrentTrack == null) return;

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

        await PlayCurrentIndexAsync();

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

        MemoryFirstCachingStream? cacheStream = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            var stream = await GetOrRefreshStreamAsync(track, ct);
            if (stream == null) throw new Exception("Failed to get stream URL");

            if (_session != session)
            {
                Log.Info($"[AudioEngine] Session changed ({session} -> {_session}), aborting");
                return;
            }
            ct.ThrowIfCancellationRequested();

            SetStreamInfo(stream.Codec, stream.Bitrate, stream.Container);

            long size = stream.Size > 0 ? stream.Size : await TryGetContentLengthAsync(stream.Url, ct);

            if (_session != session) return;
            ct.ThrowIfCancellationRequested();

            if (size <= 0)
            {
                StartPlayback(new Media(_libVLC, stream.Url, FromType.FromLocation), null, track);
                return;
            }

            cacheStream = new MemoryFirstCachingStream(track.Id, stream.Url, size, _httpClient, _cacheManager);

            var preBufferResult = await cacheStream.PreBufferAsync(ct);

            if (!preBufferResult)
            {
                cacheStream.Dispose();
                cacheStream = null;

                // Проверяем - это отмена или реальная ошибка?
                if (ct.IsCancellationRequested || _session != session)
                {
                    // Отмена - не показываем ошибку
                    IsLoading = false;
                    return;
                }

                throw new Exception("PreBuffer failed");
            }

            if (_session != session || ct.IsCancellationRequested)
            {
                cacheStream.Dispose();
                return;
            }

            StartPlayback(new Media(_libVLC, new StreamMediaInput(cacheStream)), cacheStream, track);
            cacheStream = null; // Передали владение
            Log.Info($"[AudioEngine] Loaded in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            // Нормальная отмена - просто выходим без ошибки
            cacheStream?.Dispose();
            IsLoading = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cacheStream?.Dispose();
            Log.Error($"[AudioEngine] Error: {ex.Message}");
            RaiseEvent(() => OnError?.Invoke(ex.Message));
            IsLoading = false;
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

            if (_currentIndex == -1 && _queue.Count > 0)
            {
                _currentIndex = 0;
            }
        }

        RaiseEvent(() => OnQueueChanged?.Invoke());

        await PlayCurrentIndexAsync();
    }

    /// <summary>
    /// Переключает на следующий трек (ручное действие пользователя)
    /// </summary>
    public async Task PlayNextAsync()
    {
        if (_isDisposed || _isNavigating) return;

        if (!await _navigationLock.WaitAsync(50))
        {
            Log.Info("[AudioEngine] PlayNextAsync skipped - navigation in progress");
            return;
        }

        _isNavigating = true;

        try
        {
            await PlayNextInternalAsync(userInitiated: true);
        }
        finally
        {
            _isNavigating = false;
            _navigationLock.Release();
        }
    }

    /// <summary>
    /// Внутренний метод перехода на следующий трек
    /// </summary>
    private async Task PlayNextInternalAsync(bool userInitiated)
    {
        // RepeatOne работает ТОЛЬКО при автоматическом окончании трека
        if (!userInitiated && RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            Log.Info("[AudioEngine] RepeatOne: Restarting current track");
            await PlayCurrentIndexInternalAsync();
            return;
        }

        bool hasNext = false;
        lock (_queue)
        {
            if (_queue.Count == 0) return;

            if (_currentIndex + 1 < _queue.Count)
            {
                _currentIndex++;
                hasNext = true;
                Log.Info($"[AudioEngine] Next track: index {_currentIndex}");
            }
            else if (RepeatMode == RepeatMode.RepeatAll)
            {
                _currentIndex = 0;
                hasNext = true;
                Log.Info("[AudioEngine] RepeatAll: Back to first track");
            }
            else
            {
                Log.Info("[AudioEngine] Queue ended");
            }
        }

        if (hasNext)
        {
            await PlayCurrentIndexInternalAsync();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>
    /// Переключает на предыдущий трек
    /// </summary>
    public async Task PlayPreviousAsync()
    {
        if (_isDisposed || _isNavigating) return;

        if (!await _navigationLock.WaitAsync(50))
        {
            Log.Info("[AudioEngine] PlayPreviousAsync skipped - navigation in progress");
            return;
        }

        _isNavigating = true;

        try
        {
            // Если проиграло более 3 сек, возвращаемся в начало трека
            if (CurrentPosition.TotalSeconds > 3 && _isPlayerReady)
            {
                if (_player != null)
                {
                    _player.Time = 0;
                }
                return;
            }

            bool hasPrev = false;
            lock (_queue)
            {
                if (_queue.Count == 0) return;

                if (_currentIndex - 1 >= 0)
                {
                    _currentIndex--;
                    hasPrev = true;
                    Log.Info($"[AudioEngine] Previous track: index {_currentIndex}");
                }
                else if (RepeatMode == RepeatMode.RepeatAll)
                {
                    _currentIndex = _queue.Count - 1;
                    hasPrev = true;
                    Log.Info("[AudioEngine] RepeatAll: Jump to last track");
                }
            }

            if (hasPrev)
            {
                await PlayCurrentIndexInternalAsync();
            }
            else if (_isPlayerReady && _player != null)
            {
                _player.Time = 0;
            }
        }
        finally
        {
            _isNavigating = false;
            _navigationLock.Release();
        }
    }

    public void Enqueue(TrackInfo track)
    {
        lock (_queue)
        {
            if (_queue.Any(t => t.Id == track.Id))
                return;

            _queue.Add(track);
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());

        if (CurrentTrack == null && !IsPlaying && !IsLoading)
        {
            lock (_queue)
            {
                _currentIndex = _queue.Count - 1;
            }
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

        if (CurrentTrack == null && !IsPlaying && !IsLoading)
        {
            _ = PlayNextAsync();
        }
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

            var current = (_currentIndex >= 0 && _currentIndex < _queue.Count)
                ? _queue[_currentIndex]
                : null;

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
                if (index == _queue.Count - 1)
                {
                    _currentIndex--;
                }
                needStop = _queue.Count == 1;
            }
            else if (index < _currentIndex)
            {
                _currentIndex--;
            }

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

            if (_currentIndex == oldIndex)
            {
                _currentIndex = newIndex;
            }
            else if (oldIndex < _currentIndex && newIndex >= _currentIndex)
            {
                _currentIndex--;
            }
            else if (oldIndex > _currentIndex && newIndex <= _currentIndex)
            {
                _currentIndex++;
            }
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
        IsPlaying = true;
        IsPaused = false;

        ApplyVolume();
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

        Log.Info("[AudioEngine] Track ended, preparing next...");

        IsPlaying = false;
        IsPaused = false;
        _isPlayerReady = false;
        NotifyPlaybackState();

        var session = Interlocked.Increment(ref _session);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50);
                if (_isDisposed || _session != session) return;

                if (_player?.State != VLCState.Stopped)
                {
                    _player?.Stop();
                    await Task.Delay(50);
                }

                if (_isDisposed || _session != session) return;

                await CleanupCurrentMediaAsync();

                if (_isDisposed || _session != session) return;

                await Task.Delay(100);

                // Захватываем навигационный лок для автоперехода
                if (!await _navigationLock.WaitAsync(100))
                {
                    Log.Info("[AudioEngine] EndReached: navigation busy, skipping auto-next");
                    return;
                }

                _isNavigating = true;

                try
                {
                    if (!_isDisposed && _session == session)
                    {
                        await PlayNextInternalAsync(userInitiated: false);
                    }
                }
                finally
                {
                    _isNavigating = false;
                    _navigationLock.Release();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioEngine] Error in OnVlcEndReached: {ex.Message}");
            }
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

    private static void RaiseEvent(Action action)
    {
        try
        {
            // Гарантируем выполнение на UI потоке
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(action);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Event error: {ex.Message}");
        }
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

        Try(_libVLC.Dispose);
        Try(_loadLock.Dispose);
        Try(_commandLock.Dispose);
        Try(_navigationLock.Dispose);
        Try(_apiLock.Dispose);
        Try(_httpClient.Dispose);
        Try(_cacheManager.Dispose);

        Log.Info("[AudioEngine] Disposed");
    }
}
