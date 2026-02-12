// Core/Services/AudioEngine.cs

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LibVLCSharp.Shared;
using LMP.Core.Models;
using LMP.Core.Services.Streaming;
using LMP.Core.ViewModels;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Utils;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Services;

public sealed class AudioEngine : ViewModelBase, IDisposable
{
    #region Constants

    private const int MaxConsecutiveErrors = 3;
    private const int ApiCooldownMs = 200;
    private const int QualitySwitchTimeoutMs = 8000;
    private const int MaxHistorySize = 100;
    private const int RefreshTimeoutMs = 60_000;
    private const int CommandTimeoutMs = 5000;
    private const int LockTimeoutMs = 3000;

    #endregion

    #region Dependencies

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly StreamCacheManager _cacheManager;
    private readonly HttpClient _httpClient;

    #endregion

    #region VLC Core

    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private Media? _currentMedia;

    /// <summary>
    /// Унифицированный стрим (HLS или MemoryFirst)
    /// </summary>
    private MediaStreamBase? _currentStream;

    #endregion

    #region Synchronization

    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private readonly SemaphoreSlim _apiLock = new(1, 1);
    private readonly Channel<Func<ValueTask>> _commandQueue;

    private CancellationTokenSource? _playbackCts;
    private TaskCompletionSource<bool>? _playbackStartedTcs;
    private int _session;

    #endregion

    #region State

    [Flags]
    private enum StateFlags
    {
        None = 0,
        Playing = 1 << 0,
        Paused = 1 << 1,
        Loading = 1 << 2,
        Ready = 1 << 3,
        Navigating = 1 << 4,
        Disposed = 1 << 5,
        SuppressAutoNext = 1 << 6
    }

    private int _stateFlags;
    private int _consecutiveErrors;
    private long _cachedTimeMs;
    private long _cachedLengthMs;
    private int _volumePercent;
    private DateTime _lastApiCall;
    private DateTime _lastVolumeChange;

    private string _activeCodec = "";
    private int _activeBitrate;
    private StreamingConfig _streamingConfig;

    #endregion

    #region Queue

    private readonly List<TrackInfo> _queue = new(64);
    private readonly List<TrackInfo> _history = new(MaxHistorySize);
    private readonly Lock _queueLock = new();
    private int _currentIndex = -1;
    private IReadOnlyList<TrackInfo>? _queueSnapshot;
    private int _queueVersion;

    #endregion

