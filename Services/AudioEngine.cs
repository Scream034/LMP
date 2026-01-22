using System.Diagnostics;
using LibVLCSharp.Shared;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.ViewModels;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Services;

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

    // === НОВАЯ ЛОГИКА ОЧЕРЕДИ (Index Based) ===
    private readonly List<TrackInfo> _queue = [];
    private int _currentIndex = -1;

    // История прослушивания (для аналитики или UI "Recently Played")
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

    // === Properties ===

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }

    // Возвращаем копию для безопасности UI потоков
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

        Core.Initialize();
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
            _library.Data.LastVolume = _volumePercent; // Запоминаем текущую
            SetVolumeInstant(0);
        }
        else
        {
            // Восстанавливаем из LastVolume, если там пусто - ставим 50
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
            // 1. Проверяем, есть ли трек в текущей очереди
            int existingIndex = _queue.FindIndex(t => t.Id == track.Id);

            if (existingIndex >= 0)
            {
                // Если есть - просто прыгаем на него
                _currentIndex = existingIndex;
                // Обновляем данные трека (вдруг лайк изменился)
                _queue[existingIndex] = track;
            }
            else
            {
                // 2. Если трека нет - это новый контекст.
                // Очищаем очередь и ставим трек первым.
                // Примечание: Для "Добавить в конец" есть метод Enqueue.
                // PlayTrackAsync подразумевает "Я хочу слушать ЭТО прямо сейчас".
                _queue.Clear();
                _queue.Add(track);
                _currentIndex = 0;
                needsEvent = true;
            }
        }

        if (needsEvent) RaiseEvent(() => OnQueueChanged?.Invoke());

        await PlayCurrentIndexAsync();
    }

    // Внутренний метод для запуска воспроизведения по текущему индексу
    private async Task PlayCurrentIndexAsync()
    {
        TrackInfo? trackToPlay;
        lock (_queue)
        {
            if (_currentIndex >= 0 && _currentIndex < _queue.Count)
                trackToPlay = _queue[_currentIndex];
            else
                return; // Индекс вне диапазона
        }

        Log.Info($"[AudioEngine] Play Index: {_currentIndex} ({trackToPlay.Title})");
        SyncTrackPreferences(trackToPlay);

        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        var session = Interlocked.Increment(ref _session);
        Try(() => oldCts?.Cancel());

        ResetStreamInfo();

        IsLoading = true;
        CurrentTrack = trackToPlay;
        _isPlayerReady = false;

        RaiseEvent(() => OnTrackChanged?.Invoke(trackToPlay));
        // Для обновления UI выделения в плейлисте
        RaiseEvent(() => OnQueueChanged?.Invoke());

        _ = Task.Run(async () =>
        {
            if (!await _loadLock.WaitAsync(500)) return;
            try { await PlayTrackInternalAsync(trackToPlay, session, _cts.Token); }
            finally { _loadLock.Release(); }
        });
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

        // Перезапускаем текущий трек
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
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
                    // Если остановлено, пробуем играть текущий индекс
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

    /// <summary>
    /// Полностью заменяет очередь списком треков и начинает воспроизведение указанного трека.
    /// Используется при клике на трек внутри плейлиста/альбома.
    /// </summary>
    public async Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        if (_isDisposed) return;

        lock (_queue)
        {
            _queue.Clear();
            _queue.AddRange(tracks);

            // Ищем индекс стартового трека в новом списке
            _currentIndex = _queue.FindIndex(t => t.Id == startTrack.Id);

            // Если вдруг трек не найден в переданном списке (редкий баг), играем первый
            if (_currentIndex == -1 && _queue.Count > 0)
            {
                _currentIndex = 0;
            }
        }

        RaiseEvent(() => OnQueueChanged?.Invoke());

        // Запускаем воспроизведение
        await PlayCurrentIndexAsync();
    }

    public async Task PlayNextAsync()
    {
        // 1. Повтор одного трека
        if (RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await SeekAsync(TimeSpan.Zero);
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
            }
            else if (RepeatMode == RepeatMode.RepeatAll)
            {
                _currentIndex = 0;
                hasNext = true;
            }
        }

        if (hasNext) await PlayCurrentIndexAsync();
        else Stop(); // Конец очереди
    }

    public async Task PlayPreviousAsync()
    {
        // Если проиграло более 3 сек, возвращаемся в начало трека
        if (CurrentPosition.TotalSeconds > 3)
        {
            await SeekAsync(TimeSpan.Zero);
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
            }
            else if (RepeatMode == RepeatMode.RepeatAll)
            {
                _currentIndex = _queue.Count - 1;
                hasPrev = true;
            }
        }

        if (hasPrev) await PlayCurrentIndexAsync();
        else await SeekAsync(TimeSpan.Zero);
    }

    public void Enqueue(TrackInfo track)
    {
        lock (_queue)
        {
            // ✅ Защита от дублирования - проверяем по ID, не только последний
            if (_queue.Any(t => t.Id == track.Id))
                return;

            _queue.Add(track);
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());

        // Автоплей, если стоим
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
            // ✅ Фильтруем дубликаты
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

            // Если играет, оставляем только его
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

            // Запоминаем текущий
            var current = (_currentIndex >= 0 && _currentIndex < _queue.Count)
                ? _queue[_currentIndex]
                : null;

            // Алгоритм Фишера-Йетса
            var rng = Random.Shared;
            int n = _queue.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }

            // Находим новый индекс текущего трека
            if (current != null)
            {
                _currentIndex = _queue.IndexOf(current);
                if (_currentIndex == -1)
                {
                    _currentIndex = 0; // На всякий случай
                    _queue.Insert(0, current);
                }
            }
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());
    }

    // --- НОВЫЕ МЕТОДЫ ДЛЯ DRAG & DROP И УДАЛЕНИЯ ---

    public void RemoveFromQueue(TrackInfo track)
    {
        bool changed = false;
        bool needStop = false;

        lock (_queue)
        {
            int index = _queue.FindIndex(t => t.Id == track.Id);
            if (index == -1) return;

            // Если удаляем текущий играющий
            if (index == _currentIndex)
            {
                // Если есть следующий, индекс останется тем же (но указывать будет на след.)
                // Если это был последний, нужно уменьшить индекс
                if (index == _queue.Count - 1)
                {
                    _currentIndex--; // Станет указывать на предпоследний (который теперь последний) или -1
                }

                // В идеале можно остановить, но лучше играть дальше
                // Если удалили играющий, плеер продолжит играть буфер, но логически трека уже нет.
                // Решение: Если удалили играющий, переключаем на следующий сразу или стоп.
                needStop = _queue.Count == 1; // Был единственный
            }
            else if (index < _currentIndex)
            {
                // Удалили трек ДО курсора, нужно сместить курсор влево
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

            // Коррекция курсора воспроизведения
            if (_currentIndex == oldIndex)
            {
                _currentIndex = newIndex;
            }
            else if (oldIndex < _currentIndex && newIndex >= _currentIndex)
            {
                _currentIndex--; // Трек переехал из "до" в "после", курсор влево
            }
            else if (oldIndex > _currentIndex && newIndex <= _currentIndex)
            {
                _currentIndex++; // Трек переехал из "после" в "до", курсор вправо
            }
        }
        RaiseEvent(() => OnQueueChanged?.Invoke());
    }

    // ----------------------------------------------

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;
        _history.Add(track);
        if (_history.Count > MaxHistorySize) _history.RemoveAt(0);
    }

    // === Stream Info & API ===
    // (Без изменений, копия из оригинального кода для целостности, но сокращу для ответа)
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
        IsPlaying = false;
        IsPaused = false;
        NotifyPlaybackState();

        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            if (!_isDisposed) await PlayNextAsync();
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