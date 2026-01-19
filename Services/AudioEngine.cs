using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MyLiteMusicPlayer.Models;
using PlaybackState = NAudio.Wave.PlaybackState;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Высокопроизводительный аудио движок.
/// 
/// ОПТИМИЗАЦИИ:
/// 1. Минимальный DesiredLatency (100ms)
/// 2. Предзагрузка URL при hover (PrefetchAsync)
/// 3. Атомарные сессии без тяжёлых блокировок
/// 4. Переиспользование WaveOutEvent
/// 
/// TODO для ещё большей скорости:
/// - FFmpeg pipe для мгновенного streaming
/// - libmpv интеграция
/// - WASAPI для низкой латентности
/// </summary>
public class AudioEngine : IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    // Playback state
    private WaveOutEvent? _output;
    private WaveStream? _stream;
    private VolumeSampleProvider? _volume;

    // Session management - простой атомарный счётчик
    private CancellationTokenSource? _cts;
    private int _session;
    private float _volumeLevel;

    // Queue & History
    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;

    // Public state
    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public TimeSpan CurrentPosition => _stream?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalDuration => _stream?.TotalTime ?? CurrentTrack?.Duration ?? TimeSpan.Zero;
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
            OnLoadingChanged?.Invoke(value);
        }
    }

    // Events
    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;

    public AudioEngine(YoutubeProvider youtube, LibraryService library, DownloadService _)
    {
        _youtube = youtube;
        _library = library;
        _volumeLevel = library.Data.Volume;
        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;
    }

    #region MAIN PLAYBACK

    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return;

        // 1. New session - atomically invalidates all previous operations
        int session = Interlocked.Increment(ref _session);
        Debug.WriteLine($"[Audio] #{session} → {track.Title}");

        // 2. Update UI IMMEDIATELY (responsive feel)
        IsLoading = true;
        CurrentTrack = track;
        OnTrackChanged?.Invoke(track);

        // 3. Cancel previous operation
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 4. Stop current playback (fast, synchronous)
        DisposePlayback();

        try
        {
            // 5. Get stream URL (may already be cached)
            string? url = await GetStreamUrlAsync(track, ct);
            if (_session != session) return; // Check after async

            // 6. Create audio reader
            var sw = Stopwatch.StartNew();
            var reader = await CreateReaderAsync(track, url, ct);
            Debug.WriteLine($"[Audio] #{session} Reader: {sw.ElapsedMilliseconds}ms");

            if (_session != session)
            {
                reader.Dispose();
                return;
            }

            // 7. Setup playback chain
            _stream = reader;
            _volume = new VolumeSampleProvider(reader.ToSampleProvider());
            ApplyVolume();

            // 8. Create output (reuse causes issues, create new)
            _output = new WaveOutEvent
            {
                DesiredLatency = 100,  // Low latency
                NumberOfBuffers = 2    // Minimum buffers
            };
            _output.PlaybackStopped += OnOutputStopped;
            _output.Init(_volume);
            _output.Play();

            Debug.WriteLine($"[Audio] #{session} Playing!");

            // 9. Track history & start position updates
            AddToHistory(track);
            _ = UpdatePositionLoopAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[Audio] #{session} Cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] #{session} Error: {ex.Message}");
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            if (_session == session)
                IsLoading = false;
        }
    }

    private async Task<string?> GetStreamUrlAsync(TrackInfo track, CancellationToken ct)
    {
        // Local file - no URL needed
        if (track.IsDownloaded && File.Exists(track.LocalPath))
            return null;

        // Already have cached URL
        if (!string.IsNullOrEmpty(track.StreamUrl))
            return track.StreamUrl;

        // Fetch from YouTube (with timeout)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var url = await _youtube.RefreshStreamUrlAsync(track, cts.Token);
        if (string.IsNullOrEmpty(url))
            throw new Exception("Cannot get stream URL");

        return url;
    }

    private static async Task<WaveStream> CreateReaderAsync(TrackInfo track, string? url, CancellationToken ct)
    {
        // Local file - fast path
        if (track.IsDownloaded && File.Exists(track.LocalPath))
            return new AudioFileReader(track.LocalPath);

        // Remote stream - runs on thread pool to not block UI
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return new MediaFoundationReader(url!, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = true
            });
        }, ct);
    }

    private void DisposePlayback()
    {
        // Dispose output device
        if (_output != null)
        {
            _output.PlaybackStopped -= OnOutputStopped;
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            _output = null;
        }

        // Dispose stream in background (can be slow for large streams)
        var oldStream = _stream;
        _stream = null;
        _volume = null;

        if (oldStream != null)
        {
            _ = Task.Run(() =>
            {
                try { oldStream.Dispose(); }
                catch (Exception ex) { Debug.WriteLine($"[Audio] Dispose error: {ex.Message}"); }
            });
        }
    }

    private async Task UpdatePositionLoopAsync(int session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _session == session)
            {
                if (_output?.PlaybackState == PlaybackState.Playing && _stream != null)
                {
                    try { OnPositionChanged?.Invoke(_stream.CurrentTime); } catch { }
                }
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region PREFETCH (OPTIMIZATION)

    /// <summary>
    /// Предзагружает stream URL для трека.
    /// Вызывать при hover на элемент списка для мгновенного старта.
    /// </summary>
    public async Task PrefetchAsync(TrackInfo track)
    {
        if (track.IsDownloaded) return;
        if (!string.IsNullOrEmpty(track.StreamUrl)) return;

        try
        {
            await _youtube.RefreshStreamUrlAsync(track);
            Debug.WriteLine($"[Audio] Prefetched: {track.Title}");
        }
        catch { /* Ignore prefetch errors */ }
    }

    /// <summary>
    /// Предзагружает несколько треков (например, следующие в очереди)
    /// </summary>
    public void PrefetchRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var track in tracks.Take(3))
        {
            _ = PrefetchAsync(track);
        }
    }

    #endregion

    #region TRANSPORT CONTROLS

    public void SetVolume(float value)
    {
        _volumeLevel = Math.Clamp(value, 0f, 4f);
        _library.Data.Volume = _volumeLevel;
        _library.Save();
        ApplyVolume();
    }

    public float GetVolume() => _volumeLevel;

    private void ApplyVolume()
    {
        if (_volume == null) return;
        float dbGain = _library.Data.TargetGainDb;
        float dbMult = (float)Math.Pow(10, dbGain / 20.0);
        _volume.Volume = _volumeLevel * dbMult;
    }

    public void TogglePlayPause()
    {
        if (_output == null && CurrentTrack != null)
        {
            _ = PlayTrackAsync(CurrentTrack);
            return;
        }

        if (IsPlaying) _output?.Pause();
        else if (IsPaused) _output?.Play();
    }

    public void Seek(TimeSpan position)
    {
        if (_stream == null) return;

        var target = position;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > _stream.TotalTime) target = _stream.TotalTime;

        try { _stream.CurrentTime = target; }
        catch (Exception ex) { Debug.WriteLine($"[Audio] Seek error: {ex.Message}"); }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _session);
        _cts?.Cancel();
        DisposePlayback();

        CurrentTrack = null;
        OnTrackChanged?.Invoke(null);
        IsLoading = false;
    }

    #endregion

    #region QUEUE

    public void Enqueue(TrackInfo track)
    {
        if (_output?.PlaybackState == PlaybackState.Stopped && !IsLoading)
            _ = PlayTrackAsync(track);
        else
            _queue.Enqueue(track);
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var t in tracks) Enqueue(t);
    }

    public void ClearQueue() => _queue.Clear();

    public async Task PlayNextAsync()
    {
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

        if (next != null) await PlayTrackAsync(next);
        else Stop();
    }

    public async Task PlayPreviousAsync()
    {
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

    #region EVENT HANDLERS

    private void OnOutputStopped(object? sender, StoppedEventArgs e)
    {
        // Ignore if we're loading a new track
        if (IsLoading) return;

        if (e.Exception != null)
        {
            Debug.WriteLine($"[Audio] Device error: {e.Exception.Message}");
            OnError?.Invoke(e.Exception.Message);
            return;
        }

        // Check if track naturally finished
        bool finished = _stream != null &&
                        _stream.TotalTime.TotalSeconds > 0 &&
                        (_stream.TotalTime - _stream.CurrentTime).TotalSeconds < 1;

        if (finished)
        {
            Debug.WriteLine("[Audio] Track finished, next...");
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
        Interlocked.Increment(ref _session);
        _cts?.Cancel();
        _cts?.Dispose();
        DisposePlayback();
    }
}