    #region Properties

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }

    public bool IsPlaying => HasFlag(StateFlags.Playing);
    public bool IsPaused => HasFlag(StateFlags.Paused);
    public bool IsLoading => HasFlag(StateFlags.Loading);

    public IReadOnlyList<TrackInfo> Queue
    {
        get
        {
            lock (_queueLock)
            {
                _queueSnapshot ??= [.. _queue];
                return _queueSnapshot;
            }
        }
    }

    public int CurrentQueueIndex => Volatile.Read(ref _currentIndex);
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(Volatile.Read(ref _cachedTimeMs));

    public TimeSpan TotalDuration
    {
        get
        {
            var len = Volatile.Read(ref _cachedLengthMs);
            return len > 0 ? TimeSpan.FromMilliseconds(len) : (CurrentTrack?.Duration ?? TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Прогресс буферизации (0-100).
    /// </summary>
    public double BufferProgress => _currentStream?.DownloadProgress ?? 0;

    /// <summary>
    /// Количество загруженных байт.
    /// </summary>
    public long GetDownloadedBytes() => _currentStream?.BufferedBytes ?? 0;

    /// <summary>
    /// Закэшированные диапазоны для визуализации.
    /// </summary>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() =>
        _currentStream?.GetBufferedRanges() ?? [];

    /// <summary>
    /// Полностью загружен.
    /// </summary>
    public bool IsFullyBuffered => _currentStream?.IsFullyDownloaded ?? false;

    public string VlcStateString => _player?.State.ToString() ?? "None";

    #endregion

    #region Events

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action? OnStreamInfoReady;
    public event Action<bool, bool>? OnPlaybackStateChanged;
    public event Action? OnQueueChanged;
    public event Action<string, string>? OnCriticalError;
    public event Action<bool>? OnLoadingStateChanged;

    #endregion

    #region Constructor

    public AudioEngine(YoutubeProvider youtube, LibraryService library, StreamCacheManager cacheManager)
    {
        _youtube = youtube;
        _library = library;
        _cacheManager = cacheManager;
        _httpClient = CreateHttpClient();

        _commandQueue = Channel.CreateBounded<Func<ValueTask>>(
            new BoundedChannelOptions(16)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        ShuffleEnabled = library.Settings.ShuffleEnabled;
        RepeatMode = library.Settings.RepeatMode;
        _volumePercent = NormalizeVolume(library.Settings.Volume);
        _streamingConfig = GetStreamingConfig(library.Settings.InternetProfile);

        InitializeVLC();

        _ = ProcessCommandsAsync();
        _ = VolumeSaveLoopAsync();

        Log.Info($"[AudioEngine] Ready. Volume={_volumePercent}%");
    }

    private static HttpClient CreateHttpClient() => new(new LoggingHandler(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 8,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        EnableMultipleHttp2Connections = true
    }))
    {
        Timeout = TimeSpan.FromMinutes(4),
        DefaultRequestHeaders =
        {
            { "User-Agent", YoutubeClientUtils.UserAgent },
            { "Accept-Language", YoutubeHttpHandler.GetHl() },
            { "Accept", "*/*" },
            { "Origin", "https://www.youtube.com/" },
            { "Referer", "https://www.youtube.com/" }
        }
    };

    #endregion

    #region VLC Initialization

    private void InitializeVLC()
    {
        LibVLCSharp.Shared.Core.Initialize();
        var cache = _streamingConfig.VlcNetworkCachingMs;

        _libVLC = new LibVLC(
            "--no-video", "--vout=none", "--no-spu", "--no-osd", "--no-stats",
            $"--network-caching={cache}",
            $"--file-caching={cache}",
            $"--live-caching={cache}",
            "--http-reconnect", "--http-continuous",
            "--audio-resampler=speex", "--aout=wasapi",
            "--clock-jitter=0", "--clock-synchro=0"
        );

        _player = new MediaPlayer(_libVLC);
        AttachPlayerEvents(_player);
        ApplyVolume();
    }

    private void AttachPlayerEvents(MediaPlayer player)
    {
        player.Playing += (_, _) => OnVlcPlaying();
        player.Paused += (_, _) => OnVlcPaused();
        player.Stopped += (_, _) => OnVlcStopped();
        player.EndReached += (_, _) => OnVlcEndReached();
        player.EncounteredError += (_, _) => OnVlcError();
        player.TimeChanged += (_, e) =>
        {
            Volatile.Write(ref _cachedTimeMs, e.Time);
            RaiseOnUI(() => OnPositionChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time)));
        };
        player.LengthChanged += (_, e) =>
        {
            Volatile.Write(ref _cachedLengthMs, e.Length);
            RaiseOnUI(() => this.RaisePropertyChanged(nameof(TotalDuration)));
        };
    }

    public async Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        Log.Info($"[AudioEngine] Switching to profile: {profile}");
        CancelCurrentPlayback();

        await EnqueueCommandAsync(async () =>
        {
            await CleanupMediaAsync();
            _player?.Dispose();
            _player = null;
            _libVLC?.Dispose();

            _streamingConfig = GetStreamingConfig(profile);
            InitializeVLC();
            ClearState();

            RaiseOnUI(() =>
            {
                OnTrackChanged?.Invoke(null);
                OnPlaybackStopped?.Invoke();
            });
        });
    }

    #endregion

    #region Playback Control

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null || HasFlag(StateFlags.Disposed)) return Task.CompletedTask;

        var newSession = CancelCurrentPlayback();

        return EnqueueCommandAsync(async () =>
        {
            if (_session != newSession) return;

            lock (_queueLock)
            {
                var idx = _queue.FindIndex(t => t.Id == track.Id);
                if (idx >= 0)
                {
                    _currentIndex = idx;
                    _queue[idx] = track;
                }
                else
                {
                    _queue.Clear();
                    _queue.Add(track);
                    _currentIndex = 0;
                    InvalidateQueueSnapshot();
                }
            }

            RaiseOnUI(() => OnQueueChanged?.Invoke());
            await PlayCurrentIndexAsync(newSession);
        });
    }

    public Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        if (HasFlag(StateFlags.Disposed)) return Task.CompletedTask;

        var newSession = CancelCurrentPlayback();

        return EnqueueCommandAsync(async () =>
        {
            if (_session != newSession) return;

            lock (_queueLock)
            {
                _queue.Clear();
                _queue.AddRange(tracks);
                _currentIndex = _queue.FindIndex(t => t.Id == startTrack.Id);
                if (_currentIndex == -1 && _queue.Count > 0) _currentIndex = 0;
                InvalidateQueueSnapshot();
            }

            RaiseOnUI(() => OnQueueChanged?.Invoke());
            await PlayCurrentIndexAsync(newSession);
        });
    }

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (HasFlag(StateFlags.Disposed) || _player == null) return;

        using var cts = new CancellationTokenSource(CommandTimeoutMs);

        await Task.Run(() =>
        {
            var state = _player?.State ?? VLCState.Error;

            if (shouldPlay)
            {
                _currentStream?.NotifyPaused(false);

                if (state is VLCState.Paused or VLCState.Stopped)
                    _player?.Play();
                else if (state is VLCState.Ended)
                {
                    var session = CancelCurrentPlayback();
                    _ = EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
                }
                else
                    _player?.Play();

                SetFlag(StateFlags.Playing, true);
                SetFlag(StateFlags.Paused, false);
            }
            else
            {
                if (state is VLCState.Playing or VLCState.Buffering or VLCState.Opening)
                    _player?.Pause();

                _currentStream?.NotifyPaused(true);

                SetFlag(StateFlags.Playing, false);
                SetFlag(StateFlags.Paused, true);
            }
        }, cts.Token);

        NotifyPlaybackState();
    }

    public ValueTask SeekAsync(TimeSpan position)
    {
        if (_player == null || !HasFlag(StateFlags.Ready) || HasFlag(StateFlags.Disposed))
            return ValueTask.CompletedTask;

        var ms = (long)Math.Clamp(position.TotalMilliseconds, 0, TotalDuration.TotalMilliseconds);

        Log.Debug($"[AudioEngine] Seek to {position.TotalSeconds:F1}s");

        SetFlag(StateFlags.SuppressAutoNext, true);

        try
        {
            _currentStream?.NotifySeek(ms);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] NotifySeek error: {ex.Message}");
        }

        Volatile.Write(ref _cachedTimeMs, ms);

        _ = Task.Run(async () =>
        {
            try
            {
                if (_player == null || HasFlag(StateFlags.Disposed)) return;

                // Запоминаем что нужно возобновить воспроизведение
                bool shouldPlay = IsPlaying || _player.State == VLCState.Paused;

                Log.Debug($"[AudioEngine] Setting VLC time to {ms}ms, state={_player.State}");

                // Устанавливаем время
                _player.Time = ms;

                await Task.Delay(50);

                if (HasFlag(StateFlags.Disposed)) return;

                if (shouldPlay)
                {
                    // Пробуем несколько способов возобновить воспроизведение
                    for (int retry = 0; retry < 10; retry++)
                    {
                        if (HasFlag(StateFlags.Disposed)) return;

                        var state = _player.State;
                        if (state == VLCState.Playing)
                        {
                            Log.Debug($"[AudioEngine] VLC playing after {retry} retries");
                            break;
                        }

                        Log.Debug($"[AudioEngine] Play attempt {retry + 1}, state={state}");

                        // Разные методы в зависимости от состояния
                        if (state == VLCState.Paused)
                        {
                            _player.SetPause(false);
                        }
                        else if (state == VLCState.Stopped || state == VLCState.Ended)
                        {
                            _player.Play();
                        }
                        else
                        {
                            _player.Play();
                        }

                        await Task.Delay(50 + retry * 20);
                    }

                    // Последняя попытка
                    if (_player.State != VLCState.Playing && !HasFlag(StateFlags.Disposed))
                    {
                        Log.Warn($"[AudioEngine] Force play, state={_player.State}");
                        _player.Play();
                    }
                }

                await Task.Delay(50);
                SetFlag(StateFlags.SuppressAutoNext, false);

                Log.Debug($"[AudioEngine] Seek complete, VLC state={_player.State}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioEngine] VLC seek error: {ex.Message}");
                SetFlag(StateFlags.SuppressAutoNext, false);
            }
        });

        return ValueTask.CompletedTask;
    }

    public void Stop()
    {
        CancelCurrentPlayback();

        _ = EnqueueCommandAsync(async () =>
        {
            await CleanupMediaAsync();
            ClearState();

            RaiseOnUI(() =>
            {
                OnTrackChanged?.Invoke(null);
                OnPlaybackStopped?.Invoke();
            });
            NotifyPlaybackState();
        });
    }

    public Task PlayNextAsync() => NavigateAsync(forward: true, userInitiated: true);
    public Task PlayPreviousAsync() => NavigateAsync(forward: false, userInitiated: true);

    private int CancelCurrentPlayback()
    {
        SetFlag(StateFlags.SuppressAutoNext, true);
        var newSession = Interlocked.Increment(ref _session);

        Log.Debug($"[AudioEngine] Cancel session → {newSession}");

        // Отменяем CTS
        try
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
        }
        catch { }
        _playbackCts = null;

        // Немедленно отменяем операции в стриме
        try { _currentStream?.CancelPendingReads(); }
        catch { }

        return newSession;
    }


    #endregion

    #region Volume

    public void SaveVolumeNow() => _library.UpdateSettings(s => s.Volume = _volumePercent);
    public float GetVolume() => _volumePercent;

    public void SetVolumeInstant(float value)
    {
        _volumePercent = Math.Clamp((int)Math.Round(value), 0, 500);
        _lastVolumeChange = DateTime.UtcNow;
        ApplyVolume();
    }

    public void ToggleMute()
    {
        if (_volumePercent > 0)
        {
            _library.UpdateSettings(s => s.LastVolume = _volumePercent);
            SetVolumeInstant(0);
        }
        else
        {
            var restore = _library.Settings.LastVolume > 0 ? _library.Settings.LastVolume : 50;
            SetVolumeInstant(restore);
        }
    }

    public void UpdateAudioSettings()
    {
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        if (_player == null || HasFlag(StateFlags.Disposed)) return;

        try
        {
            var gain = MathF.Pow(10f, Math.Clamp(_library.Settings.TargetGainDb, -20f, 20f) / 20f);
            var final = Math.Clamp((int)Math.Round(_volumePercent * gain), 0, 500);
            if (_player.Volume != final) _player.Volume = final;
        }
        catch { }
    }

    private async Task VolumeSaveLoopAsync()
    {
        while (!HasFlag(StateFlags.Disposed))
        {
            await Task.Delay(2000);
            if ((DateTime.UtcNow - _lastVolumeChange).TotalSeconds >= 1.5)
                _library.UpdateSettings(s => s.Volume = _volumePercent);
        }
    }

    private static int NormalizeVolume(float saved) =>
        Math.Clamp(saved is <= 1f and > 0 ? (int)(saved * 100) : (int)saved, 0, 500);

    #endregion

    #region Queue Management

    public void Enqueue(TrackInfo track)
    {
        lock (_queueLock)
        {
            if (_queue.Any(t => t.Id == track.Id)) return;
            _queue.Add(track);
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() => OnQueueChanged?.Invoke());

        if (CurrentTrack == null && !IsPlaying && !IsLoading)
        {
            lock (_queueLock) _currentIndex = _queue.Count - 1;
            var session = CancelCurrentPlayback();
            _ = EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        int added = 0;
        lock (_queueLock)
        {
            var existing = _queue.Select(t => t.Id).ToHashSet();
            foreach (var track in tracks)
            {
                if (existing.Add(track.Id))
                {
                    _queue.Add(track);
                    added++;
                }
            }
            if (added > 0) InvalidateQueueSnapshot();
        }

        if (added > 0)
        {
            RaiseOnUI(() => OnQueueChanged?.Invoke());
            if (CurrentTrack == null && !IsPlaying && !IsLoading)
                _ = PlayNextAsync();
        }
    }

    public void ClearQueue()
    {
        lock (_queueLock)
        {
            var current = CurrentTrack;
            _queue.Clear();
            _currentIndex = -1;

            if (current != null)
            {
                _queue.Add(current);
                _currentIndex = 0;
            }
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    public void ShuffleQueue()
    {
        lock (_queueLock)
        {
            if (_queue.Count < 2) return;

            var current = _currentIndex >= 0 && _currentIndex < _queue.Count ? _queue[_currentIndex] : null;
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_queue));

            if (current != null)
            {
                _currentIndex = _queue.IndexOf(current);
                if (_currentIndex == -1)
                {
                    _queue.Insert(0, current);
                    _currentIndex = 0;
                }
            }
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    public void RemoveFromQueue(TrackInfo track)
    {
        bool needStop = false;
        lock (_queueLock)
        {
            var idx = _queue.FindIndex(t => t.Id == track.Id);
            if (idx == -1) return;

            if (idx == _currentIndex)
            {
                needStop = _queue.Count == 1;
                if (idx == _queue.Count - 1) _currentIndex--;
            }
            else if (idx < _currentIndex) _currentIndex--;

            _queue.RemoveAt(idx);
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() => OnQueueChanged?.Invoke());
        if (needStop) Stop();
    }

    public void MoveQueueItem(int from, int to)
    {
        lock (_queueLock)
        {
            if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count || from == to) return;

            var item = _queue[from];
            _queue.RemoveAt(from);
            _queue.Insert(to, item);

            if (_currentIndex == from) _currentIndex = to;
            else if (from < _currentIndex && to >= _currentIndex) _currentIndex--;
            else if (from > _currentIndex && to <= _currentIndex) _currentIndex++;

            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InvalidateQueueSnapshot()
    {
        _queueSnapshot = null;
        _queueVersion++;
    }

    #endregion

    #region Quality Switch

    public async Task SwitchQualityAsync(string container, int targetBitrate = 0)
    {
        if (CurrentTrack == null) return;

        var position = CurrentPosition;
        var track = CurrentTrack;

        track.TransientContainer = container;
        track.TransientBitrate = targetBitrate;
        track.StreamUrl = "";
        track.CachedCodec = "";
        track.CachedBitrate = 0;
        track.CachedContainer = "";

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredContainer = container;
            track.PreferredBitrate = targetBitrate;
            _ = _library.AddOrUpdateTrackAsync(track);
        }

        _playbackStartedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = CancelCurrentPlayback();
        await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));

        try
        {
            using var cts = new CancellationTokenSource(QualitySwitchTimeoutMs);
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

    #endregion

    #region Stream Info

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        if (!string.IsNullOrEmpty(_activeCodec))
            return (_activeCodec, _activeBitrate, true);

        if (CurrentTrack?.IsDownloaded == true && !string.IsNullOrEmpty(CurrentTrack.LocalPath))
        {
            var ext = Path.GetExtension(CurrentTrack.LocalPath)?.TrimStart('.').ToUpperInvariant() ?? "FILE";
            var bitrate = CurrentTrack.PreferredBitrate;
            if (bitrate <= 0)
            {
                var meta = StreamCacheManager.TryGetMetadata(CurrentTrack.Id);
                if (meta != null) bitrate = meta.Bitrate;
            }
            return (ext, bitrate, true);
        }

        return ("", 0, false);
    }

    #endregion

    #region Internal Playback

    private async ValueTask PlayCurrentIndexAsync(int? expectedSession = null)
    {
        if (HasFlag(StateFlags.Disposed)) return;

        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
            track = _queue[_currentIndex];
        }

        if (track == null) return;

        int currentSession = expectedSession ?? Interlocked.Increment(ref _session);
        if (expectedSession.HasValue && _session != currentSession) return;

        SetFlag(StateFlags.SuppressAutoNext, true);

        _playbackCts?.Cancel();
        _playbackCts = new CancellationTokenSource();
        var ct = _playbackCts.Token;

        await CleanupMediaAsync();

        if (_session != currentSession || ct.IsCancellationRequested) return;

        ClearStreamInfo();
        SetLoadingState(true);
        SetFlag(StateFlags.Ready, false);
        CurrentTrack = track;

        RaiseOnUI(() =>
        {
            OnTrackChanged?.Invoke(track);
            OnQueueChanged?.Invoke();
        });

        try
        {
            await LoadAndPlayAsync(track, currentSession, ct);
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"[AudioEngine] Cancelled session {currentSession}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Playback error: {ex.Message}");
            RaiseOnUI(() => OnError?.Invoke(ex.Message));
            await HandlePlaybackErrorAsync();
        }
        finally
        {
            if (_session == currentSession)
                SetLoadingState(false);
        }
    }

    private async ValueTask LoadAndPlayAsync(TrackInfo track, int session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        MediaStreamBase? stream = null;

        try
        {
            // Проверки с быстрым выходом
            if (ct.IsCancellationRequested || _session != session) return;

            var streamInfo = await GetStreamAsync(track, forceRefresh: false, ct);
            if (streamInfo == null) throw new Exception("Failed to get stream URL");

            if (ct.IsCancellationRequested || _session != session) return;

            bool isHls = track.IsHlsOnly || streamInfo.Container == "m3u8";
            string cacheId = track.Id;

            if (!isHls)
            {
                var hasOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);
                if (hasOverride)
                    cacheId = $"{track.Id}_{streamInfo.Container}_{streamInfo.Bitrate}";
                StreamCacheManager.UpdateStreamInfo(cacheId, streamInfo.Codec, streamInfo.Bitrate, streamInfo.Container);
            }

            SetStreamInfo(streamInfo.Codec, streamInfo.Bitrate);
            Log.Info($"[AudioEngine] Stream: {streamInfo.Codec}/{streamInfo.Bitrate}kbps, HLS={isHls}");

            if (ct.IsCancellationRequested || _session != session) return;

            // Создаём стрим
            if (isHls)
            {
                stream = CreateHlsStream(streamInfo, track, ct);
            }
            else
            {
                stream = await CreateMemoryStreamAsync(streamInfo, track, cacheId, ct);
            }

            if (stream == null)
                throw new Exception("Failed to create stream");

            // Проверка перед инициализацией
            if (ct.IsCancellationRequested || _session != session)
            {
                stream.Dispose();
                return;
            }

            if (!await stream.InitializeAsync(ct))
            {
                stream.Dispose();
                throw new Exception("Stream initialization failed");
            }

            if (ct.IsCancellationRequested || _session != session)
            {
                stream.Dispose();
                return;
            }

            if (!await stream.PreBufferAsync(ct))
            {
                stream.Dispose();
                throw new Exception("PreBuffer failed");
            }

            // Финальная проверка
            if (ct.IsCancellationRequested || _session != session)
            {
                stream.Dispose();
                return;
            }

            // Запуск воспроизведения
            var media = new Media(_libVLC!, new StreamMediaInput(stream));
            StartPlayback(media, stream, track);

            Log.Info($"[AudioEngine] Loaded in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            stream?.Dispose();
            Log.Debug($"[AudioEngine] Load cancelled, session {session}");
        }
        catch (Exception)
        {
            stream?.Dispose();
            throw;
        }
    }

    private HlsStream CreateHlsStream(StreamInfo info, TrackInfo track, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = YoutubeClientUtils.UserAgent,
            ["Referer"] = "https://www.youtube.com/",
            ["Origin"] = "https://www.youtube.com"
        };

        return new HlsStream(
            info.Url,
            _httpClient,
            headers,
            urlRefresher: async token =>
            {
                var s = await GetStreamAsync(track, forceRefresh: true, token);
                return s?.Url;
            });
    }


    private async Task<MediaStreamBase?> CreateMemoryStreamAsync(
     StreamInfo info, TrackInfo track, string cacheId, CancellationToken ct)
    {
        var size = info.Size;
        if (size <= 0)
            size = await GetContentLengthAsync(info.Url, ct);

        if (size <= 0)
        {
            Log.Warn("[AudioEngine] Cannot determine content length");
            return null;
        }

        // Инициализируем метаданные кэша
        StreamCacheManager.LoadOrCreateMetadata(cacheId, info.Url, size);

        var stream = new MemoryFirstCachingStream(
            cacheId,
            info.Url,
            size,
            _httpClient,
            _cacheManager,
            _streamingConfig,
            urlRefresher: async token =>
            {
                var s = await GetStreamAsync(track, forceRefresh: true, token);
                return s?.Url;
            },
            originalTrackId: track.Id,
            getPlaybackTimeMs: () => Volatile.Read(ref _cachedTimeMs),
            totalDurationMs: (long)track.Duration.TotalMilliseconds);

        return stream;
    }

    private void StartPlayback(Media media, MediaStreamBase stream, TrackInfo track)
    {
        var (oldMedia, oldStream) = (_currentMedia, _currentStream);
        _currentMedia = media;
        _currentStream = stream;

        _ = Task.Run(() =>
        {
            Thread.Sleep(100);
            try { oldStream?.Dispose(); } catch { }
            try { oldMedia?.Dispose(); } catch { }
        });

        if (_player == null) return;

        _player.Media = media;
        ApplyVolume();
        _player.Play();
        AddToHistory(track);
    }

    private async Task NavigateAsync(bool forward, bool userInitiated)
    {
        if (HasFlag(StateFlags.Disposed)) return;

        var newSession = CancelCurrentPlayback();
        SetFlag(StateFlags.Navigating, true);

        try
        {
            if (!forward && CurrentPosition.TotalSeconds > 3 && HasFlag(StateFlags.Ready))
            {
                await EnqueueCommandAsync(() => PlayCurrentIndexAsync(newSession));
                return;
            }

            bool canMove;
            lock (_queueLock)
            {
                canMove = forward ? TryMoveNext(userInitiated) : TryMovePrevious();
            }

            if (canMove)
                await EnqueueCommandAsync(() => PlayCurrentIndexAsync(newSession));
            else if (!forward && HasFlag(StateFlags.Ready) && _player != null)
                _player.Time = 0;
            else
                Stop();
        }
        finally
        {
            SetFlag(StateFlags.Navigating, false);
        }
    }

    private bool TryMoveNext(bool userInitiated)
    {
        if (_queue.Count == 0) return false;
        if (!userInitiated && RepeatMode == RepeatMode.RepeatOne) return true;

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

    private bool TryMovePrevious()
    {
        if (_queue.Count == 0) return false;

        if (_currentIndex > 0)
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

    private async Task HandlePlaybackErrorAsync()
    {
        if (++_consecutiveErrors >= MaxConsecutiveErrors)
        {
            Stop();
            RaiseOnUI(() => OnCriticalError?.Invoke(
                SL["Player_Error_403_Title"] ?? "Error",
                SL["Player_Error_403_Msg"] ?? "Too many errors"));
            _consecutiveErrors = 0;
            return;
        }

        await Task.Delay(1000);
        await PlayNextAsync();
    }

    #endregion

    #region Stream Resolution

    private record StreamInfo(string Url, long Size, int Bitrate, string Codec, string Container);

    private async Task<StreamInfo?> GetStreamAsync(TrackInfo track, bool forceRefresh, CancellationToken ct)
    {
        var hasOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);

        if (!hasOverride && !forceRefresh && _cacheManager.IsFullyCached(track.Id))
        {
            var meta = StreamCacheManager.TryGetMetadata(track.Id);
            if (meta is { ContentLength: > 0, Codec: not "" })
            {
                Log.Debug($"[AudioEngine] Using cached: {track.Id}");
                if (!track.IsDownloaded && !_cacheManager.IsPromoted(track.Id))
                    _cacheManager.TriggerCacheCompleted(track.Id, track.Id);
                return new(meta.SourceUrl, meta.ContentLength, meta.Bitrate, meta.Codec, meta.Container);
            }
        }

        if (!forceRefresh && !hasOverride && !string.IsNullOrEmpty(track.StreamUrl) && !string.IsNullOrEmpty(track.CachedCodec))
            return new(track.StreamUrl, -1, track.CachedBitrate, track.CachedCodec, track.CachedContainer);

        return await WithApiLockAsync(async () =>
        {
            await ThrottleAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RefreshTimeoutMs);

            var result = await _youtube.RefreshStreamUrlAsync(track, forceRefresh, cts.Token);
            _lastApiCall = DateTime.UtcNow;

            return result.HasValue
                ? new StreamInfo(result.Value.Url, result.Value.Size, result.Value.Bitrate, result.Value.Codec, result.Value.Container)
                : null;
        }, ct);
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var elapsed = (DateTime.UtcNow - _lastApiCall).TotalMilliseconds;
        if (elapsed < ApiCooldownMs)
            await Task.Delay(ApiCooldownMs - (int)elapsed, ct);
    }

    private async Task<long> GetContentLengthAsync(string url, CancellationToken ct)
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

    #endregion

    #region VLC Events

    private void OnVlcPlaying()
    {
        if (HasFlag(StateFlags.Disposed)) return;

        _consecutiveErrors = 0;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (!HasFlag(StateFlags.Disposed))
                SetFlag(StateFlags.SuppressAutoNext, false);
        });

        SetFlag(StateFlags.Ready, true);
        SetFlag(StateFlags.Loading, false);
        SetFlag(StateFlags.Playing, true);
        SetFlag(StateFlags.Paused, false);

        _currentStream?.NotifyPlaybackStarted();
        _currentStream?.NotifyPaused(false);

        ApplyVolume();

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            if (IsPlaying && !HasFlag(StateFlags.Disposed)) ApplyVolume();
        });

        NotifyPlaybackState();
        _playbackStartedTcs?.TrySetResult(true);
    }

    private void OnVlcPaused()
    {
        if (HasFlag(StateFlags.Disposed)) return;
        SetFlag(StateFlags.Playing, false);
        SetFlag(StateFlags.Paused, true);
        _currentStream?.NotifyPaused(true);
        NotifyPlaybackState();
    }

    private void OnVlcStopped()
    {
        if (HasFlag(StateFlags.Disposed)) return;
        SetFlag(StateFlags.Ready, false);
        SetFlag(StateFlags.Playing, false);
        SetFlag(StateFlags.Paused, false);
        NotifyPlaybackState();
    }

    private void OnVlcEndReached()
    {
        long lastSeek = Volatile.Read(ref _cachedTimeMs);
        long totalDur = Volatile.Read(ref _cachedLengthMs);

        if (totalDur > 0 && lastSeek < totalDur * 0.95)
        {
            Log.Warn($"[AudioEngine] Ignoring false EndReached");
            return;
        }

        if (_player != null)
        {
            long vlcTime = _player.Time;
            if (totalDur > 0 && vlcTime > 0 && vlcTime < totalDur * 0.95)
            {
                Log.Warn($"[AudioEngine] Ignoring false EndReached (VLC check)");
                return;
            }
        }

        if (HasFlag(StateFlags.Disposed | StateFlags.SuppressAutoNext)) return;

        SetFlag(StateFlags.Playing, false);
        SetFlag(StateFlags.Paused, false);
        SetFlag(StateFlags.Ready, false);
        NotifyPlaybackState();

        var session = Interlocked.Increment(ref _session);
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);

            if (_session != session || HasFlag(StateFlags.Disposed)) return;

            if (_player != null)
            {
                long finalPos = _player.Time;
                long finalDur = _player.Length;

                if (finalDur > 0 && finalPos < finalDur * 0.95)
                {
                    Log.Warn($"[AudioEngine] Cancelled auto-next");
                    return;
                }
            }

            bool canAdvance;
            lock (_queueLock) { canAdvance = TryMoveNext(userInitiated: false); }

            if (canAdvance)
                await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
            else
                Stop();
        });
    }

    private void OnVlcError()
    {
        SetLoadingState(false);
        SetFlag(StateFlags.Playing, false);
        SetFlag(StateFlags.Paused, false);
        RaiseOnUI(() => OnError?.Invoke("VLC playback error"));
        NotifyPlaybackState();
    }

    #endregion

    #region Helpers

    private async Task CleanupMediaAsync()
    {
        var (media, stream) = (_currentMedia, _currentStream);
        _currentMedia = null;
        _currentStream = null;

        if (stream == null && media == null) return;

        // Сначала отменяем операции стрима
        try { stream?.CancelPendingReads(); }
        catch { }

        // Останавливаем VLC если играет
        if (_player?.State is VLCState.Playing or VLCState.Buffering)
        {
            try { _player?.Pause(); }
            catch { }
        }

        // Отсоединяем медиа от плеера
        if (_player?.Media == media)
        {
            try
            {
                _player!.Media = null;
                _player.Stop();
            }
            catch { }
        }

        // Dispose в фоне с небольшой задержкой
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            try { stream?.Dispose(); } catch { }
            try { media?.Dispose(); } catch { }
        });
    }

    private void ClearState()
    {
        ClearStreamInfo();
        CurrentTrack = null;
        SetLoadingState(false);
        SetFlag(StateFlags.Playing, false);
        SetFlag(StateFlags.Paused, false);
        SetFlag(StateFlags.Ready, false);
    }

    private void ClearStreamInfo()
    {
        _activeCodec = "";
        _activeBitrate = 0;
        Volatile.Write(ref _cachedTimeMs, 0);
        Volatile.Write(ref _cachedLengthMs, 0);
    }

    private void SetStreamInfo(string codec, int bitrate)
    {
        _activeCodec = codec?.ToUpperInvariant() ?? "";
        _activeBitrate = bitrate;
        RaiseOnUI(() => OnStreamInfoReady?.Invoke());
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;
        _history.Add(track);
        if (_history.Count > MaxHistorySize) _history.RemoveAt(0);
    }

    private void NotifyPlaybackState() =>
        RaiseOnUI(() => OnPlaybackStateChanged?.Invoke(IsPlaying, IsPaused));

    private void SetLoadingState(bool value)
    {
        bool changed = HasFlag(StateFlags.Loading) != value;
        SetFlag(StateFlags.Loading, value);

        if (changed)
        {
            RaiseOnUI(() =>
            {
                this.RaisePropertyChanged(nameof(IsLoading));
                OnLoadingStateChanged?.Invoke(value);
            });
        }
    }

    public void NotifyAppMinimized() => _currentStream?.ReleaseRamBuffers();

    #endregion

    #region Command Queue

    private async Task ProcessCommandsAsync()
    {
        await foreach (var cmd in _commandQueue.Reader.ReadAllAsync())
        {
            if (HasFlag(StateFlags.Disposed)) break;
            try { await cmd(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error($"[AudioEngine] Command error: {ex.Message}"); }
        }
    }

    private async Task EnqueueCommandAsync(Func<ValueTask> command)
    {
        if (HasFlag(StateFlags.Disposed)) return;
        await _commandQueue.Writer.WriteAsync(command);
    }

    private async Task<T?> WithApiLockAsync<T>(Func<Task<T?>> action, CancellationToken ct) where T : class
    {
        if (!await _apiLock.WaitAsync(LockTimeoutMs, ct)) return null;
        try { return await action(); }
        finally { _apiLock.Release(); }
    }

    #endregion

    #region State Flags

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasFlag(StateFlags flag) =>
        (Volatile.Read(ref _stateFlags) & (int)flag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetFlag(StateFlags flag, bool value)
    {
        int current, desired;
        do
        {
            current = Volatile.Read(ref _stateFlags);
            desired = value ? current | (int)flag : current & ~(int)flag;
        } while (Interlocked.CompareExchange(ref _stateFlags, desired, current) != current);
    }

    #endregion

    #region UI Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RaiseOnUI(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            action();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    #endregion

    #region Configuration

    private static StreamingConfig GetStreamingConfig(InternetProfile profile) =>
        StreamingProfiles.GetConfig(profile);

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (HasFlag(StateFlags.Disposed)) return;
        SetFlag(StateFlags.Disposed, true);

        if (disposing)
        {
            _library.UpdateSettings(s => s.Volume = _volumePercent);
            _commandQueue.Writer.TryComplete();
            _playbackCts?.Cancel();

            try { _currentStream?.Dispose(); } catch { }
            try { _player?.Stop(); _player?.Dispose(); } catch { }
            try { _libVLC?.Dispose(); } catch { }
            try { _playbackLock.Dispose(); } catch { }
            try { _apiLock.Dispose(); } catch { }
            try { _httpClient.Dispose(); } catch { }

            Log.Info("[AudioEngine] Disposed");
        }

        base.Dispose(disposing);
    }

    #endregion
}