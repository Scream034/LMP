// Services/AudioEngine.cs
// Главный аудио движок приложения на базе LibVLCSharp
// Реализует логику воспроизведения, управления ресурсами и синхронизацию настроек

using System.Diagnostics;
using LibVLCSharp.Shared;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.ViewModels;
using ReactiveUI;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Центральный аудио движок приложения.
/// Отвечает за:
/// - Воспроизведение треков (локальных и стриминговых)
/// - Управление очередью и историей
/// - Громкость и усиление (Gain) с плавным применением
/// - Кэширование потоков и управление жизненным циклом файлов
/// - Синхронизацию предпочтений форматов треков
/// </summary>
public class AudioEngine : ViewModelBase, IDisposable
{
    // ЗАВИСИМОСТИ И СЕРВИСЫ

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly StreamCacheManager _cacheManager;
    private readonly HttpClient _streamHttpClient;
    private readonly LibVLC _libVLC;

    // СОСТОЯНИЕ ПЛЕЕРА

    private MediaPlayer? _player;
    private Media? _currentMedia;
    private MemoryFirstCachingStream? _currentStream;

    // ПРИМИТИВЫ СИНХРОНИЗАЦИИ (Thread Safety)

    /// <summary>Блокировка загрузки трека (предотвращает параллельную загрузку)</summary>
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <summary>Блокировка команд управления (Play/Pause/Seek)</summary>
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    /// <summary>Блокировка API запросов (throttling)</summary>
    private readonly SemaphoreSlim _apiLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private int _session; // Счетчик сессий воспроизведения для отмены устаревших задач

    // ВНУТРЕННИЕ ПОЛЯ

    private int _volumePercent;
    private volatile bool _isDisposed;
    private volatile bool _isPlayerReady;
    private volatile bool _isPlayingOrBuffering;

    // Информация о текущем потоке
    private string _activeCodec = "";
    private int _activeBitrate = 0;
    private string _activeContainer = "";
    private volatile bool _streamInfoReady;

    // API Throttling
    private DateTime _lastApiCall = DateTime.MinValue;
    private const int ApiCooldownMs = 200;

    // Очередь и история
    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;

    // Сохранение громкости
    private DateTime _lastVolumeChange = DateTime.MinValue;
    private bool _volumeSavePending;

    // ПУБЛИЧНЫЕ СВОЙСТВА

    /// <summary>Текущий воспроизводимый трек</summary>
    public TrackInfo? CurrentTrack { get; private set; }

    public bool IsPlaying => _player?.IsPlaying ?? false;
    public bool IsPaused => _player?.State == VLCState.Paused;
    public string VlcStateString => _player?.State.ToString() ?? "NULL";

    /// <summary>Текущая позиция воспроизведения</summary>
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

    /// <summary>Общая длительность трека</summary>
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

    public double BufferProgress => _currentStream?.DownloadProgress ?? 0;
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

    // СОБЫТИЯ

    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action? OnStreamInfoReady;
    public event Action<bool, bool>? OnPlaybackStateChanged;

    private TaskCompletionSource<bool>? _playbackStartedTcs;

    // КОНСТРУКТОР И ИНИЦИАЛИЗАЦИЯ

    public AudioEngine(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
        _cacheManager = new StreamCacheManager();

        // Настройка HttpClient с оптимизацией соединений
        _streamHttpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
            EnableMultipleHttp2Connections = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;

        // Загрузка громкости
        float savedVolume = library.Data.Volume;
        _volumePercent = savedVolume <= 1.0f && savedVolume > 0
            ? (int)Math.Round(savedVolume * 100f)
            : (int)Math.Round(savedVolume);
        _volumePercent = Math.Clamp(_volumePercent, 0, 500);

        Log.Info($"[AudioEngine] Loaded Volume: {_volumePercent}% (Raw saved: {savedVolume})");

        // Инициализация LibVLC с параметрами для качественного аудио
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
        Log.Info("[AudioEngine] Initialized with HIGH QUALITY audio settings.");
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
        _player.TimeChanged += OnVlcTimeChanged;
        _isPlayerReady = false;
        ApplyVolumeImmediate();
    }

    // УПРАВЛЕНИЕ ГРОМКОСТЬЮ
    #region VOLUME

