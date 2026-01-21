// AudioEngine.cs
// Главный аудио движок приложения на базе LibVLCSharp
// Управляет воспроизведением, очередью треков, громкостью и кэшированием


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
/// - Громкость и усиление (Gain)
/// - Кэширование потоков
/// - Уведомление UI об изменении состояния
/// </summary>
public class AudioEngine : ViewModelBase, IDisposable
{
    // ЗАВИСИМОСТИ

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly StreamCacheManager _cacheManager;
    private readonly HttpClient _streamHttpClient;
    private readonly LibVLC _libVLC;

    // СОСТОЯНИЕ ПЛЕЕРА

    private MediaPlayer? _player;
    private Media? _currentMedia;
    private MemoryFirstCachingStream? _currentStream;

    // СЕМАФОРЫ ДЛЯ ПОТОКОБЕЗОПАСНОСТИ

    /// <summary>Блокировка загрузки трека (предотвращает параллельную загрузку)</summary>
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <summary>Блокировка команд управления (Play/Pause/Seek)</summary>
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    /// <summary>Блокировка API запросов (throttling)</summary>
    private readonly SemaphoreSlim _apiLock = new(1, 1);

    // ТОКЕНЫ ОТМЕНЫ И СЕССИИ

    private CancellationTokenSource? _cts;
    private int _session;

    // СОСТОЯНИЕ

    private int _volumePercent;
    private volatile bool _isDisposed;
    private volatile bool _isPlayerReady;
    private volatile bool _isPlayingOrBuffering;

    // ИНФОРМАЦИЯ О ТЕКУЩЕМ ПОТОКЕ

    /// <summary>Текущий кодек (Opus/AAC/etc)</summary>
    private string _activeCodec = "";

    /// <summary>Текущий битрейт в kbps</summary>
    private int _activeBitrate = 0;

    /// <summary>Текущий контейнер (webm/mp4)</summary>
    private string _activeContainer = "";

    /// <summary>Флаг готовности информации о потоке</summary>
    private volatile bool _streamInfoReady;

    // API THROTTLING

    private DateTime _lastApiCall = DateTime.MinValue;
    private const int ApiCooldownMs = 200;

    // ОЧЕРЕДЬ И ИСТОРИЯ

    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;

    // СОХРАНЕНИЕ ГРОМКОСТИ

    private DateTime _lastVolumeChange = DateTime.MinValue;
    private bool _volumeSavePending;

    // ПУБЛИЧНЫЕ СВОЙСТВА

    /// <summary>Текущий воспроизводимый трек</summary>
    public TrackInfo? CurrentTrack { get; private set; }

    /// <summary>Воспроизводится ли трек в данный момент</summary>
    public bool IsPlaying => _player?.IsPlaying ?? false;

    /// <summary>Находится ли плеер на паузе</summary>
    public bool IsPaused => _player?.State == VLCState.Paused;

