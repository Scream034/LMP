using System.Diagnostics;
using LibVLCSharp.Shared;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.ViewModels;
using ReactiveUI;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Стабильный аудио-движок на LibVLCSharp.
/// 
/// v3 - Исправления:
/// 1. Volume корректно рассчитывается
/// 2. Быстрый старт с минимальной буферизацией
/// 3. Prefetch для мгновенного переключения
/// </summary>
public class AudioEngine : ViewModelBase, IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    // LibVLC
    private readonly LibVLC _libVLC;
    private MediaPlayer? _player;
    private Media? _currentMedia;

    // State
    private readonly SemaphoreSlim _playLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private int _session;
    private int _volumePercent; // 0-100 (или до 200 для усиления)
    private volatile bool _isDisposed;
    private volatile bool _isPlayerReady;

    // Prefetch cache
    private readonly Dictionary<string, Media> _prefetchedMedia = new();
    private readonly SemaphoreSlim _prefetchLock = new(1, 1);

    // Queue & History
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
        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;

        // library.Data.Volume хранится как 0.0-1.0
        float savedVolume = library.Data.Volume;
        if (savedVolume <= 1.0f)
        {
            _volumePercent = (int)Math.Round(savedVolume * 100f);
        }
        else
        {
            _volumePercent = (int)Math.Round(savedVolume);
        }
        _volumePercent = Math.Clamp(_volumePercent, 0, 200);

        Debug.WriteLine($"[AudioEngine] Initial volume: {_volumePercent}%");

        // LibVLC с минимальной буферизацией для быстрого старта
        Core.Initialize();

        _libVLC = new LibVLC(
            "--no-video",
            "--no-spu",
            "--network-caching=1000",    // 1 сек - баланс скорости и стабильности
            "--file-caching=300",
            "--live-caching=1000",
            "--http-reconnect",
            "--no-http-forward-cookies"
        );

        InitializePlayer();

        Debug.WriteLine("[AudioEngine] Initialized v3");
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

    #region VOLUME — ИСПРАВЛЕНО

    /// <summary>
    /// Установить громкость.
    /// ВАЖНО: UI присылает float в диапазоне 0.0-1.0 (или 0.0-MaxVolume/100)
    /// LibVLC ожидает int 0-100 (до 200 для усиления)
    /// </summary>
    public void SetVolume(float value)
    {
        // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: 
        // Определяем входной формат и нормализуем к 0-100
        int percent;

        if (value <= 1.0f)
        {
            // Формат 0.0 - 1.0 → умножаем на 100
            percent = (int)Math.Round(value * 100f);
        }
        else
        {
            // Формат уже 0-100+ → округляем
            percent = (int)Math.Round(value);
        }

        _volumePercent = Math.Clamp(percent, 0, 200);

        Debug.WriteLine($"[Audio] SetVolume called: {value:F2} → {_volumePercent}%");

        // Сохраняем в нормализованном формате 0.0-1.0
        _library.Data.Volume = Math.Clamp(value, 0f, 2f);
        _library.Save();

        ApplyVolume();
    }

    /// <summary>
    /// Получить громкость в формате 0-100
    /// </summary>
    public float GetVolume() => _volumePercent;

    /// <summary>
    /// Получить громкость в формате 0.0-1.0 (для UI binding)
    /// </summary>
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

            Debug.WriteLine($"[Audio] Volume applied: {_volumePercent}% × {gainMultiplier:F2} (gain {dbGain:F1}dB) = {finalVolume}");
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

        // Быстрый захват lock
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

        // UI update
        IsLoading = true;
        CurrentTrack = track;
        _isPlayerReady = false;
        SafeInvoke(() => OnTrackChanged?.Invoke(track));

        // Cancel previous
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Stop current
        StopPlaybackInternal();

        try
        {
            Media? media = null;

            // 1. Проверяем prefetch cache
            if (_prefetchedMedia.TryGetValue(track.Id, out var cachedMedia))
            {
                await _prefetchLock.WaitAsync(ct);
                try
                {
                    if (_prefetchedMedia.Remove(track.Id))
                    {
                        media = cachedMedia;
                        Debug.WriteLine($"[Audio] #{session} Using prefetched media ({sw.ElapsedMilliseconds}ms)");
                    }
                }
                finally { _prefetchLock.Release(); }
            }

            // 2. Создаём media если не было в кэше
            if (media == null)
            {
                string source = await GetPlaybackSourceAsync(track, ct);
                if (_session != session || ct.IsCancellationRequested) return;

                if (string.IsNullOrEmpty(source))
                    throw new Exception("No playback source");

                media = CreateMedia(source);
                Debug.WriteLine($"[Audio] #{session} Media created ({sw.ElapsedMilliseconds}ms)");
            }

            if (_session != session || ct.IsCancellationRequested)
            {
                media.Dispose();
                return;
            }

            // 3. Dispose old media
            var oldMedia = _currentMedia;
            _currentMedia = media;
            oldMedia?.Dispose();

            // 4. Play
            _player!.Media = media;
            _player.Play();

            Debug.WriteLine($"[Audio] #{session} Play() called ({sw.ElapsedMilliseconds}ms)");

            AddToHistory(track);

            // 5. Prefetch next track
            _ = PrefetchNextInQueueAsync();

            // 6. Position loop
            _ = PositionUpdateLoopAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[Audio] #{session} Cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] #{session} Error: {ex.Message}");
            SafeInvoke(() => OnError?.Invoke(ex.Message));
            IsLoading = false;
        }
    }

    private Media CreateMedia(string source)
    {
        if (File.Exists(source))
        {
            return new Media(_libVLC, source, FromType.FromPath);
        }
        else
        {
            return new Media(_libVLC, source, FromType.FromLocation);
        }
    }

    private async Task<string> GetPlaybackSourceAsync(TrackInfo track, CancellationToken ct)
    {
        // Local file - instant
        if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
        {
            Debug.WriteLine("[Audio] Source: local file");
            return track.LocalPath;
        }

        // Cached URL - instant
        if (!string.IsNullOrEmpty(track.StreamUrl))
        {
            Debug.WriteLine("[Audio] Source: cached URL");
            return track.StreamUrl;
        }

        // Fetch URL
        Debug.WriteLine("[Audio] Source: fetching URL...");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var url = await _youtube.RefreshStreamUrlAsync(track, cts.Token);

        if (string.IsNullOrEmpty(url))
            throw new Exception("Failed to get stream URL");

        return url;
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
        // Ждём пока плеер станет готов
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

    #region PREFETCH — Ключ к мгновенному переключению

    public async Task PrefetchAsync(TrackInfo track)
    {
        if (_isDisposed) return;
        if (track.IsDownloaded) return;

        try
        {
            // Prefetch URL
            if (string.IsNullOrEmpty(track.StreamUrl))
            {
                await _youtube.RefreshStreamUrlAsync(track);
            }

            // Prefetch Media object
            if (!string.IsNullOrEmpty(track.StreamUrl))
            {
                await _prefetchLock.WaitAsync();
                try
                {
                    if (!_prefetchedMedia.ContainsKey(track.Id))
                    {
                        var media = CreateMedia(track.StreamUrl);

                        // Ограничиваем кэш
                        if (_prefetchedMedia.Count >= 3)
                        {
                            var oldest = _prefetchedMedia.First();
                            _prefetchedMedia.Remove(oldest.Key);
                            oldest.Value.Dispose();
                        }

                        _prefetchedMedia[track.Id] = media;
                        Debug.WriteLine($"[Audio] Prefetched media: {track.Title}");
                    }
                }
                finally { _prefetchLock.Release(); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] Prefetch error: {ex.Message}");
        }
    }

    private async Task PrefetchNextInQueueAsync()
    {
        try
        {
            // Prefetch первый трек в очереди
            if (_queue.Count > 0)
            {
                var next = _queue.Peek();
                await PrefetchAsync(next);
            }
        }
        catch { }
    }

    public void PrefetchRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var track in tracks.Take(2))
        {
            _ = PrefetchAsync(track);
        }
    }

    #endregion

    #region VLC EVENTS

    private void OnVlcPlaying(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        Debug.WriteLine("[Audio] VLC: Playing");
        _isPlayerReady = true;
        IsLoading = false;

        // КРИТИЧНО: Volume применяется здесь!
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

        // VLC требует другой поток!
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
        SafeInvoke(() => OnError?.Invoke("Playback error"));
    }

    private void OnVlcBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        if (_isDisposed) return;

        // Логируем только значимые изменения
        if (e.Cache < 100 && (int)e.Cache % 25 == 0)
        {
            Debug.WriteLine($"[Audio] Buffering: {e.Cache:F0}%");
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
                // Volume может сброситься
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

            // Volume после seek
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

        CurrentTrack = null;
        IsLoading = false;
        _isPlayerReady = false;

        SafeInvoke(() => OnTrackChanged?.Invoke(null));
        SafeInvoke(() => OnPlaybackStopped?.Invoke());
    }

    #endregion

    #region QUEUE

    public void Enqueue(TrackInfo track)
    {
        _queue.Enqueue(track);

        // Если ничего не играет — запускаем
        if (!IsPlaying && !IsPaused && !IsLoading)
        {
            _ = PlayTrackAsync(track);
            _queue.TryDequeue(out _);
        }
        else
        {
            // Prefetch
            _ = PrefetchAsync(track);
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

        // Cleanup prefetch cache
        foreach (var media in _prefetchedMedia.Values)
        {
            try { media.Dispose(); } catch { }
        }
        _prefetchedMedia.Clear();

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
        try { _prefetchLock.Dispose(); } catch { }

        Debug.WriteLine("[AudioEngine] Disposed");
    }
}