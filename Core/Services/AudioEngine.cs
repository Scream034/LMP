using System.Diagnostics;
using LibVLCSharp.Shared;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Utils;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Services;

public sealed class AudioEngine : ViewModelBase, IDisposable
{
    private const int MaxConsecutiveErrors = 3; // Порог ошибок подряд
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
    private int _consecutiveErrors = 0; // Счетчик ошибок
    private long _cachedTime = 0;
    private long _cachedLength = 0;

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

    // Events

    public event Action<string, string>? OnCriticalError;

    // Вместо копирования каждый раз, используем snapshot только когда нужно
    private List<TrackInfo> _queueSnapshot = [];
    private int _queueVersion = 0;
    private int _snapshotVersion = -1;

    public IReadOnlyList<TrackInfo> Queue
    {
        get
        {
            lock (_queue)
            {
                // Возвращаем кэшированный snapshot, если очередь не изменилась
                if (_snapshotVersion == _queueVersion)
                    return _queueSnapshot;

                _queueSnapshot = [.. _queue];
                _snapshotVersion = _queueVersion;
                return _queueSnapshot;
            }
        }
    }

    public int CurrentQueueIndex => _currentIndex;

    public string VlcStateString => _player?.State.ToString() ?? "NULL";
    public double BufferProgress => _currentStream?.DownloadProgress ?? 0;
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    // Это предотвращает блокировку UI, если плеер "задумался"
    public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(Volatile.Read(ref _cachedTime));