    public void SetVolumeInstant(float value)
    {
        int percent = (int)Math.Round(value);
        _volumePercent = Math.Clamp(percent, 0, 500);
        Task.Run(ApplyVolumeImmediate);

        // Отмечаем необходимость сохранения
        _library.Data.Volume = _volumePercent;
        _lastVolumeChange = DateTime.UtcNow;
        _volumeSavePending = true;
    }

    public float GetVolume() => _volumePercent;

    public void SaveVolumeNow()
    {
        if (_volumeSavePending)
        {
            _volumeSavePending = false;
            _library.Save();
            Log.Info("[AudioEngine] Volume saved to disk.");
        }
    }

    public void UpdateAudioSettings()
    {
        Log.Info("[AudioEngine] Updating audio settings (MaxVol/Gain)...");
        SafeInvoke(() => OnMaxVolumeChanged?.Invoke(_library.Data.MaxVolumeLimit));
        Task.Run(ApplyVolumeImmediate);
    }

    private void ApplyVolumeImmediate()
    {
        if (_player == null || _isDisposed) return;
        try
        {
            float dbGain = Math.Clamp(_library.Data.TargetGainDb, -20f, 20f);
            float gainMultiplier = MathF.Pow(10f, dbGain / 20f);
            int finalVolume = (int)Math.Round(_volumePercent * gainMultiplier);
            finalVolume = Math.Clamp(finalVolume, 0, 500);
            _player.Volume = finalVolume;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] ApplyVolume error: {ex.Message}");
        }
    }

    private async Task VolumeSaveLoopAsync()
    {
        while (!_isDisposed)
        {
            await Task.Delay(2000);
            if (_volumeSavePending && (DateTime.UtcNow - _lastVolumeChange).TotalSeconds >= 1.5)
            {
                SaveVolumeNow();
            }
        }
    }
    #endregion

    // ЛОГИКА ВОСПРОИЗВЕДЕНИЯ (CORE)
    #region PLAYBACK LOGIC

    /// <summary>
    /// Переключает качество трека и сохраняет выбор пользователя.
    /// </summary>
    public async Task SwitchQualityAsync(string container, int targetBitrate = 0)
    {
        if (CurrentTrack == null) return;

        Log.Info($"[AudioEngine] Switching quality to {container}/{targetBitrate}kbps...");

        var position = CurrentPosition;
        var trackToPlay = CurrentTrack;

        // 1. Устанавливаем временные параметры
        trackToPlay.TransientContainer = container;
        trackToPlay.TransientBitrate = targetBitrate;

        // 2. Если включено запоминание, сохраняем в библиотеку
        if (_library.Data.RememberTrackFormat)
        {
            trackToPlay.PreferredContainer = container;
            trackToPlay.PreferredBitrate = targetBitrate;

            if (_library.Data.Tracks.TryGetValue(trackToPlay.Id, out var savedTrack))
            {
                savedTrack.PreferredContainer = container;
                savedTrack.PreferredBitrate = targetBitrate;
            }
            else
            {
                _library.Data.Tracks[trackToPlay.Id] = trackToPlay.Clone();
            }
            _library.Save();
        }

        // 3. Сбрасываем URL
        trackToPlay.StreamUrl = string.Empty;

        // 4. Создаём TaskCompletionSource для ожидания Playing
        _playbackStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 5. Запускаем воспроизведение
        await PlayTrackAsync(trackToPlay);

        // 6. Ждём события Playing (с таймаутом)
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _playbackStartedTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("[AudioEngine] Timeout waiting for playback start during quality switch");
        }
        finally
        {
            _playbackStartedTcs = null;
        }

        // 7. Восстанавливаем позицию
        if (position.TotalSeconds > 1)
        {
            // Небольшая задержка чтобы VLC стабилизировался
            await Task.Delay(200);
            await SeekAsync(position);
            Log.Info($"[AudioEngine] Quality switched to {container}/{targetBitrate}kbps, position restored to {position}");
        }
        else
        {
            Log.Info($"[AudioEngine] Quality switched to {container}/{targetBitrate}kbps");
        }
    }

    /// <summary>
    /// Основной метод запуска трека.
    /// </summary>
    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null || _isDisposed) return;
        Log.Info($"[AudioEngine] PlayTrackAsync requested: {track.Title} ({track.Id})");

        // === СИНХРОНИЗАЦИЯ НАСТРОЕК (Быстрая операция в памяти) ===
        if (_library.Data.Tracks.TryGetValue(track.Id, out var savedTrack))
        {
            if (string.IsNullOrEmpty(track.PreferredContainer) && !string.IsNullOrEmpty(savedTrack.PreferredContainer))
            {
                track.PreferredContainer = savedTrack.PreferredContainer;
                track.PreferredBitrate = savedTrack.PreferredBitrate;
            }
        }

        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        var session = Interlocked.Increment(ref _session);
        try { oldCts?.Cancel(); } catch { /* ignore */ }

        // === ОБНОВЛЕНИЕ UI (Должно быть в UI потоке) ===
        ResetStreamInfo();
        IsLoading = true;
        CurrentTrack = track;
        _isPlayerReady = false;
        _isPlayingOrBuffering = true;

        SafeInvoke(() => OnTrackChanged?.Invoke(track));

        // Выносим всю тяжелую работу в фоновый поток
        // Это гарантирует, что UI освобождается мгновенно после установки флага IsLoading.
        // VLC Stop() и сетевые запросы будут выполняться в ThreadPool.
        _ = Task.Run(async () =>
        {
            Log.Debug("[AudioEngine] Waiting for _loadLock...");
            bool lockAcquired = await _loadLock.WaitAsync(500);
            Log.Debug($"[AudioEngine] _loadLock acquired: {lockAcquired}");

            try
            {
                if (lockAcquired)
                {
                    // Внутри этого метода происходит StopPlaybackAsync (Stop VLC) и сетевые запросы
                    await PlayTrackInternalAsync(track, session, _cts.Token);
                }
            }
            finally
            {
                if (lockAcquired) _loadLock.Release();
            }
        });
    }


    /// <summary>
    /// Внутренняя логика загрузки и запуска потока.
    /// </summary>
    private async Task PlayTrackInternalAsync(TrackInfo track, int session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // ★ КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ:
        // Асинхронно останавливаем предыдущее воспроизведение и ЖДЕМ освобождения файла.
        // Это предотвращает ошибку "File being used by another process".
        await StopPlaybackAsync();

        try
        {
            StreamDetails? streamDetails = null;

            // Проверяем, нужно ли получать новую ссылку
            bool needFreshUrl = string.IsNullOrEmpty(track.StreamUrl)
                             || string.IsNullOrEmpty(track.CachedCodec)
                             || track.CachedBitrate <= 0;

            if (needFreshUrl)
            {
                // YoutubeProvider внутри учтет Transient и Preferred настройки
                streamDetails = await GetStreamDetailsAsync(track, ct);
                if (streamDetails == null)
                {
                    throw new Exception("Failed to get stream URL");
                }

                track.CachedCodec = streamDetails.Codec;
                track.CachedBitrate = streamDetails.Bitrate;
                track.CachedContainer = streamDetails.Container;
            }
            else
            {
                streamDetails = new StreamDetails
                {
                    Url = track.StreamUrl,
                    Size = -1,
                    Bitrate = track.CachedBitrate,
                    Codec = track.CachedCodec,
                    Container = track.CachedContainer
                };
                Log.Info($"[AudioEngine] Using cached stream info: {track.CachedCodec}/{track.CachedBitrate}kbps");
            }

            if (_session != session || ct.IsCancellationRequested) return;

            SetStreamInfo(streamDetails.Codec, streamDetails.Bitrate, streamDetails.Container);

            string url = streamDetails.Url;
            long size = streamDetails.Size > 0
                ? streamDetails.Size
                : await TryGetContentLengthAsync(url, ct);

            // Если размер неизвестен, играем напрямую (без кэширования)
            if (size <= 0)
            {
                Log.Info($"[AudioEngine] Playing direct URL (size unknown): {url}");
                var media = new Media(_libVLC, url, FromType.FromLocation);
                StartPlayback(media, null, track, session);
                return;
            }

            Log.Info($"[AudioEngine] Starting MemoryFirst stream. Size: {size / 1024}KB, Format: {streamDetails.Codec}/{streamDetails.Bitrate}kbps");

            // Создаем поток. Теперь файл точно свободен благодаря StopPlaybackAsync.
            var stream = new MemoryFirstCachingStream(
                track.Id,
                url,
                size,
                _streamHttpClient,
                _cacheManager
            );

            // Предбуферизация
            int prebuffer = size > 20 * 1024 * 1024 ? 64 * 1024 : 128 * 1024;
            await stream.PreBufferAsync(prebuffer, ct);

            if (_session != session || ct.IsCancellationRequested)
            {
                stream.Dispose();
                return;
            }

            var streamMedia = new Media(_libVLC, new StreamMediaInput(stream));
            StartPlayback(streamMedia, stream, track, session);
            sw.Stop();
            Log.Info($"[AudioEngine] Track loaded in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            Log.Info("[AudioEngine] Playback cancelled");
            _isPlayingOrBuffering = false;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Error in PlayTrackInternal: {ex.Message}");
            SafeInvoke(() => OnError?.Invoke(ex.Message));
            IsLoading = false;
            _isPlayingOrBuffering = false;
        }
    }

    private void StartPlayback(Media media, MemoryFirstCachingStream? stream, TrackInfo track, int session)
    {
        Log.Info("[AudioEngine] StartPlayback called. Swapping media...");

        // Очищаем предыдущие ресурсы (хотя StopPlaybackAsync уже должен был это сделать,
        // но для надежности при прямом воспроизведении оставим)
        var oldMedia = _currentMedia;
        var oldStream = _currentStream;

        _currentMedia = media;
        _currentStream = stream;

        _ = Task.Run(() =>
        {
            try { oldStream?.Dispose(); } catch { /* ignore */ }
            try { oldMedia?.Dispose(); } catch { /* ignore */ }
        });

        if (_player == null) return;
        _player.Media = media;
        ApplyVolumeImmediate();
        var result = _player.Play();
        Log.Info($"[AudioEngine] _player.Play() result: {result}");
        AddToHistory(track);
    }

    /// <summary>
    /// Асинхронно останавливает воспроизведение и ДОЖИДАЕТСЯ освобождения ресурсов.
    /// Это устраняет Race Condition при смене качества.
    /// </summary>
    private async Task StopPlaybackAsync()
    {
        if (_player == null) return;

        // 1. Быстрая остановка VLC
        try
        {
            if (_player.State != VLCState.Stopped)
            {
                Log.Debug("[AudioEngine] StopPlaybackAsync: Stopping VLC...");
                _player.Stop();
            }
        }
        catch { /* ignore */ }

        // 2. Забираем ссылки и обнуляем поля класса
        var oldStream = _currentStream;
        var oldMedia = _currentMedia;

        _currentStream = null;
        _currentMedia = null;
        _isPlayerReady = false;

        // 3. Ждем очистки ресурсов в фоновом потоке
        if (oldStream != null || oldMedia != null)
        {
            Log.Debug("[AudioEngine] StopPlaybackAsync: Awaiting resource disposal...");
            await Task.Run(() =>
            {
                try { oldStream?.Dispose(); } catch (Exception ex) { Log.Error($"Stream dispose error: {ex.Message}"); }
                try { oldMedia?.Dispose(); } catch { /* ignore */ }
            });
            Log.Debug("[AudioEngine] StopPlaybackAsync: Resources disposed.");
        }
    }

    #endregion

    // УПРАВЛЕНИЕ И СОБЫТИЯ
    #region CONTROLS & EVENTS

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (_isDisposed || _player == null) return;
        Log.Info($"[AudioEngine] SetPlaybackStateAsync: {(shouldPlay ? "PLAY" : "PAUSE")} requested.");

        await _commandLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var currentState = _player.State;
                if (shouldPlay)
                {
                    switch (currentState)
                    {
                        case VLCState.Playing: break;
                        case VLCState.Paused: _player.SetPause(false); break;
                        case VLCState.Stopped:
                        case VLCState.Ended:
                        case VLCState.Error:
                            if (CurrentTrack != null) _ = PlayTrackAsync(CurrentTrack);
                            else _player.Play();
                            break;
                        default: _player.Play(); break;
                    }
                }
                else
                {
                    if (currentState == VLCState.Playing || currentState == VLCState.Buffering || currentState == VLCState.Opening)
                    {
                        _player.Pause();
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] SetState error: {ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_player == null || !_isPlayerReady || _isDisposed) return;
        Log.Info($"[AudioEngine] Seek to {position}");
        await _commandLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                long ms = (long)Math.Clamp(position.TotalMilliseconds, 0, _player.Length);
                _player.Time = ms;
            });
        }
        catch (Exception ex) { Log.Error($"[AudioEngine] Seek error: {ex.Message}"); }
        finally { _commandLock.Release(); }
    }

    public void Stop()
    {
        Log.Info("[AudioEngine] Stop requested.");
        Interlocked.Increment(ref _session);
        _cts?.Cancel();

        // Запускаем остановку. Здесь не ждем (fire-and-forget), т.к. Stop() обычно вызывается UI синхронно,
        // и нам важнее быстрее освободить UI. Гонки не будет, т.к. _session инкрементирован.
        _ = StopPlaybackAsync();

        ResetStreamInfo();
        CurrentTrack = null;
        IsLoading = false;
        _isPlayingOrBuffering = false;
        SafeInvoke(() => OnTrackChanged?.Invoke(null));
        SafeInvoke(() => OnPlaybackStopped?.Invoke());
        NotifyPlaybackStateChanged();
    }

    private void NotifyPlaybackStateChanged()
    {
        bool isPlaying = IsPlaying;
        bool isPaused = IsPaused;
        SafeInvoke(() => OnPlaybackStateChanged?.Invoke(isPlaying, isPaused));
    }

    /// <summary>
    /// Обработчики VLC
    /// </summary>
    private void OnVlcPlaying(object? sender, EventArgs e)
    {
        Log.Info("[AudioEngine] VLC Event: Playing");
        if (_isDisposed) return;

        _isPlayerReady = true;
        IsLoading = false;
        ApplyVolumeImmediate();
        NotifyPlaybackStateChanged();

        // Сигнализируем о готовности
        _playbackStartedTcs?.TrySetResult(true);

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            _isPlayingOrBuffering = false;
            await PrefetchNextInQueueAsync();
        });
    }

    private void OnVlcPaused(object? sender, EventArgs e) { NotifyPlaybackStateChanged(); }

    private void OnVlcStopped(object? sender, EventArgs e)
    {
        _isPlayerReady = false;
        NotifyPlaybackStateChanged();
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        Log.Info("[AudioEngine] VLC Event: EndReached");
        if (_isDisposed) return;
        NotifyPlaybackStateChanged();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            if (!_isDisposed) await PlayNextAsync();
        });
    }

    private void OnVlcError(object? sender, EventArgs e)
    {
        Log.Error("[AudioEngine] VLC Event: ERROR");
        SafeInvoke(() => OnError?.Invoke("VLC playback error"));
        IsLoading = false;
        NotifyPlaybackStateChanged();
    }

    private void OnVlcBuffering(object? sender, MediaPlayerBufferingEventArgs e) { }

    private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_isDisposed || !_isPlayerReady) return;
        SafeInvoke(() => OnPositionChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time)));
    }

    #endregion

    // ИНФОРМАЦИЯ О ПОТОКЕ И ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
    #region STREAM INFO & HELPERS

    private class StreamDetails
    {
        public string Url { get; set; } = "";
        public long Size { get; set; }
        public int Bitrate { get; set; }
        public string Codec { get; set; } = "";
        public string Container { get; set; } = "";
    }

    private void ResetStreamInfo()
    {
        _activeCodec = "";
        _activeBitrate = 0;
        _activeContainer = "";
        _streamInfoReady = false;
    }

    private void SetStreamInfo(string codec, int bitrate, string container)
    {
        _activeCodec = codec;
        _activeBitrate = bitrate;
        _activeContainer = container;
        _streamInfoReady = true;
        Log.Info($"[AudioEngine] Stream info set: {codec}/{bitrate}kbps ({container})");
        SafeInvoke(() => OnStreamInfoReady?.Invoke());
    }

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        if (CurrentTrack?.IsDownloaded == true)
        {
            string ext = Path.GetExtension(CurrentTrack.LocalPath)?.TrimStart('.').ToUpper() ?? "FILE";
            return (ext, 0, true);
        }
        return (_activeCodec, _activeBitrate, _streamInfoReady);
    }

    public long GetDownloadedBytes() => _currentStream != null
        ? (long)(_currentStream.DownloadProgress / 100.0 * _currentStream.Length)
        : 0;

    private async Task<StreamDetails?> GetStreamDetailsAsync(TrackInfo track, CancellationToken ct)
    {
        await _apiLock.WaitAsync(ct);
        try
        {
            if ((DateTime.UtcNow - _lastApiCall).TotalMilliseconds < ApiCooldownMs)
            {
                await Task.Delay(ApiCooldownMs, ct);
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            // Здесь YoutubeProvider автоматически выберет формат на основе 
            // TransientContainer или PreferredContainer, которые мы установили
            var result = await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            _lastApiCall = DateTime.UtcNow;

            if (!result.HasValue) return null;
            return new StreamDetails
            {
                Url = result.Value.Url,
                Size = result.Value.Size,
                Bitrate = result.Value.Bitrate,
                Codec = result.Value.Codec,
                Container = result.Value.Container
            };
        }
        finally { _apiLock.Release(); }
    }

    private async Task<long> TryGetContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _streamHttpClient.SendAsync(req, cts.Token);
            return resp.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    private async Task PrefetchNextInQueueAsync()
    {
        await Task.Delay(1000);
        if (_queue.Count > 0 && !_isPlayingOrBuffering)
        {
            await PrefetchAsync(_queue.Peek());
        }
    }

    public async Task PrefetchAsync(TrackInfo track)
    {
        if (_isPlayingOrBuffering || track.IsDownloaded) return;
        try
        {
            await _apiLock.WaitAsync();
            using var cts = new CancellationTokenSource(5000);
            await _youtube.RefreshStreamUrlAsync(track, cts.Token);
            _lastApiCall = DateTime.UtcNow;
        }
        catch { }
        finally { _apiLock.Release(); }
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

    public void EnqueueRange(IEnumerable<TrackInfo> tracks) { foreach (var t in tracks) Enqueue(t); }
    public void ClearQueue() => _queue.Clear();

    public async Task PlayNextAsync()
    {
        TrackInfo? next = null;
        if (RepeatMode == RepeatMode.RepeatOne) next = CurrentTrack;
        else if (ShuffleEnabled && _queue.Count > 0)
        {
            var list = _queue.ToList();
            int index = Random.Shared.Next(list.Count);
            next = list[index];
            list.RemoveAt(index);
            _queue.Clear();
            foreach (var t in list) _queue.Enqueue(t);
        }
        else if (_queue.TryDequeue(out var queued)) next = queued;

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
        else if (CurrentTrack != null)
        {
            Log.Info("[AudioEngine] No previous track, rewinding to start");
            await SeekAsync(TimeSpan.Zero);
        }
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;
        _history.Add(track);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    private static void SafeInvoke(Action action) { try { action(); } catch { } }

    #endregion

    // ОСВОБОЖДЕНИЕ РЕСУРСОВ

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Log.Info("[AudioEngine] Disposing...");
        SaveVolumeNow();
        _cts?.Cancel();
        try { _currentStream?.Dispose(); } catch { }
        if (_player != null)
        {
            _player.Playing -= OnVlcPlaying;
            _player.Paused -= OnVlcPaused;
            _player.Stopped -= OnVlcStopped;
            _player.EndReached -= OnVlcEndReached;
            _player.EncounteredError -= OnVlcError;
            _player.Buffering -= OnVlcBuffering;
            _player.TimeChanged -= OnVlcTimeChanged;
            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
        }
        try { _libVLC.Dispose(); } catch { }
        try { _loadLock.Dispose(); } catch { }
        try { _commandLock.Dispose(); } catch { }
        try { _apiLock.Dispose(); } catch { }
        try { _streamHttpClient.Dispose(); } catch { }
        try { _cacheManager.Dispose(); } catch { }
        Log.Info("[AudioEngine] Disposed.");
    }
}
