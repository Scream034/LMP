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
    private const int RefreshTimeoutS = 60;

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
    private volatile bool _suppressAutoNext;

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
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 3,
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
        if (_isDisposed) return;

        // 1. Захватываем лок только для определения следующего трека
        if (!await _navigationLock.WaitAsync(500))
        {
            Log.Warn("[AudioEngine] PlayCurrentIndexAsync timeout waiting for lock");
            return;
        }

        TrackInfo? trackToPlay;
        int session;

        try
        {
            if (_isNavigating) return; // Двойная проверка
            _isNavigating = true;

            lock (_queue)
            {
                if (_currentIndex < 0 || _currentIndex >= _queue.Count)
                {
                    Log.Warn($"[AudioEngine] Invalid index: {_currentIndex}, queue size: {_queue.Count}");
                    _isNavigating = false;
                    return;
                }
                trackToPlay = _queue[_currentIndex];
            }

            if (trackToPlay == null)
            {
                Log.Warn("[AudioEngine] Track at current index is null");
                _isNavigating = false;
                return;
            }

            // Получаем "билет" на выполнение этой сессии воспроизведения
            session = Interlocked.Increment(ref _session);
        }
        finally
        {
            // 2. ОСВОБОЖДАЕМ ЛОК КАК МОЖНО СКОРЕЕ!
            _navigationLock.Release();
        }

        // 3. Вся долгая работа (остановка, загрузка) происходит уже БЕЗ блокировки
        await LoadAndPlayTrackAsync(trackToPlay, session);
    }

    /// <summary>
    /// "Мозг" навигации вперед. Определяет следующий индекс для воспроизведения.
    /// Возвращает true, если нужно начать воспроизведение, и false, если очередь закончилась.
    /// </summary>
    /// <param name="userInitiated">True, если действие вызвано пользователем (кнопка "далее").</param>
    /// <returns>True, если есть следующий трек для воспроизведения.</returns>
    private bool TryAdvanceQueue(bool userInitiated)
    {
        lock (_queue)
        {
            if (_queue.Count == 0) return false;

            // Логика для RepeatOne: срабатывает только при автоматическом окончании трека
            if (!userInitiated && RepeatMode == RepeatMode.RepeatOne)
            {
                Log.Info("[AudioEngine] RepeatOne: Preparing to restart current track.");
                return true; // Индекс не меняем, просто говорим, что нужно играть
            }

            // Есть ли следующий трек в списке?
            if (_currentIndex + 1 < _queue.Count)
            {
                _currentIndex++;
                Log.Info($"[AudioEngine] Advancing to next track: index {_currentIndex}");
                return true;
            }

            // Мы в конце списка. Проверяем режим RepeatAll.
            if (RepeatMode == RepeatMode.RepeatAll)
            {
                _currentIndex = 0;
                Log.Info("[AudioEngine] RepeatAll: Wrapping back to first track.");
                return true;
            }

            // Очередь закончилась, и повтор выключен
            Log.Info("[AudioEngine] Queue ended.");
            return false;
        }
    }

    /// <summary>
    /// "Мозг" навигации назад. Определяет предыдущий индекс для воспроизведения.
    /// </summary>
    /// <returns>True, если есть предыдущий трек для воспроизведения.</returns>
    private bool TryRetreatQueue()
    {
        lock (_queue)
        {
            if (_queue.Count == 0) return false;

            // Есть ли предыдущий трек?
            if (_currentIndex - 1 >= 0)
            {
                _currentIndex--;
                Log.Info($"[AudioEngine] Retreating to previous track: index {_currentIndex}");
                return true;
            }

            // Мы в начале списка. Проверяем режим RepeatAll.
            if (RepeatMode == RepeatMode.RepeatAll)
            {
                _currentIndex = _queue.Count - 1;
                Log.Info("[AudioEngine] RepeatAll: Wrapping around to last track.");
                return true;
            }

            return false;
        }
    }

    private async Task LoadAndPlayTrackAsync(TrackInfo trackToPlay, int session)
    {
        try
        {
            Log.Info($"[AudioEngine] Playing index {_currentIndex}: {trackToPlay.Title}");
            SyncTrackPreferences(trackToPlay);

            _suppressAutoNext = true;

            // Отменяем предыдущие операции
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            if (oldCts != null) Try(oldCts.Cancel);

            // Ждём освобождения ресурсов от старого трека
            await CleanupCurrentMediaAsync();

            // Проверяем, не устарела ли наша сессия, пока мы ждали
            if (_session != session)
            {
                Log.Info("[AudioEngine] Session changed during cleanup, aborting load");
                return;
            }

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
                if (!await _loadLock.WaitAsync(2000))
                {
                    Log.Warn("[AudioEngine] Could not acquire load lock - forcing release");
                    return;
                }

                try
                {
                    // Финальная проверка сессии перед началом загрузки
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
        finally
        {
            // Вне зависимости от результата, помечаем, что навигация завершена
            _isNavigating = false;
        }
    }

    /// <summary>
    /// Внутренний метод для перезапуска текущего трека.
    /// ВАЖНО: Вызывается ТОЛЬКО из кода, который уже удерживает _navigationLock.
    /// </summary>
    private async Task RestartCurrentTrackInternalAsync()
    {
        TrackInfo? trackToPlay = CurrentTrack;
        if (trackToPlay == null)
        {
            Log.Warn("[AudioEngine] Restart requested, but CurrentTrack is null.");
            return;
        }

        Log.Info($"[AudioEngine] Restarting track: {trackToPlay.Title}");

        // Используем тот же механизм сессий для отмены старых операций
        var session = Interlocked.Increment(ref _session);
        _suppressAutoNext = true;

        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        if (oldCts != null) Try(oldCts.Cancel);

        await CleanupCurrentMediaAsync();

        if (_session != session)
        {
            Log.Info("[AudioEngine] Session changed during restart cleanup, aborting.");
            return;
        }

        ResetStreamInfo();
        _isPlayerReady = false;

        var cts = _cts;

        // Запускаем загрузку точно так же, как в основном методе
        _ = Task.Run(async () =>
        {
            if (!await _loadLock.WaitAsync(2000)) return;
            try
            {
                if (_session != session || cts.IsCancellationRequested) return;
                await PlayTrackInternalAsync(trackToPlay, session, cts.Token);
            }
            catch (OperationCanceledException) { Log.Info("[AudioEngine] Restart cancelled."); }
            catch (Exception ex) { Log.Error($"[AudioEngine] Restart error: {ex.Message}"); }
            finally { _loadLock.Release(); }
        });
    }

    /// <summary>
    /// Асинхронно очищает текущий медиа и стрим БЕЗ блокировки UI
    /// </summary>
    private async Task CleanupCurrentMediaAsync()
    {
        // --- ИЗМЕНЕНО: Более надежная логика очистки для предотвращения дедлоков ---

        var oldMedia = _currentMedia;
        var oldStream = _currentStream;
        var player = _player; // Создаем локальную копию, чтобы избежать проблем в многопоточной среде

        // Немедленно отсоединяем ссылки, чтобы новые операции не использовали старые объекты
        _currentMedia = null;
        _currentStream = null;

        if (oldStream == null && oldMedia == null) return;

        // 1. CRITICAL FIX: Отменяем все ожидающие операции чтения в стриме.
        // Это заставит любой заблокированный вызов Read() немедленно вернуться с ошибкой или 0,
        // что разблокирует внутренние потоки VLC.
        try
        {
            oldStream?.CancelPendingReads();
        }
        catch (ObjectDisposedException) { /* Игнорируем, стрим уже уничтожен */ }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Error in CancelPendingReads: {ex.Message}");
        }

        // 2. NEW: Отсоединяем медиа-объект от плеера ПЕРЕД вызовом Stop().
        // Это важнейший шаг, который говорит VLC "забыть" о старом стриме,
        // предотвращая попытки чтения из него во время остановки.
        if (player != null && player.Media == oldMedia)
        {
            player.Media = null;
        }

        // 3. Теперь безопасно останавливаем плеер. Он больше не привязан к старому стриму и не должен зависнуть.
        if (player != null && player.State != VLCState.Stopped && player.State != VLCState.Error)
        {
            // Запускаем в фоновом потоке, но ждем завершения.
            await Task.Run(() => Try(player.Stop));
        }

        // 4. Запускаем полную очистку ресурсов (закрытие файла, сохранение метаданных)
        // в фоновом режиме, не блокируя основной поток.
        _ = Task.Run(() =>
        {
            try
            {
                // Небольшая задержка, чтобы дать VLC время полностью завершить свои внутренние обратные вызовы
                Thread.Sleep(100);
                oldStream?.Dispose();
                oldMedia?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioEngine] Background cleanup error: {ex.Message}");
            }
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

            // 1. Get Stream Details
            StreamDetails? stream = null;
            try { stream = await GetOrRefreshStreamAsync(track, ct); }
            catch (OperationCanceledException) { return; }

            if (stream == null)
            {
                if (ct.IsCancellationRequested) return;
                throw new Exception("Failed to get stream URL");
            }

            if (_session != session) return;
            ct.ThrowIfCancellationRequested();

            SetStreamInfo(stream.Codec, stream.Bitrate, stream.Container);

            // ОПРЕДЕЛЯЕМ, является ли это ручным выбором качества
            bool isManualQualityOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);

            long size = stream.Size;
            // Если размера нет в кэше и это не ручной выбор, пробуем узнать HEAD запросом
            if (size <= 0 && !isManualQualityOverride)
            {
                size = await TryGetContentLengthAsync(stream.Url, ct);
            }

            if (_session != session || ct.IsCancellationRequested) return;

            // 3. Create Stream
            if (size > 0)
            {
                // Если это ручной выбор, добавляем к ID параметры формата, 
                // чтобы не испортить основной кэш трека (который обычно BestAvailable)
                string cacheId = isManualQualityOverride
                    ? $"{track.Id}_{stream.Container}_{stream.Bitrate}"
                    : track.Id;

                cacheStream = new MemoryFirstCachingStream(cacheId, stream.Url, size, _httpClient, _cacheManager);

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
                // Играем через кэш
                StartPlayback(new Media(_libVLC, new StreamMediaInput(cacheStream)), cacheStream, track);
                cacheStream = null; // Ownership transferred
            }
            else
            {
                // Играем напрямую по ссылке (Смена формата или ошибка кэша)
                StartPlayback(new Media(_libVLC, stream.Url, FromType.FromLocation), null, track);
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
        // Используем тот же неблокирующий метод
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
        _suppressAutoNext = true; // Чтобы при Stop() не сработал переход на следующий
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
    /// <summary>
    /// Переключает на следующий трек (ручное действие пользователя)
    /// </summary>
    public async Task PlayNextAsync()
    {
        if (_isDisposed || _isNavigating) return;

        if (TryAdvanceQueue(userInitiated: true))
        {
            await PlayCurrentIndexAsync();
        }
        else
        {
            Stop();
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
            await RestartCurrentTrackInternalAsync();
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
            await RestartCurrentTrackInternalAsync();
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

        // Если трек играл достаточно долго, просто перезапускаем его
        if (CurrentPosition.TotalSeconds > 3 && _isPlayerReady)
        {
            Log.Info("[AudioEngine] Position > 3s, restarting track.");
            await PlayCurrentIndexAsync(); // Просто вызываем воспроизведение текущего индекса
            return;
        }

        // Иначе переключаемся на предыдущий
        if (TryRetreatQueue())
        {
            await PlayCurrentIndexAsync();
        }
        else if (_isPlayerReady && _player != null)
        {
            // Если предыдущего нет, перематываем на начало
            _player.Time = 0;
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
        // ИСПРАВЛЕНИЕ: Проверяем наличие ручного выбора формата
        bool hasUserOverride = track.TransientBitrate > 0 || !string.IsNullOrEmpty(track.TransientContainer);

        // 1. Проверяем кэш ТОЛЬКО если нет ручного переопределения
        if (!hasUserOverride && _cacheManager.IsFullyCached(track.Id))
        {
            var meta = _cacheManager.TryGetMetadata(track.Id);
            if (meta != null && meta.ContentLength > 0 && !string.IsNullOrEmpty(meta.Codec))
            {
                Log.Info($"[AudioEngine] Track {track.Id} is fully cached. Using saved format.");
                track.CachedBitrate = meta.Bitrate;
                track.CachedCodec = meta.Codec;
                track.CachedContainer = meta.Container;

                return new StreamDetails(meta.SourceUrl, meta.ContentLength,
                    meta.Bitrate, meta.Codec, meta.Container);
            }
        }

        // 2. Если есть Override или нет кэша - запрашиваем свежую ссылку
        bool needFresh = string.IsNullOrEmpty(track.StreamUrl)
            || string.IsNullOrEmpty(track.CachedCodec)
            || hasUserOverride; // <-- Важно

        if (!needFresh)
            return new(track.StreamUrl, -1, track.CachedBitrate, track.CachedCodec, track.CachedContainer);

        return await WithLock(_apiLock, async () =>
        {
            await ThrottleApiCall(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(RefreshTimeoutS));

            // Тут YoutubeProvider сам разберется с track.TransientContainer внутри
            var result = await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            _lastApiCall = DateTime.UtcNow;

            if (!result.HasValue) return null;

            // В кэш сохраняем только если это стандартное воспроизведение (не override),
            // чтобы не перезаписать метаданные "хорошего" файла временным выбором.
            if (!hasUserOverride)
            {
                _cacheManager.UpdateStreamInfo(track.Id, result.Value.Codec, result.Value.Bitrate, result.Value.Container);
            }

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
        _suppressAutoNext = false; // Возвращаем авто-переход для нормального конца трека
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
        if (_suppressAutoNext)
        {
            Log.Info("[AudioEngine] EndReached ignored (manual navigation)");
            return;
        }

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
                // Небольшая задержка, чтобы все события VLC завершились
                if (_isDisposed || _session != session) return;

                // Используем новый "мозг" для принятия решения
                if (TryAdvanceQueue(userInitiated: false))
                {
                    if (_session == session) // Дополнительная проверка на случай быстрой смены
                    {
                        await PlayCurrentIndexAsync();
                    }
                }
                else
                {
                    // Если очередь закончилась, останавливаем плеер
                    Stop();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioEngine] Error in OnVlcEndReached task: {ex.Message}");
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