    public TimeSpan TotalDuration
    {
        get
        {
            long len = Volatile.Read(ref _cachedLength);
            return len > 0 ? TimeSpan.FromMilliseconds(len) : (CurrentTrack?.Duration ?? TimeSpan.Zero);
        }
    }

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
            PooledConnectionLifetime = TimeSpan.FromMinutes(16),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8, // Увеличить для параллельной загрузки обложек/поиска
            ConnectTimeout = TimeSpan.FromSeconds(4), // Быстрый тайм-аут коннекта (Fail fast)
            EnableMultipleHttp2Connections = true // YouTube поддерживает HTTP/2
        })
        { Timeout = TimeSpan.FromMinutes(4) };

        // Устанавливаем User-Agent от VR-клиента для скачивания потоков
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UserAgent);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", YoutubeHttpHandler.GetHl());
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.youtube.com/");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.youtube.com/");

        ShuffleEnabled = library.Settings.ShuffleEnabled;
        RepeatMode = library.Settings.RepeatMode;
        _volumePercent = NormalizeVolume(library.Settings.Volume);

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
            _player.TimeChanged += (_, e) =>
            {
                // Обновляем кэш при событии от плеера
                Volatile.Write(ref _cachedTime, e.Time);
                OnVlcTimeChanged(e.Time);
            };
            _player.LengthChanged += (_, e) =>
            {
                Volatile.Write(ref _cachedLength, e.Length);
                RaiseEvent(() => this.RaisePropertyChanged(nameof(TotalDuration)));
            };
            ApplyVolume();
        }
    }

    public async Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        Log.Info($"[AudioEngine] Switching profile to {profile}...");

        // 1. Stop gracefully AND WAIT
        // Повторяем логику Stop(), но с await
        _suppressAutoNext = true;
        Interlocked.Increment(ref _session);
        Try(() => _cts?.Cancel());

        // ВАЖНО: Ждем завершения работы с медиа
        await StopPlaybackAsync();

        ResetStreamInfo();
        CurrentTrack = null;
        IsLoading = false;
        IsPlaying = false;
        IsPaused = false;

        // События
        RaiseEvent(() => OnTrackChanged?.Invoke(null));
        RaiseEvent(() => OnPlaybackStopped?.Invoke());
        // NotifyPlaybackState вызывает RaiseEvent, можно вызвать напрямую
        RaiseEvent(() => OnPlaybackStateChanged?.Invoke(false, false));

        // 2. Explicitly dispose player FIRST
        // Уничтожаем плеер до того, как уничтожим _libVLC
        if (_player != null)
        {
            _player.Dispose();
            _player = null;
        }

        // 3. Update config
        _currentStreamingConfig = GetConfigForProfile(profile);

        // 4. Re-init engine (теперь безопасно делать Dispose старого _libVLC)
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

        // КРИТИЧНО: Очищаем ВСЕ кэшированные данные о стриме
        track.StreamUrl = string.Empty;
        track.CachedCodec = string.Empty;
        track.CachedBitrate = 0;
        track.CachedContainer = string.Empty;

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredContainer = container;
            track.PreferredBitrate = targetBitrate;
            _ = _library.AddOrUpdateTrackAsync(track);
        }

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

            var stream = await GetOrRefreshStreamAsync(track, forceRefresh: false, ct);
            if (stream == null)
            {
                if (ct.IsCancellationRequested) return;
                throw new Exception("Failed to get stream URL");
            }

            if (_session != session) return;
            ct.ThrowIfCancellationRequested();

            // ═══════════════════════════════════════════════════════════════
            // Определяем cacheId СРАЗУ после получения stream
            // ═══════════════════════════════════════════════════════════════
            bool hasOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);

            string cacheId = hasOverride
                ? $"{track.Id}_{stream.Container}_{stream.Bitrate}"
                : track.Id;

            // Обновляем метаданные для ПРАВИЛЬНОГО cacheId
            StreamCacheManager.UpdateStreamInfo(cacheId, stream.Codec, stream.Bitrate, stream.Container);

            Log.Info($"[AudioEngine] Stream: {stream.Codec}/{stream.Bitrate}kbps, cache={cacheId}");
            SetStreamInfo(stream.Codec, stream.Bitrate);

            long size = stream.Size;
            if (size <= 0 && !hasOverride)
                size = await TryGetContentLengthAsync(stream.Url, ct);

            if (_session != session || ct.IsCancellationRequested) return;

            if (size > 0)
            {
                cacheStream = new MemoryFirstCachingStream(
                    cacheId,
                    stream.Url,
                    size,
                    _httpClient,
                    _cacheManager,
                    _currentStreamingConfig,
                    urlRefresher: async token =>
                    {
                        var s = await GetOrRefreshStreamAsync(track, forceRefresh: true, token);
                        return s?.Url;
                    },
                    originalTrackId: track.Id
                );

                if (!await cacheStream.PreBufferAsync(ct))
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
                var media = new Media(_libVLC!, stream.Url, FromType.FromLocation);
                media.AddOption($":http-user-agent={YoutubeClientUtils.UserAgent}");
                media.AddOption(":http-referrer=https://www.youtube.com/");
                StartPlayback(media, null, track);
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

                if (++_consecutiveErrors >= MaxConsecutiveErrors)
                {
                    Stop();
                    RaiseEvent(() => OnCriticalError?.Invoke(
                        SL["Player_Error_403_Title"] ?? "Error",
                        SL["Player_Error_403_Msg"] ?? "Too many errors"));
                    _consecutiveErrors = 0;
                    return;
                }

                await Task.Delay(1000, CancellationToken.None);
                _ = PlayNextAsync();
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

        // Сразу обновляем кэш, чтобы UI отреагировал мгновенно
        long ms = (long)Math.Clamp(position.TotalMilliseconds, 0, TotalDuration.TotalMilliseconds);
        Volatile.Write(ref _cachedTime, ms);

        await WithLock(_commandLock, () => Task.Run(() =>
        {
            // Сама перемотка происходит в фоне
            _player.Time = ms;
        }));
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

    /// <summary>
    /// Заменяет очередь новым списком треков и начинает воспроизведение с указанного.
    /// </summary>
    public async Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        if (_isDisposed) return;

        lock (_queue)
        {
            _queue.Clear();

            // Добавляем без лишних копий
            foreach (var track in tracks)
            {
                _queue.Add(track);
            }

            _currentIndex = _queue.FindIndex(t => t.Id == startTrack.Id);
            if (_currentIndex == -1 && _queue.Count > 0)
                _currentIndex = 0;

            InvalidateQueueSnapshot();
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

    /// <summary>
    /// Инвалидация при изменении очереди
    /// </summary>
    private void InvalidateQueueSnapshot()
    {
        _queueVersion++;
    }

    public void Enqueue(TrackInfo track)
    {
        lock (_queue)
        {
            if (_queue.Any(t => t.Id == track.Id)) return;
            _queue.Add(track);
            InvalidateQueueSnapshot();
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
        lock (_queue)
        {
            var existingIds = _queue.Select(t => t.Id).ToHashSet();
            int addedCount = 0;

            foreach (var track in tracks)  // ← Без .ToList()!
            {
                if (!existingIds.Contains(track.Id))
                {
                    _queue.Add(track);
                    existingIds.Add(track.Id);  // Для дедупликации внутри batch
                    addedCount++;
                }
            }

            if (addedCount == 0) return;
            InvalidateQueueSnapshot();
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
            InvalidateQueueSnapshot();
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

            InvalidateQueueSnapshot();
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
            InvalidateQueueSnapshot();
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

            InvalidateQueueSnapshot();
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

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        // Активный стрим имеет приоритет
        if (_streamInfoReady && !string.IsNullOrEmpty(_activeCodec))
        {
            return (_activeCodec, _activeBitrate, true);
        }

        // Fallback для скачанных
        if (CurrentTrack?.IsDownloaded == true && !string.IsNullOrEmpty(CurrentTrack.LocalPath))
        {
            string format = Path.GetExtension(CurrentTrack.LocalPath)?
                .TrimStart('.').ToUpperInvariant() ?? "FILE";
            int bitrate = CurrentTrack.PreferredBitrate;
            if (bitrate <= 0)
            {
                var meta = StreamCacheManager.TryGetMetadata(CurrentTrack.Id);
                if (meta != null) bitrate = meta.Bitrate;
            }
            return (format, bitrate, true);
        }

        return ("", 0, false);
    }

    public long GetDownloadedBytes() =>
        _currentStream != null ? (long)(_currentStream.DownloadProgress / 100 * _currentStream.Length) : 0;

    private void ResetStreamInfo()
    {
        _activeCodec = "";
        _activeBitrate = 0;
        _streamInfoReady = false;
        Volatile.Write(ref _cachedTime, 0);
        Volatile.Write(ref _cachedLength, 0);
    }

    private void SetStreamInfo(string codec, int bitrate)
    {
        _activeCodec = codec?.ToUpperInvariant() ?? "";
        _activeBitrate = bitrate;
        _streamInfoReady = true;
        RaiseEvent(() => OnStreamInfoReady?.Invoke());
    }

    private record StreamDetails(string Url, long Size, int Bitrate, string Codec, string Container);

    private async Task<StreamDetails?> GetOrRefreshStreamAsync(TrackInfo track, bool forceRefresh, CancellationToken ct)
    {
        bool hasOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);

        // Проверяем полный кэш (только если нет override)
        if (!hasOverride && !forceRefresh && _cacheManager.IsFullyCached(track.Id))
        {
            var meta = StreamCacheManager.TryGetMetadata(track.Id);
            if (meta != null && meta.ContentLength > 0 && !string.IsNullOrEmpty(meta.Codec))
            {
                Log.Info($"[AudioEngine] Using cached stream for {track.Id}");

                // Запускаем промоут если ещё не скачано
                if (!track.IsDownloaded && !_cacheManager.IsPromoted(track.Id))
                    _cacheManager.TriggerPromoteWithNotification(track.Id, track.Id);

                return new StreamDetails(meta.SourceUrl, meta.ContentLength, meta.Bitrate, meta.Codec, meta.Container);
            }
        }

        // Нужен ли свежий запрос?
        bool needFresh = forceRefresh || hasOverride ||
                         string.IsNullOrEmpty(track.StreamUrl) ||
                         string.IsNullOrEmpty(track.CachedCodec);

        if (!needFresh)
            return new(track.StreamUrl, -1, track.CachedBitrate, track.CachedCodec, track.CachedContainer);

        // Запрос к YouTube
        return await WithLock(_apiLock, async () =>
        {
            await ThrottleApiCall(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(RefreshTimeoutS));

            var result = await _youtube.RefreshStreamUrlAsync(track, forceRefresh, cts.Token);
            _lastApiCall = DateTime.UtcNow;

            if (!result.HasValue) return null;

            // НЕ вызываем UpdateStreamInfo здесь — это делается в PlayTrackInternalAsync

            return new StreamDetails(
                result.Value.Url, result.Value.Size,
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

        _consecutiveErrors = 0; // <--- СБРОС СЧЕТЧИКА ПРИ УСПЕХЕ

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