using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using LMP.Core.Audio;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Services;

public sealed class AudioEngine : ViewModelBase, IDisposable
{
    #region Constants

    private const int MaxConsecutiveErrors = 3;
    private const int MaxHistorySize = 100;

    #endregion

    #region Dependencies

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly AudioPlayer _player;

    #endregion

    #region Synchronization

    private readonly Channel<Func<Task>> _commandQueue;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Lock _queueLock = new();
    private readonly Lock _seekDebounce = new();
    private volatile int _session;

    #endregion

    #region State

    private int _consecutiveErrors;
    private int _volumePercent;
    private bool _volumeInitialized;
    private CancellationTokenSource? _lastSeekCts;

    #endregion

    #region Queue

    private readonly List<TrackInfo> _queue = new(64);
    private readonly List<TrackInfo> _history = new(MaxHistorySize);
    private int _currentIndex = -1;

    #endregion

    #region Properties

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }

    [Reactive] public AudioStreamInfo StreamInfo { get; private set; } = AudioStreamInfo.Empty;

    public bool IsPlaying => _player.State == PlaybackState.Playing;
    public bool IsPaused => _player.State == PlaybackState.Paused;
    public bool IsLoading => _player.State == PlaybackState.Loading || _player.State == PlaybackState.Buffering;

    [AllowNull]
    public IReadOnlyList<TrackInfo> Queue
    {
        get
        {
            lock (_queueLock)
            {
                field ??= [.. _queue];
                return field;
            }
        }
        private set;
    }

    public int CurrentQueueIndex => Volatile.Read(ref _currentIndex);
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    public TimeSpan CurrentPosition => _player.Position;
    public TimeSpan TotalDuration => _player.Duration;

    public double BufferProgress => _player.BufferProgress;
    public bool IsFullyBuffered => _player.IsFullyBuffered;

    #endregion

    #region Events

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<bool, bool>? OnPlaybackStateChanged;
    public event Action? OnQueueChanged;
    public event Action<bool>? OnLoadingStateChanged;
    public event Action<string, string>? OnCriticalError;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action<AudioStreamInfo>? OnStreamInfoChanged;
    public event Action<BufferState>? OnBufferStateChanged;

    #endregion

    public AudioEngine(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;

        var options = new AudioPlayerOptions
        {
            UrlRefreshCallback = RefreshUrlCallback,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(200),
            MaxRetryAttempts = 3,
            UseNullBackend = false
        };

        _player = new AudioPlayer(options);

        _player.Events.PositionChanged += pos => RaiseOnUI(() => OnPositionChanged?.Invoke(pos));
        _player.Events.StateChanged += OnPlayerStateChanged;
        _player.Events.TrackEnded += OnPlayerTrackEnded;
        _player.Events.ErrorOccurred += err => RaiseOnUI(() => OnError?.Invoke(err.Message));
        _player.Events.StreamInfoChanged += OnStreamInfoReceived;
        _player.Events.BufferStateChanged += state =>
        {
            Log.Debug($"[AudioEngine] BufferStateChanged received: progress={state.Progress:F1}%, " +
                      $"ranges={state.Ranges.Count}");

            RaiseOnUI(() => OnBufferStateChanged?.Invoke(state));
        };

        ShuffleEnabled = library.Settings.ShuffleEnabled;
        RepeatMode = library.Settings.RepeatMode;

        var savedVolume = library.Settings.Volume;
        _volumePercent = savedVolume > 0 ? NormalizeVolume(savedVolume) : 60;

        ApplyVolume();

        _commandQueue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _ = ProcessCommandsAsync();
        _ = VolumeSaveLoopAsync();

        Log.Info($"[AudioEngine] Native Engine Ready. Volume={_volumePercent}%");
    }

    #region Stream Info

    private void OnStreamInfoReceived(AudioStreamInfo info)
    {
        RaiseOnUI(() =>
        {
            StreamInfo = info;
            OnStreamInfoChanged?.Invoke(info);

            Log.Debug($"[AudioEngine] Stream info: {info.FormatDisplay}");
        });
    }

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        var info = StreamInfo;
        return (info.Codec, info.Bitrate, info.IsValid);
    }

    #endregion

    #region Public API

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        lock (_queueLock)
        {
            _queue.AddRange(tracks);
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

            var n = _queue.Count;
            while (n > 1)
            {
                n--;
                var k = Random.Shared.Next(n + 1);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }

            if (current != null)
            {
                var newIndex = _queue.IndexOf(current);
                if (newIndex != 0)
                {
                    _queue.RemoveAt(newIndex);
                    _queue.Insert(0, current);
                    _currentIndex = 0;
                }
                else
                {
                    _currentIndex = 0;
                }
            }
            else
            {
                _currentIndex = -1;
            }

            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    public void UpdateAudioSettings()
    {
        ApplyVolume();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
    }

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        Log.Info($"[AudioEngine] Profile switched to {profile}. (No-op in Native Engine)");
        return Task.CompletedTask;
    }

    public static void NotifyAppMinimized()
    {
        GC.Collect(1, GCCollectionMode.Optimized, false);
    }

    /// <summary>
    /// Переключает качество потока с сохранением позиции.
    /// </summary>
    public async Task SwitchQualityAsync(string container, int bitrate)
    {
        if (CurrentTrack == null) return;

        var pos = CurrentPosition;
        var track = CurrentTrack;

        track.TransientContainer = container;
        track.TransientBitrate = bitrate;

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredContainer = container;
            track.PreferredBitrate = bitrate;
        }

        Log.Info($"[AudioEngine] Switching quality to {container}/{bitrate}kbps at {pos.TotalSeconds:F1}s");

        var session = Interlocked.Increment(ref _session);

        await EnqueueCommandAsync(async () =>
        {
            if (_session != session) return;

            try
            {
                await _player.StopAsync();

                track.StreamUrl = "";

                var streamInfo = await _youtube.RefreshStreamUrlAsync(track, true, CancellationToken.None);
                if (streamInfo == null)
                {
                    Log.Error("[AudioEngine] Failed to get new stream URL");
                    RaiseOnUI(() => OnError?.Invoke("Failed to switch quality"));
                    return;
                }

                // Пробрасываем bitrate hint для точного cacheKey
                await _player.PlayAsync(streamInfo.Value.Url, track.Id, bitrate, CancellationToken.None);

                // Seek сразу после PlayAsync, не через delay
                if (pos.TotalSeconds > 1)
                {
                    // Ждём пока декодер начнёт работать
                    await WaitForPlayerReadyAsync(TimeSpan.FromSeconds(2));
                    await _player.SeekAsync(pos);
                }

                Log.Info($"[AudioEngine] Quality switched to {container}/{bitrate}kbps at {pos.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioEngine] Quality switch failed: {ex.Message}");
                RaiseOnUI(() => OnError?.Invoke("Failed to switch quality"));
            }
        });
    }

    /// <summary>
    /// Ждёт пока плеер будет в состоянии Playing или Paused.
    /// </summary>
    private async Task WaitForPlayerReadyAsync(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var state = _player.State;
            if (state == PlaybackState.Playing || state == PlaybackState.Paused)
                return;

            await Task.Delay(50);
        }
    }

    public long GetDownloadedBytes() => _player.GetDownloadedBytes();

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => _player.GetBufferedRanges();

    #endregion

    #region Playback Control

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;
        var session = Interlocked.Increment(ref _session);

        return EnqueueCommandAsync(async () =>
        {
            if (_session != session) return;
            lock (_queueLock)
            {
                var idx = _queue.FindIndex(t => t.Id == track.Id);
                if (idx >= 0) { _currentIndex = idx; _queue[idx] = track; }
                else { _queue.Clear(); _queue.Add(track); _currentIndex = 0; InvalidateQueueSnapshot(); }
            }
            RaiseOnUI(() => OnQueueChanged?.Invoke());
            await PlayCurrentIndexAsync(session);
        });
    }

    public Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        var session = Interlocked.Increment(ref _session);
        return EnqueueCommandAsync(async () =>
        {
            if (_session != session) return;
            lock (_queueLock)
            {
                _queue.Clear(); _queue.AddRange(tracks);
                _currentIndex = _queue.FindIndex(t => t.Id == startTrack.Id);
                if (_currentIndex == -1 && _queue.Count > 0) _currentIndex = 0;
                InvalidateQueueSnapshot();
            }
            RaiseOnUI(() => OnQueueChanged?.Invoke());
            await PlayCurrentIndexAsync(session);
        });
    }

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        await EnqueueCommandAsync(async () =>
        {
            if (shouldPlay)
            {
                if (_player.State == PlaybackState.Paused) _player.Resume();
                else if (_player.State == PlaybackState.Stopped && CurrentTrack != null) await PlayCurrentIndexAsync(_session);
            }
            else _player.Pause();
        });
    }

    public async ValueTask SeekAsync(TimeSpan position)
    {
        lock (_seekDebounce)
        {
            _lastSeekCts?.Cancel();
            _lastSeekCts?.Dispose();
            _lastSeekCts = new CancellationTokenSource();
        }

        var localCts = _lastSeekCts;

        try
        {
            await Task.Delay(50, localCts.Token);
            await _player.SeekAsync(position, localCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Seek error: {ex.Message}");
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _session);
        _player.Stop();
        RaiseOnUI(() =>
        {
            CurrentTrack = null;
            StreamInfo = AudioStreamInfo.Empty;
            OnTrackChanged?.Invoke(null);
            OnPlaybackStopped?.Invoke();
        });
    }

    public Task PlayNextAsync() => NavigateAsync(true, true);
    public Task PlayPreviousAsync() => NavigateAsync(false, true);

    #endregion

    #region Internal Logic

    private async Task PlayCurrentIndexAsync(int session)
    {
        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
            track = _queue[_currentIndex];
        }
        if (track == null) return;

        RaiseOnUI(() =>
        {
            CurrentTrack = track;
            StreamInfo = AudioStreamInfo.Empty;
            OnTrackChanged?.Invoke(track);
            OnPositionChanged?.Invoke(TimeSpan.Zero);
        });

        try
        {
            string? streamUrl = track.StreamUrl;
            int bitrateHint = track.TransientBitrate;

            // Проверяем кэш
            var cached = AudioSourceFactory.FindAnyCachedTrack(track.Id);

            if (cached != null && string.IsNullOrEmpty(streamUrl))
            {
                Log.Debug($"[AudioEngine] Using cache: {cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps");
                streamUrl = "";
                bitrateHint = cached.Value.Entry.Bitrate;
            }
            else if (string.IsNullOrEmpty(streamUrl))
            {
                var streamInfo = await _youtube.RefreshStreamUrlAsync(track, false, CancellationToken.None);
                if (streamInfo == null) throw new Exception("Failed to resolve URL");
                streamUrl = streamInfo.Value.Url;
                // Используем битрейт из stream info если не задан явно
                if (bitrateHint <= 0)
                    bitrateHint = streamInfo.Value.Bitrate;
            }

            if (_session != session) return;

            await _player.PlayAsync(streamUrl ?? "", track.Id, bitrateHint, CancellationToken.None);
            AddToHistory(track);
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Play error: {ex.Message}");
            RaiseOnUI(() => OnError?.Invoke(ex.Message));
            await HandlePlaybackErrorAsync();
        }
    }

    private async ValueTask<string?> RefreshUrlCallback(string trackId, CancellationToken ct)
    {
        var track = await _library.GetTrackAsync(trackId);
        if (track == null) return null;
        var info = await _youtube.RefreshStreamUrlAsync(track, true, ct);
        return info?.Url;
    }

    private async Task NavigateAsync(bool forward, bool userInitiated)
    {
        var session = Interlocked.Increment(ref _session);
        bool canMove;
        lock (_queueLock) { canMove = forward ? TryMoveNext(userInitiated) : TryMovePrevious(); }

        if (canMove) await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
        else if (!forward && _player.State != PlaybackState.Stopped) await _player.SeekAsync(TimeSpan.Zero);
        else Stop();
    }

    private bool TryMoveNext(bool userInitiated)
    {
        if (_queue.Count == 0) return false;
        if (!userInitiated && RepeatMode == RepeatMode.RepeatOne) return true;
        if (_currentIndex + 1 < _queue.Count) { _currentIndex++; return true; }
        if (RepeatMode == RepeatMode.RepeatAll) { _currentIndex = 0; return true; }
        return false;
    }

    private bool TryMovePrevious()
    {
        if (_queue.Count == 0) return false;
        if (CurrentPosition.TotalSeconds > 3) return false;
        if (_currentIndex > 0) { _currentIndex--; return true; }
        if (RepeatMode == RepeatMode.RepeatAll) { _currentIndex = _queue.Count - 1; return true; }
        return false;
    }

    private async Task HandlePlaybackErrorAsync()
    {
        if (++_consecutiveErrors >= MaxConsecutiveErrors)
        {
            Stop();
            RaiseOnUI(() => OnCriticalError?.Invoke("Error", "Too many playback errors"));
            _consecutiveErrors = 0;
            return;
        }
        await Task.Delay(1000);
        await PlayNextAsync();
    }

    private void OnPlayerStateChanged(PlaybackState state)
    {
        RaiseOnUI(() =>
        {
            this.RaisePropertyChanged(nameof(IsPlaying));
            this.RaisePropertyChanged(nameof(IsPaused));
            this.RaisePropertyChanged(nameof(IsLoading));
            this.RaisePropertyChanged(nameof(TotalDuration));

            OnPlaybackStateChanged?.Invoke(state == PlaybackState.Playing, state == PlaybackState.Paused);
            OnLoadingStateChanged?.Invoke(state == PlaybackState.Loading || state == PlaybackState.Buffering);

            if (state == PlaybackState.Playing) _consecutiveErrors = 0;
        });
    }

    private void OnPlayerTrackEnded()
    {
        var session = Interlocked.Increment(ref _session);
        _ = Task.Run(async () =>
        {
            bool canAdvance;
            lock (_queueLock) { canAdvance = TryMoveNext(false); }
            if (canAdvance) await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
            else Stop();
        });
    }

    #endregion

    #region Volume

    public void SaveVolumeNow() => _library.UpdateSettings(s => s.Volume = _volumePercent);
    public float GetVolume() => _volumePercent;

    public void SetVolumeInstant(float value)
    {
        _volumePercent = Math.Clamp((int)Math.Round(value), 0, 100);
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        float linearPercent = _volumePercent / 100f;
        float perceivedVolume = linearPercent * linearPercent;

        float gain = MathF.Pow(10f, Math.Clamp(_library.Settings.TargetGainDb, -20f, 20f) / 20f);

        _player.Volume = perceivedVolume * gain;
    }

    public void InitializeVolumeFromSettings()
    {
        if (_volumeInitialized) return;

        var savedVolume = _library.Settings.Volume;

        if (savedVolume > 0 && savedVolume <= 1.0f)
        {
            _volumePercent = (int)(savedVolume * 100);
        }
        else if (savedVolume > 1)
        {
            _volumePercent = Math.Clamp((int)savedVolume, 0, 100);
        }
        else
        {
            _volumePercent = 50;
        }

        _volumeInitialized = true;
        ApplyVolume();

        Log.Info($"[AudioEngine] Volume initialized: {_volumePercent}%");
    }

    private async Task VolumeSaveLoopAsync()
    {
        while (!_lifetimeCts.IsCancellationRequested)
        {
            await Task.Delay(2000, _lifetimeCts.Token);
            _library.UpdateSettings(s => s.Volume = _volumePercent);
        }
    }

    private static int NormalizeVolume(float saved) =>
        Math.Clamp((int)saved, 0, 100);

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
            var session = Interlocked.Increment(ref _session);
            _ = EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
        }
    }

    public void ClearQueue()
    {
        lock (_queueLock)
        {
            var current = CurrentTrack;
            _queue.Clear();
            _currentIndex = -1;
            if (current != null) { _queue.Add(current); _currentIndex = 0; }
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
            if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count) return;
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

    private void InvalidateQueueSnapshot() { Queue = null; }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;
        _history.Add(track);
        if (_history.Count > MaxHistorySize) _history.RemoveAt(0);
    }

    #endregion

    #region Helpers

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(_lifetimeCts.Token))
            {
                await cmd();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task EnqueueCommandAsync(Func<Task> command)
    {
        await _commandQueue.Writer.WriteAsync(command);
    }

    private static void RaiseOnUI(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_seekDebounce)
            {
                _lastSeekCts?.Cancel();
                _lastSeekCts?.Dispose();
            }

            _lifetimeCts.Cancel();
            _player.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}