    /// <summary>Строковое представление состояния VLC (для отладки)</summary>
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
            catch
            {
                return TimeSpan.Zero;
            }
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
                if (length > 0)
                    return TimeSpan.FromMilliseconds(length);
                return CurrentTrack?.Duration ?? TimeSpan.Zero;
            }
            catch
            {
                return CurrentTrack?.Duration ?? TimeSpan.Zero;
            }
        }
    }

    /// <summary>Прогресс буферизации (0-100)</summary>
    public double BufferProgress => _currentStream?.DownloadProgress ?? 0;

    /// <summary>Включен ли режим перемешивания</summary>
    public bool ShuffleEnabled { get; set; }

    /// <summary>Режим повтора</summary>
    public RepeatMode RepeatMode { get; set; }

    private bool _isLoading;
    /// <summary>Идет ли загрузка трека</summary>
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

    /// <summary>Изменилось состояние загрузки</summary>
    public event Action<bool>? OnLoadingChanged;

    /// <summary>Сменился текущий трек</summary>
    public event Action<TrackInfo?>? OnTrackChanged;

    /// <summary>Воспроизведение остановлено</summary>
    public event Action? OnPlaybackStopped;

    /// <summary>Произош��а ошибка</summary>
    public event Action<string>? OnError;

    /// <summary>Изменилась позиция воспроизведения</summary>
    public event Action<TimeSpan>? OnPositionChanged;

    /// <summary>Изменился максимальный уровень громкости</summary>
    public event Action<int>? OnMaxVolumeChanged;

    /// <summary>Информация о потоке готова к отображению</summary>
    public event Action? OnStreamInfoReady;

    /// <summary>
    /// Изменилось состояние воспроизведения (Play/Pause/Stop).
    /// Параметры: (isPlaying, isPaused) - состояние на момент события.
    /// </summary>
    public event Action<bool, bool>? OnPlaybackStateChanged;

    // КОНСТРУКТОР

    /// <summary>
    /// Создает новый экземпляр AudioEngine
    /// </summary>
    /// <param name="youtube">Провайдер YouTube</param>
    /// <param name="library">Сервис библиотеки</param>
    public AudioEngine(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
        _cacheManager = new StreamCacheManager();

        // Настройка HTTP клиента для стриминга
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

        // Загрузка настроек из библиотеки
        ShuffleEnabled = library.Data.ShuffleEnabled;
        RepeatMode = library.Data.RepeatMode;

        // Загрузка громкости
        float savedVolume = library.Data.Volume;
        _volumePercent = savedVolume <= 1.0f && savedVolume > 0
            ? (int)Math.Round(savedVolume * 100f)
            : (int)Math.Round(savedVolume);
        _volumePercent = Math.Clamp(_volumePercent, 0, 500);

        Log.Info($"[AudioEngine] Loaded Volume: {_volumePercent}% (Raw saved: {savedVolume})");

        // Инициализация LibVLC
        Core.Initialize();

        _libVLC = new LibVLC(
            // Базовые настройки - только аудио
            "--no-video",
            "--no-embedded-video",
            "--no-spu",
            "--no-osd",
            "--no-stats",

            // Буферизация для плавного воспроизведения
            "--network-caching=2048",
            "--file-caching=1024",
            "--live-caching=1024",

            // Настройки стриминга
            "--http-reconnect",
            "--http-continuous",

            // Высокое качество аудио
            "--audio-resampler=speex",
            "--aout=wasapi",

            // Синхронизация
            "--clock-jitter=0",
            "--clock-synchro=0",

            // Отключаем агрессивную синхронизацию для лучшего качества
            "--avcodec-skiploopfilter=0",
            "--avcodec-skip-frame=0",
            "--avcodec-skip-idct=0"
        );

        InitializePlayer();

        // Запуск фонового сохранения громкости
        _ = VolumeSaveLoopAsync();

        Log.Info("[AudioEngine] Initialized with HIGH QUALITY audio settings.");
    }

    /// <summary>
    /// Инициализирует медиаплеер и подписывается на события
    /// </summary>
    private void InitializePlayer()
    {
        _player = new MediaPlayer(_libVLC);

        // Подписка на события VLC
        _player.Playing += OnVlcPlaying;
        _player.Paused += OnVlcPaused;
        _player.Stopped += OnVlcStopped;
        _player.EndReached += OnVlcEndReached;
        _player.EncounteredError += OnVlcError;
        _player.Buffering += OnVlcBuffering;
        _player.TimeChanged += OnVlcTimeChanged;

        _isPlayerReady = false;

        // Применяем громкость сразу, чтобы не было скачка при старте
        ApplyVolumeImmediate();
    }

    // УПРАВЛЕНИЕ ГРОМКОСТЬЮ

    #region VOLUME

    /// <summary>
    /// Устанавливает громкость мгновенно
    /// </summary>
    /// <param name="value">Значение громкости (0-500)</param>
    public void SetVolumeInstant(float value)
    {
        int percent = (int)Math.Round(value);
        _volumePercent = Math.Clamp(percent, 0, 500);

        // Применяем в фоновом потоке, чтобы не тормозить UI
        Task.Run(ApplyVolumeImmediate);

        _library.Data.Volume = _volumePercent;
        _lastVolumeChange = DateTime.UtcNow;
        _volumeSavePending = true;
    }

    /// <summary>
    /// Получает текущее значение громкости
    /// </summary>
    /// <returns>Громкость в процентах (0-500)</returns>
    public float GetVolume() => _volumePercent;

    /// <summary>
    /// Немедленно сохраняет громкость на диск
    /// </summary>
    public void SaveVolumeNow()
    {
        if (_volumeSavePending)
        {
            _volumeSavePending = false;
            _library.Save();
            Log.Info("[AudioEngine] Volume saved to disk.");
        }
    }

    /// <summary>
    /// Обновляет аудио настройки (вызывается при изменении настроек в UI)
    /// </summary>
    public void UpdateAudioSettings()
    {
        Log.Info("[AudioEngine] Updating audio settings (MaxVol/Gain)...");
        SafeInvoke(() => OnMaxVolumeChanged?.Invoke(_library.Data.MaxVolumeLimit));
        Task.Run(ApplyVolumeImmediate);
    }

    /// <summary>
    /// Применяет громкость к VLC с учетом усиления (Gain)
    /// </summary>
    private void ApplyVolumeImmediate()
    {
        if (_player == null || _isDisposed) return;

        try
        {
            // Расчет: Пользовательский % * Усиление (Gain)
            float dbGain = Math.Clamp(_library.Data.TargetGainDb, -20f, 20f);
            float gainMultiplier = MathF.Pow(10f, dbGain / 20f);

            int finalVolume = (int)Math.Round(_volumePercent * gainMultiplier);
            finalVolume = Math.Clamp(finalVolume, 0, 500);

            _player.Volume = finalVolume;

            Log.Debug($"[AudioEngine] Volume applied - Base: {_volumePercent}%, Gain: {dbGain}dB, Final VLC: {finalVolume}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] ApplyVolume error: {ex.Message}");
        }
    }

    /// <summary>
    /// Фоновый цикл отложенного сохранения громкости
    /// </summary>
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

    // ЛОГИКА ВОСПРОИЗВЕДЕНИЯ

    #region PLAYBACK LOGIC

    /// <summary>
    /// Переключает качество потока на лету
    /// </summary>
    /// <param name="container">Контейнер (webm/mp4)</param>
    /// <param name="targetBitrate">Целевой битрейт (0 = лучший доступный)</param>
    public async Task SwitchQualityAsync(string container, int targetBitrate = 0)
    {
        if (CurrentTrack == null) return;

        Log.Info($"[AudioEngine] Switching quality to {container}/{targetBitrate}kbps...");

        // 1. Запоминаем позицию
        var position = CurrentPosition;

        // 2. Обновляем предпочтение в треке
        CurrentTrack.PreferredContainer = container;
        CurrentTrack.PreferredBitrate = targetBitrate;

        // 3. ВАЖНО: Сбрасываем старый URL
        CurrentTrack.StreamUrl = string.Empty;

        // 4. Сохраняем в библиотеку если включено запоминание
        if (_library.Data.RememberTrackFormat)
        {
            _library.Save();
        }

        // 5. Перезапускаем трек с новым форматом
        await PlayTrackAsync(CurrentTrack);

        // 6. Ждем начала воспроизведения и восстанавливаем позицию
        await Task.Delay(800);

        if (position.TotalSeconds > 1)
        {
            await SeekAsync(position);
        }

        Log.Info($"[AudioEngine] Quality switched to {container}/{targetBitrate}kbps, position restored to {position}");
    }

    /// <summary>
    /// Запускает воспроизведение трека
    /// </summary>
    /// <param name="track">Трек для воспроизведения</param>
    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null || _isDisposed) return;

        Log.Info($"[AudioEngine] PlayTrackAsync requested: {track.Title} ({track.Id})");

        // Отменяем предыдущую загрузку
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        var session = Interlocked.Increment(ref _session);

        try { oldCts?.Cancel(); } catch { /* ignore */ }

        // Сбрасываем информацию о потоке
        ResetStreamInfo();

        IsLoading = true;
        CurrentTrack = track;
        _isPlayerReady = false;
        _isPlayingOrBuffering = true;

        SafeInvoke(() => OnTrackChanged?.Invoke(track));

        Log.Debug("[AudioEngine] Waiting for _loadLock...");
        bool lockAcquired = await _loadLock.WaitAsync(500);
        Log.Debug($"[AudioEngine] _loadLock acquired: {lockAcquired}");

        try
        {
            await PlayTrackInternalAsync(track, session, _cts.Token);
        }
        finally
        {
            if (lockAcquired) _loadLock.Release();
        }
    }

    /// <summary>
    /// Внутренняя логика воспроизведения трека
    /// </summary>
    private async Task PlayTrackInternalAsync(TrackInfo track, int session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        QuickStopPlayback();

        try
        {
            StreamDetails? streamDetails = null;

            // ★ ИСПРАВЛЕНО: Проверяем не только URL, но и наличие информации о формате
            bool needFreshUrl = string.IsNullOrEmpty(track.StreamUrl)
                             || string.IsNullOrEmpty(track.CachedCodec)
                             || track.CachedBitrate <= 0;

            if (needFreshUrl)
            {
                // Получаем URL потока
                streamDetails = await GetStreamDetailsAsync(track, ct);

                if (streamDetails == null)
                {
                    throw new Exception("Failed to get stream URL");
                }

                // Сохраняем информацию о формате в трек для будущего использования
                track.CachedCodec = streamDetails.Codec;
                track.CachedBitrate = streamDetails.Bitrate;
                track.CachedContainer = streamDetails.Container;
            }
            else
            {
                // URL и информация есть - используем их
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

            // Проверка отмены
            if (_session != session || ct.IsCancellationRequested) return;

            // Сохраняем информацию о потоке
            SetStreamInfo(streamDetails.Codec, streamDetails.Bitrate, streamDetails.Container);

            string url = streamDetails.Url;
            long size = streamDetails.Size > 0
                ? streamDetails.Size
                : await TryGetContentLengthAsync(url, ct);

            // Если размер неизвестен или очень маленький - играем напрямую
            if (size <= 0)
            {
                Log.Info($"[AudioEngine] Playing direct URL (size unknown): {url}");
                var media = new Media(_libVLC, url, FromType.FromLocation);
                StartPlayback(media, null, track, session);
                return;
            }

            Log.Info($"[AudioEngine] Starting MemoryFirst stream. Size: {size / 1024}KB, Format: {streamDetails.Codec}/{streamDetails.Bitrate}kbps");

            // Создаем кэширующий поток
            var stream = new MemoryFirstCachingStream(
                track.Id,
                url,
                size,
                _streamHttpClient,
                _cacheManager
            );

            // Предварительная буферизация
            int prebuffer = size > 20 * 1024 * 1024 ? 64 * 1024 : 128 * 1024;
            await stream.PreBufferAsync(prebuffer, ct);

            // Проверка отмены после буферизации
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

    /// <summary>
    /// Запускает воспроизведение подготовленного медиа
    /// </summary>
    private void StartPlayback(Media media, MemoryFirstCachingStream? stream, TrackInfo track, int session)
    {
        Log.Info("[AudioEngine] StartPlayback called. Swapping media...");

        // Сохраняем старые объекты для очистки
        var oldMedia = _currentMedia;
        var oldStream = _currentStream;

        _currentMedia = media;
        _currentStream = stream;

        // Очищаем старые ресурсы в фоне
        _ = Task.Run(() =>
        {
            try { oldStream?.Dispose(); } catch { /* ignore */ }
            try { oldMedia?.Dispose(); } catch { /* ignore */ }
        });

        if (_player == null) return;

        _player.Media = media;

        // Применяем громкость перед стартом
        ApplyVolumeImmediate();

        var result = _player.Play();
        Log.Info($"[AudioEngine] _player.Play() result: {result}");

        AddToHistory(track);
    }

    /// <summary>
    /// Быстрая остановка воспроизведения без очистки состояния
    /// </summary>
    private void QuickStopPlayback()
    {
        if (_player == null) return;

        try
        {
            if (_player.State != VLCState.Stopped)
            {
                Log.Debug("[AudioEngine] QuickStopPlayback: Stopping VLC...");
                _player.Stop();
            }
        }
        catch { /* ignore */ }

        var oldStream = _currentStream;
        _currentStream = null;

        if (oldStream != null)
        {
            _ = Task.Run(() =>
            {
                try { oldStream.Dispose(); } catch { /* ignore */ }
            });
        }

        _isPlayerReady = false;
    }

    #endregion

    // УПРАВЛЕНИЕ И СОБЫТИЯ

    #region CONTROLS & EVENTS

    /// <summary>
    /// Устанавливает состояние воспроизведения (Play/Pause)
    /// </summary>
    /// <param name="shouldPlay">True для воспроизведения, False для паузы</param>
    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (_isDisposed || _player == null) return;

        Log.Info($"[AudioEngine] SetPlaybackStateAsync: {(shouldPlay ? "PLAY" : "PAUSE")} requested.");
        Log.Debug($"[AudioEngine] Pre-Lock State -> VLC: {VlcStateString}, IsPlaying: {IsPlaying}");

        await _commandLock.WaitAsync();

        try
        {
            await Task.Run(() =>
            {
                var currentState = _player.State;
                Log.Debug($"[AudioEngine] Inside Lock. Current VLC State: {currentState}");

                if (shouldPlay)
                {
                    // Хотим воспроизводить
                    switch (currentState)
                    {
                        case VLCState.Playing:
                            Log.Debug("[AudioEngine] Already playing. Doing nothing.");
                            break;

                        case VLCState.Paused:
                            Log.Info("[AudioEngine] State is Paused. Calling SetPause(false)...");
                            _player.SetPause(false);
                            break;

                        case VLCState.Stopped:
                        case VLCState.Ended:
                        case VLCState.Error:
                            Log.Info($"[AudioEngine] State is {currentState}. Needs restart.");
                            if (CurrentTrack != null)
                            {
                                Log.Info("[AudioEngine] Restarting track via PlayTrackAsync...");
                                _ = PlayTrackAsync(CurrentTrack);
                            }
                            else
                            {
                                Log.Info("[AudioEngine] Calling _player.Play() fallback...");
                                _player.Play();
                            }
                            break;

                        default:
                            Log.Info($"[AudioEngine] State is {currentState}. Calling Play() to be safe.");
                            _player.Play();
                            break;
                    }
                }
                else
                {
                    // Хотим паузу
                    if (currentState == VLCState.Playing ||
                        currentState == VLCState.Buffering ||
                        currentState == VLCState.Opening)
                    {
                        Log.Info("[AudioEngine] Calling Pause()...");
                        _player.Pause();
                    }
                    else
                    {
                        Log.Debug($"[AudioEngine] Already not playing ({currentState}). Doing nothing.");
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
            Log.Debug($"[AudioEngine] SetPlaybackStateAsync FINISHED. Post-State: {VlcStateString}");
        }
    }

    /// <summary>
    /// Перемотка к указанной позиции
    /// </summary>
    /// <param name="position">Целевая позиция</param>
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
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Seek error: {ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Полная остановка воспроизведения
    /// </summary>
    public void Stop()
    {
        Log.Info("[AudioEngine] Stop requested.");

        Interlocked.Increment(ref _session);
        _cts?.Cancel();

        QuickStopPlayback();
        ResetStreamInfo();

        CurrentTrack = null;
        IsLoading = false;
        _isPlayingOrBuffering = false;

        SafeInvoke(() => OnTrackChanged?.Invoke(null));
        SafeInvoke(() => OnPlaybackStopped?.Invoke());
        NotifyPlaybackStateChanged();
    }

    /// <summary>
    /// Уведомляет подписчиков об изменении состояния воспроизведения.
    /// Захватывает состояние в момент вызова для избежания race condition.
    /// </summary>
    private void NotifyPlaybackStateChanged()
    {
        // Захватываем состояние СЕЙЧАС, а не когда UI обработает событие
        bool isPlaying = IsPlaying;
        bool isPaused = IsPaused;

        Log.Debug($"[AudioEngine] NotifyPlaybackStateChanged: Play={isPlaying}, Pause={isPaused}");
        SafeInvoke(() => OnPlaybackStateChanged?.Invoke(isPlaying, isPaused));
    }

    // ОБРАБОТЧИКИ СОБЫТИЙ VLC

    private void OnVlcPlaying(object? sender, EventArgs e)
    {
        Log.Info("[AudioEngine] VLC Event: Playing");
        if (_isDisposed) return;

        _isPlayerReady = true;
        IsLoading = false;

        ApplyVolumeImmediate();

        // Уведомляем UI об изменении состояния воспроизведения
        NotifyPlaybackStateChanged();

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            _isPlayingOrBuffering = false;
            await PrefetchNextInQueueAsync();
        });
    }

    private void OnVlcPaused(object? sender, EventArgs e)
    {
        Log.Info("[AudioEngine] VLC Event: Paused");
        NotifyPlaybackStateChanged();
    }

    private void OnVlcStopped(object? sender, EventArgs e)
    {
        Log.Info("[AudioEngine] VLC Event: Stopped");
        _isPlayerReady = false;
        NotifyPlaybackStateChanged();
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        Log.Info("[AudioEngine] VLC Event: EndReached");
        if (_isDisposed) return;

        // Уведомляем что воспроизведение остановлено
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

    private void OnVlcBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        // Не логируем каждое изменение буфера - слишком много спама
    }

    private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_isDisposed || !_isPlayerReady) return;
        SafeInvoke(() => OnPositionChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time)));
    }

    #endregion

    // ИНФОРМАЦИЯ О ПОТОКЕ

    #region STREAM INFO

    /// <summary>
    /// Структура с детальной информацией о потоке
    /// </summary>
    private class StreamDetails
    {
        public string Url { get; set; } = "";
        public long Size { get; set; }
        public int Bitrate { get; set; }
        public string Codec { get; set; } = "";
        public string Container { get; set; } = "";
    }

    /// <summary>
    /// Сбрасывает информацию о текущем потоке
    /// </summary>
    private void ResetStreamInfo()
    {
        _activeCodec = "";
        _activeBitrate = 0;
        _activeContainer = "";
        _streamInfoReady = false;
    }

    /// <summary>
    /// Устанавливает информацию о текущем потоке
    /// </summary>
    private void SetStreamInfo(string codec, int bitrate, string container)
    {
        _activeCodec = codec;
        _activeBitrate = bitrate;
        _activeContainer = container;
        _streamInfoReady = true;

        Log.Info($"[AudioEngine] Stream info set: {codec}/{bitrate}kbps ({container})");

        // Сразу уведомляем UI о готовности информации
        SafeInvoke(() => OnStreamInfoReady?.Invoke());
    }

    /// <summary>
    /// Получает информацию о текущем потоке
    /// </summary>
    /// <returns>Кортеж (Формат, Битрейт, ГотовностьДанных)</returns>
    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        if (CurrentTrack?.IsDownloaded == true)
        {
            string ext = Path.GetExtension(CurrentTrack.LocalPath)?.TrimStart('.').ToUpper() ?? "FILE";
            return (ext, 0, true);
        }

        return (_activeCodec, _activeBitrate, _streamInfoReady);
    }

    /// <summary>
    /// Получает количество загруженных байт
    /// </summary>
    public long GetDownloadedBytes() => _currentStream != null
        ? (long)(_currentStream.DownloadProgress / 100.0 * _currentStream.Length)
        : 0;

    #endregion

    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ

    #region HELPERS

    /// <summary>
    /// Получает детали потока с throttling и кэшированием
    /// </summary>
    private async Task<StreamDetails?> GetStreamDetailsAsync(TrackInfo track, CancellationToken ct)
    {
        await _apiLock.WaitAsync(ct);

        try
        {
            // Throttling API запросов
            if ((DateTime.UtcNow - _lastApiCall).TotalMilliseconds < ApiCooldownMs)
            {
                await Task.Delay(ApiCooldownMs, ct);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

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
        finally
        {
            _apiLock.Release();
        }
    }

    /// <summary>
    /// Пытается получить размер контента по URL
    /// </summary>
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
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Предзагружает следующий трек в очереди
    /// </summary>
    private async Task PrefetchNextInQueueAsync()
    {
        await Task.Delay(1000);

        if (_queue.Count > 0 && !_isPlayingOrBuffering)
        {
            await PrefetchAsync(_queue.Peek());
        }
    }

    /// <summary>
    /// Предзагружает URL потока для трека
    /// </summary>
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
        catch
        {
            // Предзагрузка не критична
        }
        finally
        {
            _apiLock.Release();
        }
    }

    /// <summary>
    /// Добавляет трек в очередь
    /// </summary>
    public void Enqueue(TrackInfo track)
    {
        _queue.Enqueue(track);

        if (!IsPlaying && !IsPaused && !IsLoading)
        {
            _ = PlayTrackAsync(track);
            _queue.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Добавляет несколько треков в очередь
    /// </summary>
    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var t in tracks)
            Enqueue(t);
    }

    /// <summary>
    /// Очищает очередь воспроизведения
    /// </summary>
    public void ClearQueue() => _queue.Clear();

    /// <summary>
    /// Воспроизводит следующий трек
    /// </summary>
    public async Task PlayNextAsync()
    {
        TrackInfo? next = null;

        if (RepeatMode == RepeatMode.RepeatOne)
        {
            next = CurrentTrack;
        }
        else if (ShuffleEnabled && _queue.Count > 0)
        {
            var list = _queue.ToList();
            int index = Random.Shared.Next(list.Count);
            next = list[index];
            list.RemoveAt(index);

            _queue.Clear();
            foreach (var t in list)
                _queue.Enqueue(t);
        }
        else if (_queue.TryDequeue(out var queued))
        {
            next = queued;
        }

        if (next != null)
        {
            await PlayTrackAsync(next);
        }
        else
        {
            Stop();
        }
    }

    /// <summary>
    /// Воспроизводит предыдущий трек или перематывает текущий в начало
    /// </summary>
    public async Task PlayPreviousAsync()
    {
        // Если в начале истории - перематываем текущий трек в начало
        if (_historyIndex > 0)
        {
            _historyIndex--;
            await PlayTrackAsync(_history[_historyIndex]);
        }
        else if (CurrentTrack != null)
        {
            // Перемотка в начало текущего трека
            Log.Info("[AudioEngine] No previous track, rewinding to start");
            await SeekAsync(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Добавляет трек в историю воспроизведения
    /// </summary>
    private void AddToHistory(TrackInfo track)
    {
        if (_history.LastOrDefault()?.Id == track.Id) return;

        _history.Add(track);

        if (_history.Count > 100)
            _history.RemoveAt(0);

        _historyIndex = _history.Count - 1;
    }

    /// <summary>
    /// Безопасно вызывает действие с обработкой исключений
    /// </summary>
    private static void SafeInvoke(Action action)
    {
        try { action(); } catch { /* ignore */ }
    }

    #endregion

    // ОСВОБОЖДЕНИЕ РЕСУРСОВ

    /// <summary>
    /// Освобождает все ресурсы движка
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Log.Info("[AudioEngine] Disposing...");

        SaveVolumeNow();
        _cts?.Cancel();

        try { _currentStream?.Dispose(); } catch { /* ignore */ }

        if (_player != null)
        {
            // Отписка от событий
            _player.Playing -= OnVlcPlaying;
            _player.Paused -= OnVlcPaused;
            _player.Stopped -= OnVlcStopped;
            _player.EndReached -= OnVlcEndReached;
            _player.EncounteredError -= OnVlcError;
            _player.Buffering -= OnVlcBuffering;
            _player.TimeChanged -= OnVlcTimeChanged;

            try { _player.Stop(); } catch { /* ignore */ }
            try { _player.Dispose(); } catch { /* ignore */ }
        }

        try { _libVLC.Dispose(); } catch { /* ignore */ }
        try { _loadLock.Dispose(); } catch { /* ignore */ }
        try { _commandLock.Dispose(); } catch { /* ignore */ }
        try { _apiLock.Dispose(); } catch { /* ignore */ }
        try { _streamHttpClient.Dispose(); } catch { /* ignore */ }
        try { _cacheManager.Dispose(); } catch { /* ignore */ }

        Log.Info("[AudioEngine] Disposed.");
    }
}