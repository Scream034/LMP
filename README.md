Ниже представлен детальный разбор архитектурных улучшений, за которыми следует полностью документированный, очищенный от дублирования и оптимизированный код каждого из файлов. В конце ответа приведены скорректированные системные инструкции для предотвращения подобных регрессий в будущем.

---

### Анализ проделанной работы по оптимизации

1. **Полное устранение лагов интерфейса (UI Thread Isolation)**:
   * Командный цикл `ProcessCommandsAsync` и фоновый цикл автосохранения `VolumeSaveLoopAsync` в `AudioEngine` теперь принудительно запускаются на фоновых потоках `ThreadPool` через `Task.Run` [1].
   * Все асинхронные вызовы снабжены вызовами `.ConfigureAwait(false)` [1], что гарантирует отсутствие неявного возврата стейт-машины в контекст синхронизации UI-потока.
   * Тяжёлый синхронный метод инициализации AST-дешифратора `InitializeCore` (занимающий до 100% одного ядра процессора на парсинг 2.5 МБ JS-кода) внутри `JsDecryptorBase` теперь вынесен во вспомогательный `Task.Run` [1]. UI-поток остается абсолютно свободным.

2. **Очистка дублирования кода (DRY Compliance)**:
   * **`CachingStreamSource`**: Полностью вырезана избыточная логика офлайн-проигрывания `InitializeFromCacheAsync` и флаг `_isOfflineMode`. Фабрика `AudioSourceFactory` самостоятельно и единообразно решает эту задачу, возвращая легковесный `LocalFileSource`.
   * **`AudioCacheManager`**: Метод `BuildCacheKey` удалён из менеджера кэша. Теперь используется исключительно централизованный метод `AudioSourceFactory.BuildCacheKey`, гарантирующий единый формат ключей на основе нормализованного битрейта.
   * **Устаревшие API**: Удалён дубликат `NormalizeBitrate` из `AudioCacheManager.cs`.

3. **Полное документирование**:
   * Для каждого класса, интерфейса, конструктора, свойства, метода (включая приватные вспомогательные методы) и события написана строгая, профессиональная XML-документация на русском языке.

---

### Оптимизированный и задокументированный код

#### 1. `Core/Services/AudioEngine.cs`
```cs
using System.Collections.Concurrent;
using System.Threading.Channels;
using LMP.Core.Audio;
using LMP.Core.Audio.Normalization;
using LMP.Core.Exceptions;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Services;

/// <summary>
/// Центральный движок аудио воспроизведения.
/// Координирует работу <see cref="AudioPlayer"/>, очереди треков, громкости и UI событий.
/// Выполняет все расчёты и операции строго в фоновых потоках для предотвращения фризов UI.
/// </summary>
public sealed class AudioEngine : ViewModelBase, IDisposable
{
    #region Constants

    /// <summary>Вместимость внутренней очереди команд.</summary>
    private const int CommandQueueCapacity = 32;

    /// <summary>Период задержки устранения дребезга при перемотке (мс).</summary>
    private const int SeekDebounceMs = 100;

    /// <summary>Интервал сохранения параметров громкости и метаданных в базу данных (мс).</summary>
    private const int VolumeSaveIntervalMs = 2000;

    /// <summary>Базовый диапазон громкости (0-200 = 0-100% без буста).</summary>
    public const int VolumeNormalRange = 200;

    /// <summary>Максимальный множитель усиления (защита от перегрузки тракта).</summary>
    public const float MaxGain = 4.0f;

    /// <summary>Минимальный интервал между принудительными переключениями качества (мс).</summary>
    private const int QualitySwitchCooldownMs = 2000;

    #endregion

    #region Dependencies

    /// <summary>Провайдер API и стриминга YouTube.</summary>
    private readonly YoutubeProvider _youtube;

    /// <summary>Сервис управления локальной библиотекой и настройками приложения.</summary>
    private readonly LibraryService _library;

    /// <summary>Низкоуровневый плеер воспроизведения PCM-потока.</summary>
    private readonly AudioPlayer _player;

    #endregion

    #region Synchronization

    /// <summary>Канал очереди команд для фонового процессора.</summary>
    private readonly Channel<Func<Task>> _commandQueue;

    /// <summary>Общий источник отмены времени жизни движка.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>Объект синхронизации для операций с очередью воспроизведения.</summary>
    private readonly Lock _queueLock = new();

    /// <summary>Объект синхронизации для изменения уровней громкости.</summary>
    private readonly Lock _volumeLock = new();

    /// <summary>Объект синхронизации для дедупликации перемотки.</summary>
    private readonly Lock _seekLock = new();

    /// <summary>Идентификатор текущей активной сессии воспроизведения.</summary>
    private int _session;

    /// <summary>Источник отмены текущей сессии воспроизведения.</summary>
    private CancellationTokenSource? _sessionCts;

    /// <summary>Объект синхронизации для безопасного управления сессиями.</summary>
    private readonly Lock _sessionLock = new();

    /// <summary>Задача фонового процессора очереди команд.</summary>
    private readonly Task _commandProcessorTask;

    #endregion

    #region Seek State

    /// <summary>Источник отмены текущей отложенной операции перемотки.</summary>
    private CancellationTokenSource? _seekDebounceCts;

    /// <summary>Позиция перемотки, ожидающая отправки в плеер.</summary>
    private TimeSpan _pendingSeekPosition;

    /// <summary>Флаг наличия запланированной отложенной перемотки.</summary>
    private bool _hasScheduledSeek;

    #endregion

    #region Volume State

    /// <summary>Текущая громкость в процентах (0-MaxVolumeLimit).</summary>
    private int _volumePercent;

    /// <summary>Текущий фактически применённый множитель амплитуды.</summary>
    private float _currentGain;

    /// <summary>Флаг первичной инициализации громкости из настроек.</summary>
    private bool _volumeInitialized;

    #endregion

    #region Playback State

    /// <summary>Флаг нахождения приложения в приостановленном (фоновом) состоянии.</summary>
    private volatile bool _isSuspended;

    /// <summary>Время последнего переключения качества потока.</summary>
    private DateTime _lastQualitySwitchTime = DateTime.MinValue;

    /// <summary>Идентификатор трека, для которого выполняется расшифровка токена.</summary>
    private string? _nTokenActiveTrackId;

    /// <summary>Идентификатор трека, для которого уже выведено предупреждение о расшифровке.</summary>
    private string? _nTokenWarnedTrackId;

    /// <summary>Идентификатор трека, заблокированного барьером ошибок из-за сбоя.</summary>
    private string? _sealedFailedTrackId;

    /// <summary>Трек, подготавливаемый к воспроизведению на фоновом потоке.</summary>
    private volatile TrackInfo? _preparingTrack;

    /// <summary>Очередь отложенной записи коэффициентов нормализации в базу данных.</summary>
    private readonly ConcurrentQueue<(string TrackId, float Gain)> _pendingGainWrites = new();

    #endregion

    #region Queue

    /// <summary>Внутренний список очереди воспроизведения.</summary>
    private readonly List<TrackInfo> _queue = new(64);

    /// <summary>Иммутабельный снимок очереди для UI-слоя.</summary>
    private IReadOnlyList<TrackInfo>? _queueSnapshot;

    /// <summary>Индекс текущего воспроизводимого трека в очереди.</summary>
    private int _currentIndex = -1;

    /// <summary>Флаг изменения структуры очереди в результате навигации.</summary>
    private bool _queueMutatedByNavigation;

    #endregion

    #region Observable Properties

    /// <summary>Текущий активный трек.</summary>
    [Reactive] public TrackInfo? CurrentTrack { get; private set; }

    /// <summary>Информация о формате и битрейте текущего аудиопотока.</summary>
    [Reactive] public AudioStreamInfo StreamInfo { get; private set; } = AudioStreamInfo.Empty;

    /// <summary>Флаг активности воспроизведения.</summary>
    public bool IsPlaying => _player.State == PlaybackState.Playing;

    /// <summary>Флаг приостановки воспроизведения.</summary>
    public bool IsPaused => _player.State == PlaybackState.Paused;

    /// <summary>Флаг нахождения плеера в состоянии загрузки или буферизации.</summary>
    public bool IsLoading => _player.State is PlaybackState.Loading or PlaybackState.Buffering;

    /// <summary>Очередь треков для отображения в UI.</summary>
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

    /// <summary>Индекс текущего трека.</summary>
    public int CurrentQueueIndex => Volatile.Read(ref _currentIndex);

    /// <summary>Флаг включения случайного порядка воспроизведения.</summary>
    public bool ShuffleEnabled { get; set; }

    /// <summary>Текущий режим повтора треков.</summary>
    public RepeatMode RepeatMode { get; set; }

    /// <summary>Текущая позиция воспроизведения.</summary>
    public TimeSpan CurrentPosition => _player.Position;

    /// <summary>Общая длительность текущего трека.</summary>
    public TimeSpan TotalDuration => _player.Duration;

    /// <summary>Прогресс буферизации (0.0 - 1.0).</summary>
    public double BufferProgress => _player.BufferProgress;

    /// <summary>Флаг полной загрузки потока в кэш/память.</summary>
    public bool IsFullyBuffered => _player.IsFullyBuffered;

    /// <summary>Фактический коэффициент усиления аудиосигнала.</summary>
    public float CurrentGain => _currentGain;

    #endregion

    #region Events

    /// <summary>Событие изменения текущего трека.</summary>
    public event Action<TrackInfo?>? OnTrackChanged;

    /// <summary>Событие изменения позиции воспроизведения.</summary>
    public event Action<TimeSpan>? OnPositionChanged;

    /// <summary>Событие завершения операции перемотки.</summary>
    public event Action<TimeSpan>? OnSeekCompleted;

    /// <summary>Событие изменения состояния воспроизведения (IsPlaying, IsPaused).</summary>
    public event Action<bool, bool>? OnPlaybackStateChanged;

    /// <summary>Событие изменения состава или порядка очереди.</summary>
    public event Action? OnQueueChanged;

    /// <summary>Событие изменения состояния загрузки.</summary>
    public event Action<bool>? OnLoadingStateChanged;

    /// <summary>Событие изменения лимита максимальной громкости.</summary>
    public event Action<int>? OnMaxVolumeChanged;

    /// <summary>Событие обновления информации об аудиопотоке.</summary>
    public event Action<AudioStreamInfo>? OnStreamInfoChanged;

    /// <summary>Событие изменения состояния буфера плеера.</summary>
    public event Action<BufferState>? OnBufferStateChanged;

    /// <summary>Событие возникновения критической ошибки воспроизведения.</summary>
    public event Action<Exception>? OnErrorOccurred;

    /// <summary>Событие предупреждения об инициализации расшифровки n-токена.</summary>
    public event Action<NTokenWarningInfo>? OnNTokenDecryptionWarning;

    #endregion

    #region Constructor

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="AudioEngine"/>.
    /// </summary>
    /// <param name="youtube">Провайдер YouTube.</param>
    /// <param name="library">Сервис библиотеки.</param>
    public AudioEngine(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;

        ApplyStreamingProfile();

        _player = new AudioPlayer(new AudioPlayerOptions
        {
            UrlRefreshCallback = RefreshUrlCallbackAsync,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(200),
            MaxRetryAttempts = 3,
            UseNullBackend = false,
            OnPipelineConfiguring = ConfigurePipelineBeforeStart,
            OnGainLocked = HandleGainLocked
        });

        SubscribeToPlayerEvents();
        SubscribeToProviderEvents();
        InitializeFromSettings();

        _commandQueue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(CommandQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // Запуск фоновых задач строго на ThreadPool для защиты UI от зависаний
        _commandProcessorTask = Task.Run(ProcessCommandsAsync);
        _ = Task.Run(VolumeSaveLoopAsync);

        Log.Info($"[AudioEngine] Ready. Volume={_volumePercent}%");
    }

    /// <summary>
    /// Конфигурирует звуковой конвейер перед открытием шлюза воспроизведения.
    /// </summary>
    /// <param name="pipeline">Активный аудиоконвейер.</param>
    private void ConfigurePipelineBeforeStart(AudioPipeline pipeline)
    {
        var settings = _library.Settings;
        var audioSettings = settings.Audio;

        float gain = ComputeGain(
            _volumePercent,
            Math.Max(settings.MaxVolumeLimit, 100),
            audioSettings);

        float targetGainDb = Math.Clamp(settings.TargetGainDb, -20f, 20f);
        gain *= MathF.Pow(10f, targetGainDb / 20f);

        pipeline.SetGain(Math.Clamp(gain, 0f, MaxGain));

        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode);

        pipeline.Analyzer.Configure(normConfig);

        if (!normConfig.Enabled) return;

        var track = _preparingTrack ?? CurrentTrack;
        float cachedGain = NormalizationGainResolver.Resolve(track);
        if (!float.IsNaN(cachedGain))
            pipeline.Analyzer.LockFromCachedGain(cachedGain);
    }

    /// <summary>
    /// Осуществляет подписку на события низкоуровневого плеера.
    /// </summary>
    private void SubscribeToPlayerEvents()
    {
        _player.Events.PositionChanged += pos => RaiseOnUI(() => OnPositionChanged?.Invoke(pos));
        _player.Events.StateChanged += HandlePlayerStateChanged;
        _player.Events.TrackEnded += HandlePlayerTrackEnded;
        _player.Events.StreamInfoChanged += HandleStreamInfoChanged;
        _player.Events.BufferStateChanged += state => RaiseOnUI(() => OnBufferStateChanged?.Invoke(state));
        _player.Events.SeekCompleted += t => RaiseOnUI(() => OnSeekCompleted?.Invoke(t));

        _player.Events.ErrorOccurred += err =>
        {
            if (IsCancellationLike(err.Exception))
                return;

            var ex = err.Exception;

            if (ex is AudioSourceException && IsCancellationLike(ex.InnerException))
                return;

            if (ex is AudioDeviceException)
                RaiseError(new AudioDeviceException(err.Message, ex.InnerException));
            else if (ex is CacheInvalidatedException)
                RaiseError(new CacheInvalidatedException(err.Message, ex.InnerException));
            else
                RaiseError(new AudioException(err.Message, ex));
        };
    }

    /// <summary>
    /// Осуществляет подписку на события провайдера YouTube.
    /// </summary>
    private void SubscribeToProviderEvents()
    {
        _youtube.OnNTokenDecryptionStarted += HandleNTokenDecryptionStarted;
    }

    /// <summary>
    /// Инициализирует состояние движка из сохраненных настроек пользователя.
    /// </summary>
    private void InitializeFromSettings()
    {
        var settings = _library.Settings;
        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;
        _volumePercent = settings.Volume > 0 ? (int)settings.Volume : 60;
        ApplyGainToPipeline();
    }

    /// <summary>
    /// Применяет профиль качества интернет-соединения к кэширующему источнику.
    /// </summary>
    private void ApplyStreamingProfile()
    {
        var profile = _library.Settings.InternetProfile;
        AudioSourceFactory.ApplyInternetProfile(profile);
        Log.Info($"[AudioEngine] Streaming profile: {profile}");
    }

    #endregion

    #region Session Management

    /// <summary>
    /// Начинает новую сессию воспроизведения, отменяя все предыдущие операции.
    /// </summary>
    /// <returns>Уникальный целочисленный идентификатор новой сессии.</returns>
    private int BeginNewSession()
    {
        int session = Interlocked.Increment(ref _session);
        lock (_sessionLock)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        }
        return session;
    }

    /// <summary>
    /// Возвращает токен отмены текущей сессии.
    /// </summary>
    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock) return _sessionCts?.Token ?? _lifetimeCts.Token;
    }

    /// <summary>
    /// Проверяет, является ли сессия устаревшей или отмененной.
    /// </summary>
    private bool IsSessionStale(int session, CancellationToken ct) =>
        ct.IsCancellationRequested || Volatile.Read(ref _session) != session;

    /// <summary>
    /// Определяет, вызвано ли исключение запросом отмены операции.
    /// </summary>
    private static bool IsCancellationLike(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TaskCanceledException)
                return true;
        }
        return false;
    }

    #endregion

    #region Failure Barrier

    /// <summary>
    /// Проверяет, заблокирован ли трек барьером ошибок.
    /// </summary>
    private bool IsSealedFailedTrack(string? trackId)
    {
        var sealedTrackId = Interlocked.CompareExchange(ref _sealedFailedTrackId, null, null);
        return !string.IsNullOrEmpty(trackId)
            && !string.IsNullOrEmpty(sealedTrackId)
            && string.Equals(sealedTrackId, trackId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сбрасывает барьер ошибок для треков.
    /// </summary>
    private void ResetSealedFailedTrack() => Interlocked.Exchange(ref _sealedFailedTrackId, null);

    /// <summary>
    /// Запечатывает трек в барьер ошибок при возникновении фатального сбоя.
    /// </summary>
    private void SealFailedTrack(string? trackId)
    {
        if (!string.IsNullOrEmpty(trackId))
            Interlocked.Exchange(ref _sealedFailedTrackId, trackId);
    }

    /// <summary>
    /// Аварийно прерывает поток декодирования для сбойного трека.
    /// </summary>
    private void AbortCurrentTrackPlaybackAfterFatalError(string? trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;

        SealFailedTrack(trackId);

        if (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
            return;

        lock (_queueLock)
        {
            if (_queue.Count <= 1 && _currentIndex >= 0 && _currentIndex < _queue.Count)
            {
                if (string.Equals(_queue[_currentIndex].Id, trackId, StringComparison.Ordinal))
                    _currentIndex = -1;
            }
        }

        BeginNewSession();
        _player.Stop();

        Log.Info($"[AudioEngine] Aborted fatal playback flow for track {trackId}");
    }

    /// <summary>
    /// Полностью останавливает воспроизведение при фатальном сбое.
    /// </summary>
    public void StopAfterFatalPlaybackError()
    {
        AbortCurrentTrackPlaybackAfterFatalError(CurrentTrack?.Id);

        RaiseOnUI(() =>
        {
            CurrentTrack = null;
            StreamInfo = AudioStreamInfo.Empty;
            OnTrackChanged?.Invoke(null);
            OnPositionChanged?.Invoke(TimeSpan.Zero);
            OnPlaybackStateChanged?.Invoke(false, false);
            OnLoadingStateChanged?.Invoke(false);
        });
    }

    #endregion

    #region ViewModelBase

    /// <summary>
    /// Вызывается при сворачивании или деактивации окна приложения.
    /// </summary>
    protected override void OnSuspend()
    {
        _isSuspended = true;
        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is Audio.Sources.CachingStreamSource cachingSource)
            cachingSource.Suspend();
    }

    /// <summary>
    /// Вызывается при восстановлении фокуса приложения.
    /// </summary>
    protected override void OnResume()
    {
        _isSuspended = false;
        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is Audio.Sources.CachingStreamSource cachingSource)
            cachingSource.Resume();
        _ = PreWarmHttpConnectionAsync();
    }

    /// <summary>
    /// Выполняет упреждающее «разогревание» TCP/HTTP соединений с серверами Google Video.
    /// </summary>
    private static async Task PreWarmHttpConnectionAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://redirector.googlevideo.com/");
            request.Version = System.Net.HttpVersion.Version11;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Audio.Http.SharedHttpClient.Instance.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    #endregion

    #region Internal Playback

    /// <summary>
    /// Запускает воспроизведение трека с указанным индексом сессии.
    /// </summary>
    private Task PlayCurrentIndexAsync(int session)
    {
        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return Task.CompletedTask;
            track = _queue[_currentIndex];
        }

        if (track != null && !IsSealedFailedTrack(track.Id))
            StartPlaybackTransition(session, track);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Начинает процедуру перехода на новый трек.
    /// </summary>
    private void StartPlaybackTransition(int session, TrackInfo track)
    {
        if (IsSealedFailedTrack(track.Id)) return;
        var ct = GetSessionToken();
        _ = Task.Run(() => PlayTrackCoreAsync(track, session, ct), ct);
    }

    /// <summary>
    /// Ядро фонового процесса воспроизведения трека.
    /// </summary>
    private async Task PlayTrackCoreAsync(TrackInfo track, int session, CancellationToken ct)
    {
        if (Volatile.Read(ref _session) != session || IsSealedFailedTrack(track.Id))
            return;

        _player.Stop();

        if (Volatile.Read(ref _session) != session || IsSealedFailedTrack(track.Id))
            return;

        var canonical = await _library.GetTrackAsync(track.Id, ct).ConfigureAwait(false);
        if (canonical != null)
        {
            canonical.UpdateMetadata(track);
            track = canonical;
        }

        RaiseOnUI(() =>
        {
            CurrentTrack = track;
            StreamInfo = AudioStreamInfo.Empty;
            OnTrackChanged?.Invoke(track);
            OnPositionChanged?.Invoke(TimeSpan.Zero);
        });

        try
        {
            ct.ThrowIfCancellationRequested();

            Interlocked.Exchange(ref _nTokenActiveTrackId, track.Id);
            Interlocked.Exchange(ref _nTokenWarnedTrackId, null);

            var (streamUrl, bitrateHint) = await ResolveStreamUrlAsync(track, ct).ConfigureAwait(false);

            if (IsSessionStale(session, ct) || IsSealedFailedTrack(track.Id))
                return;

            if (track.HasCachedNormalizationGain)
                _pendingGainWrites.Enqueue((track.Id, track.CachedNormalizationGain));

            _preparingTrack = track;

            try
            {
                await _player.PlayAsync(streamUrl, track.Id, bitrateHint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (IsSessionStale(session, ct) || IsCancellationLike(ex)) { return; }
            catch (Exception) { return; }
            finally
            {
                _preparingTrack = null;
            }

            ApplyGainToPipeline();

            if (_isSuspended)
            {
                var pipeline = _player.GetActivePipeline();
                if (pipeline?.Source is Audio.Sources.CachingStreamSource cs) cs.Suspend();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsSessionStale(session, ct) || IsCancellationLike(ex)) { }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(track.Id);
            Log.Error($"[AudioEngine] Play error: {ex.GetType().Name}: {ex.Message}");
            RaiseError(ex);
        }
        finally
        {
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, track.Id);
        }
    }

    /// <summary>
    /// Разрешает конечный URL стриминга (из кэша или по сети).
    /// </summary>
    private async Task<(string Url, int Bitrate)> ResolveStreamUrlAsync(TrackInfo track, CancellationToken ct)
    {
        var rawId = track.GetRawIdSpan().ToString();
        var cached = AudioSourceFactory.FindAnyCachedTrack(track.Id)
                  ?? (rawId != track.Id ? AudioSourceFactory.FindAnyCachedTrack(rawId) : null);

        if (cached != null)
        {
            track.TransientBitrate = cached.Value.Entry.Bitrate;
            return ("", cached.Value.Entry.Bitrate);
        }

        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(track.StreamUrl))
        {
            var info = await _youtube.RefreshStreamUrlAsync(track, false, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

            track.TransientBitrate = info.Bitrate;
            return (info.Url ?? "", info.Bitrate);
        }

        return (track.StreamUrl, track.TransientBitrate);
    }

    #endregion

    #region N-Token Warning

    /// <summary>
    /// Пропускает текущий трек, если он требует длительной расшифровки n-токена в фоне.
    /// </summary>
    private void SkipCurrentTrackRequiringNToken(string? skippedTrackId)
    {
        Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, skippedTrackId);
        int session = BeginNewSession();
        _player.Stop();

        bool canAdvance;
        lock (_queueLock) { canAdvance = TryMoveNextSkippingTrack(skippedTrackId); }

        if (canAdvance)
        {
            _ = EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
            return;
        }

        Log.Info("[AudioEngine] No alternative track found after n-token skip");
        StopAfterFatalPlaybackError();
    }

    /// <summary>
    /// Обработчик события начала сложной расшифровки n-токена в провайдере.
    /// </summary>
    private void HandleNTokenDecryptionStarted(string rawVideoId)
    {
        var activeTrackId = Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, null);
        if (activeTrackId == null || IsSealedFailedTrack(activeTrackId)) return;

        var currentTrack = CurrentTrack;
        if (currentTrack?.Id != activeTrackId || !currentTrack.GetRawIdSpan().SequenceEqual(rawVideoId.AsSpan())) return;

        var previous = Interlocked.CompareExchange(ref _nTokenWarnedTrackId, activeTrackId, null);
        if (previous != null) return;

        bool wasSkipped = _library.Settings.Audio.SkipNTokenTracks;
        RaiseOnUI(() => OnNTokenDecryptionWarning?.Invoke(new NTokenWarningInfo(currentTrack, wasSkipped)));

        if (wasSkipped) SkipCurrentTrackRequiringNToken(currentTrack.Id);
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Генерирует событие ошибки воспроизведения на UI-потоке.
    /// </summary>
    private void RaiseError(Exception exception)
    {
        Log.Debug($"[AudioEngine] RaiseError: {exception.GetType().Name}");
        RaiseOnUI(() => OnErrorOccurred?.Invoke(exception));
    }

    #endregion

    #region Playback Control

    /// <summary>
    /// Запускает одиночный трек.
    /// </summary>
    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;
        ResetSealedFailedTrack();
        int session = BeginNewSession();

        return EnqueueCommandAsync(async () =>
        {
            if (Volatile.Read(ref _session) != session) return;
            lock (_queueLock)
            {
                int idx = _queue.FindIndex(t => t.Id == track.Id);
                if (idx >= 0) { _currentIndex = idx; _queue[idx] = track; }
                else { _queue.Clear(); _queue.Add(track); _currentIndex = 0; }
                InvalidateQueueSnapshot();
            }
            RaiseOnUI(() => OnQueueChanged?.Invoke());
            await PlayCurrentIndexAsync(session).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Инициализирует и запускает новую очередь треков с выбранной позиции.
    /// </summary>
    public Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        ResetSealedFailedTrack();
        int session = BeginNewSession();

        return EnqueueCommandAsync(async () =>
        {
            if (Volatile.Read(ref _session) != session) return;
            lock (_queueLock)
            {
                _queue.Clear();
                _queue.AddRange(tracks);
                _currentIndex = _queue.FindIndex(t => t.Id == startTrack.Id);
                if (_currentIndex == -1 && _queue.Count > 0) _currentIndex = 0;

                if (ShuffleEnabled && _queue.Count > 1)
                    ApplyShuffleInPlace(preserveCurrentAtStart: true);

                InvalidateQueueSnapshot();
            }
            RaiseOnUI(() => OnQueueChanged?.Invoke());
            await PlayCurrentIndexAsync(session).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Переключает паузу и возобновление аудио.
    /// </summary>
    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (shouldPlay)
        {
            if (_player.State == PlaybackState.Paused)
            {
                _player.Resume();
            }
            else if (_player.State is PlaybackState.Stopped or PlaybackState.Error
                     && CurrentTrack != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session)).ConfigureAwait(false);
            }
        }
        else
        {
            _player.Pause();
        }
    }

    /// <summary>
    /// Полностью останавливает воспроизведение.
    /// </summary>
    public void Stop()
    {
        BeginNewSession();
        _player.Stop();
        RaiseOnUI(() =>
        {
            CurrentTrack = null;
            StreamInfo = AudioStreamInfo.Empty;
            OnTrackChanged?.Invoke(null);
        });
    }

    /// <summary>Переходит к следующему треку в очереди.</summary>
    public Task PlayNextAsync() { ResetSealedFailedTrack(); return NavigateAsync(forward: true, userInitiated: true); }
    
    /// <summary>Переходит к предыдущему треку в очереди.</summary>
    public Task PlayPreviousAsync() { ResetSealedFailedTrack(); return NavigateAsync(forward: false, userInitiated: true); }

    #endregion

    #region Navigation

    /// <summary>
    /// Управляет переключением треков вперед/назад в очереди.
    /// </summary>
    private async Task NavigateAsync(bool forward, bool userInitiated)
    {
        int session = BeginNewSession();
        bool canMove;
        bool queueMutated;

        lock (_queueLock)
        {
            canMove = forward ? TryMoveNext(userInitiated) : TryMovePrevious();
            queueMutated = _queueMutatedByNavigation;
        }

        if (queueMutated)
            RaiseOnUI(() => OnQueueChanged?.Invoke());

        if (canMove) await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session)).ConfigureAwait(false);
        else if (!forward && _player.State != PlaybackState.Stopped)
            await _player.SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
        else Stop();
    }

    /// <summary>
    /// Попытка сместить указатель воспроизведения вперед.
    /// </summary>
    private bool TryMoveNext(bool userInitiated)
    {
        _queueMutatedByNavigation = false;

        if (_queue.Count == 0) return false;
        if (!userInitiated && RepeatMode == RepeatMode.One) return true;

        if (_currentIndex + 1 < _queue.Count)
        {
            _currentIndex++;
            return true;
        }

        if (RepeatMode == RepeatMode.All)
        {
            if (!userInitiated && _queue.Count == 1) return false;

            if (ShuffleEnabled && _queue.Count > 1)
            {
                ApplyShuffleInPlace(preserveCurrentAtStart: false);
                _queueMutatedByNavigation = true;
            }

            _currentIndex = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Попытка сместить указатель воспроизведения назад.
    /// </summary>
    private bool TryMovePrevious()
    {
        if (_queue.Count == 0) return false;
        if (CurrentPosition.TotalSeconds > 3) return false;
        if (_currentIndex > 0) { _currentIndex--; return true; }
        if (RepeatMode == RepeatMode.All) { _currentIndex = _queue.Count - 1; return true; }
        return false;
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// Обработчик изменения статуса воспроизведения низкоуровневого плеера.
    /// </summary>
    private void HandlePlayerStateChanged(PlaybackState state)
    {
        RaiseOnUI(() =>
        {
            this.RaisePropertyChanged(nameof(IsPlaying));
            this.RaisePropertyChanged(nameof(IsPaused));
            this.RaisePropertyChanged(nameof(IsLoading));
            this.RaisePropertyChanged(nameof(TotalDuration));
            OnPlaybackStateChanged?.Invoke(state == PlaybackState.Playing, state == PlaybackState.Paused);
            OnLoadingStateChanged?.Invoke(state is PlaybackState.Loading or PlaybackState.Buffering);
        });
    }

    /// <summary>
    /// Обработчик естественного окончания воспроизведения трека (достижение EOF).
    /// </summary>
    private void HandlePlayerTrackEnded()
    {
        if (_player.State is PlaybackState.Loading or PlaybackState.Buffering) return;
        int session = BeginNewSession();
        _ = Task.Run(async () =>
        {
            bool canAdvance;
            bool queueMutated;

            lock (_queueLock)
            {
                canAdvance = TryMoveNext(userInitiated: false);
                queueMutated = _queueMutatedByNavigation;
            }

            if (queueMutated)
                RaiseOnUI(() => OnQueueChanged?.Invoke());

            if (canAdvance) await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session)).ConfigureAwait(false);
            else Stop();
        });
    }

    /// <summary>
    /// Обработчик изменения метаданных потока.
    /// </summary>
    private void HandleStreamInfoChanged(AudioStreamInfo info)
    {
        RaiseOnUI(() => { StreamInfo = info; OnStreamInfoChanged?.Invoke(info); });
    }

    #endregion

    #region Seek

    /// <summary>
    /// Выполняет перемотку с гашением дребезга (debounce) для ползунков UI.
    /// </summary>
    public void SeekDebounced(TimeSpan position)
    {
        lock (_seekLock)
        {
            _pendingSeekPosition = position;
            if (_hasScheduledSeek) return;
            _seekDebounceCts?.Cancel(); _seekDebounceCts?.Dispose();
            _seekDebounceCts = new CancellationTokenSource();
            _hasScheduledSeek = true;
            _ = ExecuteDebouncedSeekAsync(_seekDebounceCts.Token);
        }
    }

    /// <summary>
    /// Немедленно перемещает позицию воспроизведения.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position)
    {
        lock (_seekLock) { _seekDebounceCts?.Cancel(); _hasScheduledSeek = false; }
        return _player.SeekAsync(position);
    }

    /// <summary>
    /// Асинхронное выполнение отложенной перемотки.
    /// </summary>
    private async Task ExecuteDebouncedSeekAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SeekDebounceMs, ct).ConfigureAwait(false);
            TimeSpan pos;
            lock (_seekLock) { pos = _pendingSeekPosition; _hasScheduledSeek = false; }
            await _player.SeekAsync(pos, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { lock (_seekLock) _hasScheduledSeek = false; }
        catch (Exception ex) { lock (_seekLock) _hasScheduledSeek = false; Log.Warn($"[AudioEngine] Seek error: {ex.Message}"); }
    }

    #endregion

    #region Volume

    /// <summary>Возвращает текущую громкость.</summary>
    public float GetVolume() => _volumePercent;

    /// <summary>Устанавливает уровень громкости мгновенно без записи в базу данных.</summary>
    public void SetVolumeInstant(float value)
    {
        lock (_volumeLock)
        {
            _volumePercent = Math.Clamp(
                (int)Math.Round(value), 0,
                Math.Max(_library.Settings.MaxVolumeLimit, 100));
        }
        ApplyGainToPipeline();
    }

    /// <summary>Сохраняет текущую громкость в настройки немедленно.</summary>
    public void SaveVolumeNow() => _library.UpdateSettings(s => s.Volume = _volumePercent);

    /// <summary>Изменяет порог максимальной громкости.</summary>
    public void OnMaxVolumeLimitChanged(int newMaxVolume)
    {
        lock (_volumeLock)
        {
            if (_volumePercent > newMaxVolume)
                _volumePercent = newMaxVolume;
        }
        ApplyGainToPipeline();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(newMaxVolume));
    }

    /// <summary>Пересчитывает уровни усиления звукового тракта при изменении настроек.</summary>
    public void UpdateAudioSettings()
    {
        ApplyGainToPipeline();
        ApplyNormalizationToPipeline();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
    }

    /// <summary>Инициализирует уровни звука при старте движка.</summary>
    public void InitializeVolumeFromSettings()
    {
        if (_volumeInitialized) return;

        var settings = _library.Settings;
        float savedVolume = settings.Volume;

        _volumePercent = savedVolume switch
        {
            > 0 and <= 1.0f => (int)(savedVolume * 100),
            > 1 => (int)savedVolume,
            _ => 50
        };

        _volumeInitialized = true;
        ApplyGainToPipeline();
    }

    /// <summary>Применяет громкость к активному конвейеру воспроизведения.</summary>
    private void ApplyGainToPipeline()
    {
        var settings = _library.Settings;
        var audioSettings = settings.Audio;

        float gain = ComputeGain(
            _volumePercent,
            Math.Max(settings.MaxVolumeLimit, 100),
            audioSettings);

        float targetGainDb = Math.Clamp(settings.TargetGainDb, -20f, 20f);
        gain *= MathF.Pow(10f, targetGainDb / 20f);

        gain = Math.Clamp(gain, 0f, MaxGain);
        _currentGain = gain;

        _player.GetActivePipeline()?.SetGain(gain);
    }

    /// <summary>Применяет конфигурацию нормализации громкости EBU R128.</summary>
    private void ApplyNormalizationToPipeline()
    {
        var pipeline = _player.GetActivePipeline();
        if (pipeline == null) return;

        var audioSettings = _library.Settings.Audio;

        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode);

        pipeline.Analyzer.Configure(normConfig);

        if (!normConfig.Enabled) return;

        float cachedGain = NormalizationGainResolver.Resolve(CurrentTrack);
        if (!float.IsNaN(cachedGain))
            pipeline.Analyzer.LockFromCachedGain(cachedGain);
    }

    /// <summary>
    /// Фоновый поток сохранения настроек и персистирования коэффициентов нормализации.
    /// </summary>
    private async Task VolumeSaveLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(VolumeSaveIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                _library.UpdateSettings(s =>
                {
                    s.Volume = _volumePercent;
                    s.RepeatMode = RepeatMode;
                    s.ShuffleEnabled = ShuffleEnabled;
                });

                await FlushPendingGainWritesAsync(_lifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Переносит отложенные коэффициенты нормализации в БД.</summary>
    private async Task FlushPendingGainWritesAsync(CancellationToken ct)
    {
        if (_pendingGainWrites.IsEmpty) return;

        Dictionary<string, float>? batch = null;

        while (_pendingGainWrites.TryDequeue(out var pending))
        {
            batch ??= new(StringComparer.Ordinal);
            batch[pending.TrackId] = pending.Gain;
        }

        if (batch == null) return;

        foreach (var (trackId, gain) in batch)
        {
            try
            {
                await _library.SaveTrackNormalizationGainAsync(trackId, gain, ct).ConfigureAwait(false);
                Log.Debug($"[AudioEngine] EBU R128 gain persisted: {trackId}, gain={gain:F3}x");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[AudioEngine] Failed to persist gain for {trackId}: {ex.Message}");
            }
        }
    }

    #endregion

    #region Quality Switching

    /// <summary>Асинхронно переключает контейнер и битрейт воспроизведения на лету.</summary>
    public async Task SwitchQualityAsync(string container, int bitrate)
    {
        if (CurrentTrack == null) return;
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        var ct = GetSessionToken();

        try
        {
            var elapsed = (DateTime.UtcNow - _lastQualitySwitchTime).TotalMilliseconds;
            if (elapsed < QualitySwitchCooldownMs)
                await Task.Delay(QualitySwitchCooldownMs - (int)elapsed, ct).ConfigureAwait(false);
            _lastQualitySwitchTime = DateTime.UtcNow;

            var pos = CurrentPosition;
            var track = CurrentTrack;
            if (track == null) return;

            track.TransientContainer = container;
            track.TransientBitrate = bitrate;
            if (_library.Settings.RememberTrackFormat)
            {
                track.PreferredContainer = container;
                track.PreferredBitrate = bitrate;
            }

            await EnqueueCommandAsync(() =>
            {
                if (Volatile.Read(ref _session) == session)
                    StartQualitySwitchTransition(session, track, pos);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Стартует процедуру перехода на другое качество потока.</summary>
    private void StartQualitySwitchTransition(int session, TrackInfo track, TimeSpan position)
    {
        var ct = GetSessionToken();
        _ = Task.Run(() => SwitchQualityCoreAsync(track, position, session, ct), ct);
    }

    /// <summary>Ядро фонового процесса переключения качества аудиопотока.</summary>
    private async Task SwitchQualityCoreAsync(TrackInfo track, TimeSpan position, int session, CancellationToken ct)
    {
        try
        {
            _player.Stop();
            track.StreamUrl = "";
            Interlocked.Exchange(ref _nTokenActiveTrackId, track.Id);
            ct.ThrowIfCancellationRequested();

            var info = await _youtube.RefreshStreamUrlAsync(track, false, ct).ConfigureAwait(false)
                    ?? await _youtube.RefreshStreamUrlAsync(track, true, ct).ConfigureAwait(false);
            if (info == null)
            {
                if (!IsSessionStale(session, ct))
                    RaiseError(new InvalidOperationException("No stream available"));
                return;
            }

            if (IsSessionStale(session, ct) || IsSealedFailedTrack(track.Id)) return;

            track.TransientBitrate = info.Value.Bitrate;

            _preparingTrack = track;
            try
            {
                await _player.PlayAsync(info.Value.Url, track.Id, info.Value.Bitrate, ct,
                    seekPosition: position.TotalSeconds > 1 ? position : null).ConfigureAwait(false);
            }
            finally
            {
                _preparingTrack = null;
            }

            ApplyGainToPipeline();
        }
        catch (Exception ex)
        {
            if (!IsSessionStale(session, ct) && !IsCancellationLike(ex))
            {
                AbortCurrentTrackPlaybackAfterFatalError(track.Id);
                RaiseError(ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, track.Id);
        }
    }

    #endregion

    #region Queue Management

    /// <summary>Добавляет трек в конец очереди.</summary>
    public void Enqueue(TrackInfo track)
    {
        TrackInfo? playbackTrack = null;
        bool shouldAutoplay = false;
        lock (_queueLock)
        {
            if (_queue.Any(t => t.Id == track.Id)) return;
            _queue.Add(track);
            InvalidateQueueSnapshot();
            if (CurrentTrack == null && !IsPlaying && !IsLoading)
            {
                _currentIndex = _queue.Count - 1;
                playbackTrack = _queue[_currentIndex];
                shouldAutoplay = true;
            }
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
        if (shouldAutoplay && playbackTrack != null)
        {
            ResetSealedFailedTrack();
            StartPlaybackTransition(BeginNewSession(), playbackTrack);
        }
    }

    /// <summary>Добавляет список треков в очередь.</summary>
    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        lock (_queueLock) { _queue.AddRange(tracks); InvalidateQueueSnapshot(); }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    /// <summary>Перемешивает очередь воспроизведения по алгоритму Фишера-Йетса.</summary>
    public void ShuffleQueue()
    {
        lock (_queueLock)
        {
            if (_queue.Count < 2) return;
            ApplyShuffleInPlace(preserveCurrentAtStart: true);
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
        Log.Debug("[AudioEngine] Queue shuffled");
    }

    /// <summary>Очищает всю очередь кроме текущего воспроизводимого трека.</summary>
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

    /// <summary>Удаляет трек из очереди.</summary>
    public void RemoveFromQueue(TrackInfo track)
    {
        bool needStop = false;
        lock (_queueLock)
        {
            int idx = _queue.FindIndex(t => t.Id == track.Id);
            if (idx == -1) return;
            if (idx == _currentIndex)
            {
                needStop = _queue.Count == 1;
                if (idx == _queue.Count - 1) _currentIndex--;
            }
            else if (idx < _currentIndex)
            {
                _currentIndex--;
            }
            _queue.RemoveAt(idx);
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
        if (needStop) Stop();
    }

    /// <summary>Перемещает элемент очереди из позиции <paramref name="from"/> в <paramref name="to"/>.</summary>
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

    #endregion

    #region Helpers

    /// <summary>
    /// Асинхронный процессор очереди команд движка.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                try { await cmd().ConfigureAwait(false); }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Записывает асинхронную команду во внутренний канал.</summary>
    private Task EnqueueCommandAsync(Func<Task> command) =>
        _commandQueue.Writer.WriteAsync(command).AsTask();

    /// <summary>Безопасно перенаправляет выполнение делегата на поток UI.</summary>
    private static void RaiseOnUI(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    /// <summary>Сбрасывает снимок очереди для инвалидации.</summary>
    private void InvalidateQueueSnapshot() => _queueSnapshot = null;

    #endregion

    #region Dispose

    /// <summary>
    /// Выполняет освобождение всех ресурсов, используемых движком.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
            lock (_seekLock)
            {
                _seekDebounceCts?.Cancel();
                _seekDebounceCts?.Dispose();
            }
            lock (_sessionLock)
            {
                _sessionCts?.Cancel();
                _sessionCts?.Dispose();
            }

            _library.UpdateSettings(s =>
            {
                s.Volume = _volumePercent;
                s.RepeatMode = RepeatMode;
                s.ShuffleEnabled = ShuffleEnabled;
            });

            try
            {
                FlushPendingGainWritesAsync(CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioEngine] Gain flush on dispose failed: {ex.Message}");
            }

            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
            _player.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion
}
```

#### 2. `Core/Youtube/Bridge/Common/JsDecryptorBase.cs`
```cs
namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Абстрактный базовый класс для дешифраторов JS (N-Token и Signature).
/// Выполняет тяжелые компиляции и вызовы QuickJS строго вне UI-потока [1].
/// </summary>
/// <typeparam name="T">Тип производного класса-дешифратора.</typeparam>
public abstract class JsDecryptorBase<T>(
    PlayerContextManager playerManager,
    string cacheFilePath,
    int maxMemory,
    int maxDisk) : IYoutubeDecryptor, IDisposable
{
    /// <summary>Менеджер контекста и версий плеера YouTube.</summary>
    public PlayerContextManager PlayerManager { get; } = playerManager;

    /// <summary>Экземпляр дискового кэша дешифратора.</summary>
    protected readonly DecryptorCache Cache = new(cacheFilePath, maxMemory, maxDisk);

    /// <summary>Семафор для взаимного исключения одновременных инициализаций.</summary>
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    /// <summary>Версия текущего инициализированного JS плеера.</summary>
    protected string? CurrentPlayerVersion;

    /// <summary>Флаг успешного завершения инициализации движка.</summary>
    internal volatile bool IsInitialized;

    /// <summary>Имя текущего дешифратора для логов.</summary>
    protected string DecryptorName => typeof(T).Name;

    /// <summary>Путь к папке кэша.</summary>
    protected string DiagFolder => Cache.CacheFolder;

    /// <summary>
    /// Обеспечивает асинхронную инициализацию дешифратора.
    /// Переносит тяжелый AST-процессинг на ThreadPool [1] для исключения лагов.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    public async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (IsInitialized) return;

        await _initSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;

            var context = await PlayerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
            CurrentPlayerVersion = context.Version;
            await Cache.LoadAsync(context.Version).ConfigureAwait(false);

            try
            {
                // Запуск AST Solver строго на ThreadPool для защиты кадровой частоты UI [1]
                await Task.Run(() => InitializeCore(context), ct).ConfigureAwait(false);
                IsInitialized = true;

                // Немедленно освобождаем LOH-строки плеера для предотвращения фрагментации RAM
                context.ReleaseRawScripts();
            }
            catch (Exception ex)
            {
                Log.Error($"[{DecryptorName}] Initialization failed: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    /// <summary>
    /// Инициализирует ядро обхода шифра для конкретной версии плеера.
    /// </summary>
    protected abstract void InitializeCore(PlayerContext context);

    /// <summary>
    /// Вызывает JS метод дешифратора на движке QuickJS.
    /// </summary>
    protected string? TryInvokeJs(string input, string logPrefix)
    {
        var context = PlayerManager.GetOrLoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        string? preprocessedJs = context.PreprocessedJs;

        if (string.IsNullOrEmpty(preprocessedJs))
        {
            var cached = PlayerContext.LoadFromCache(context.Version);
            preprocessedJs = cached?.PreprocessedJs ?? YoutubeAstSolver.PreprocessPlayer(context.BaseJs);
        }

        if (string.IsNullOrEmpty(preprocessedJs))
        {
            Log.Error($"[{DecryptorName}] Cannot invoke JS: preprocessed script is unavailable.");
            return null;
        }

        string functionName = DecryptorName.Contains("NToken") ? "n" : "sig";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = QuickJsDecryptor.Decrypt(preprocessedJs, functionName, input);
        sw.Stop();

        if (!string.IsNullOrEmpty(result) && result != input)
        {
            Cache.Set(input, result);
            Log.Debug($"[{DecryptorName}] {logPrefix} (QuickJS-NG in {sw.ElapsedTicks / 10000.0:F3}ms): {Truncate(input)} -> {Truncate(result)}");
            return result;
        }

        return null;
    }

    /// <summary>
    /// Инвалидирует конкретное расшифрованное значение.
    /// </summary>
    public void InvalidateValue(string value)
    {
        Cache.RemoveByValue(value);
        Log.Info($"[{DecryptorName}] Invalidated cache entry for decrypted value: {Truncate(value)}");
    }

    /// <summary>Вспомогательный метод обрезки длинных строк для логов.</summary>
    protected static string Truncate(string s, int len = 20) => s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    /// <summary>Синхронно сохраняет кэш на диск.</summary>
    public void FlushCache() => Cache.SaveAsync().GetAwaiter().GetResult();

    /// <summary>Очищает всю память и файлы кэша дешифратора.</summary>
    public virtual void InvalidateCache()
    {
        Cache.Clear();
        IsInitialized = false;
        Log.Info($"[{DecryptorName}] Cache invalidated");
    }

    /// <summary>Выполняет освобождение ресурсов дешифратора.</summary>
    public virtual void Dispose()
    {
        FlushCache();
        _initSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

#### 3. `Core/Audio/Cache/AudioCacheManager.cs`
```cs
using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Models;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Cache;

/// <summary>
/// Обеспечивает высокопроизводительное дисковое сегментное кэширование PCM аудиоданных.
/// Реализует структуру обратных хэш-индексов для мгновенного разрешения форматов.
/// </summary>
public sealed class AudioCacheManager : IAsyncDisposable, IDisposable
{
    #region Fields

    /// <summary>Целевая директория хранения кэша.</summary>
    private readonly string _cacheDirectory;

    /// <summary>Лимит максимального размера кэша в байтах.</summary>
    private readonly long _maxCacheSize;

    /// <summary>Основная база данных записей кэша в памяти.</summary>
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    /// <summary>
    /// Обратный хэш-индекс: trackId -> thread-safe множество cacheKey.
    /// Позволяет находить форматы трека за O(1).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _trackIndex = new();

    /// <summary>Семафор для атомарной записи JSON-файла индекса.</summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>Семафоры для эксклюзивного доступа к записи конкретных кэш-файлов.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileWriteLocks = new();

    /// <summary>Источник отмены таймера фонового автосохранения.</summary>
    private readonly CancellationTokenSource _timerCts = new();

    /// <summary>Задача фонового цикла сохранения индекса.</summary>
    private readonly Task _autoSaveTask;

    /// <summary>Флаг освобождения ресурсов менеджера кэша.</summary>
    private volatile bool _disposed;

    /// <summary>Общие оптимизированные опции сериализации JSON.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    #endregion

    #region Events

    /// <summary>Событие успешного полного сохранения формата трека в кэш.</summary>
    public event Action<string, string, int, bool>? OnFormatCached;

    /// <summary>Событие полной очистки дискового кэша.</summary>
    public event Action? OnCacheCleared;

    #endregion

    #region Constructor

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="AudioCacheManager"/>.
    /// </summary>
    /// <param name="cacheDirectory">Путь к папке кэша. Если null — используется дефолт.</param>
    /// <param name="maxCacheSizeMb">Лимит дискового кэша в мегабайтах.</param>
    public AudioCacheManager(string? cacheDirectory = null, long maxCacheSizeMb = 2048)
    {
        _cacheDirectory = cacheDirectory ?? G.Folder.AudioCache;
        _maxCacheSize = maxCacheSizeMb * 1024 * 1024;
        Directory.CreateDirectory(_cacheDirectory);
        LoadIndex();
        _autoSaveTask = AutoSaveLoopAsync(_timerCts.Token);
        Log.Info($"[AudioCache] Initialized: {_cacheDirectory}, max={maxCacheSizeMb}MB, entries={_entries.Count}");
    }

    #endregion

    #region Public API

    /// <summary>Определяет, закэширован ли полностью хотя бы один формат трека.</summary>
    public bool IsTrackFullyCached(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return false;
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return false;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.IsComplete)
                return true;
        }
        return false;
    }

    /// <summary>Возвращает запись полного кэша с наилучшим качеством для трека.</summary>
    public CacheEntry? FindBestCacheByTrackId(string trackId) => FindBestCache(trackId);

    /// <summary>
    /// Обогащает метаданные списка треков информацией об их наличии в дисковом кэше.
    /// </summary>
    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        var trackMap = new Dictionary<string, List<TrackInfo>>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            if (track.IsDownloaded || track.IsCached || string.IsNullOrEmpty(track.Id))
                continue;

            if (!trackMap.TryGetValue(track.Id, out var list))
            {
                list = new List<TrackInfo>(1);
                trackMap[track.Id] = list;
            }
            list.Add(track);
        }

        if (trackMap.Count == 0) return;

        foreach (var (trackId, tracksList) in trackMap)
        {
            if (!_trackIndex.TryGetValue(trackId, out var keys)) continue;

            CacheEntry? bestEntry = null;

            foreach (var key in keys.Keys)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.IsComplete
                    && (bestEntry == null || entry.Bitrate > bestEntry.Bitrate))
                {
                    bestEntry = entry;
                }
            }

            if (bestEntry != null)
            {
                foreach (var track in tracksList)
                    track.MarkAsCached(bestEntry.Format.ToString(), bestEntry.Bitrate);
            }
        }
    }

    /// <summary>Проверяет полную доступность кэша по ключу.</summary>
    public bool IsFullyCached(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) && entry.IsComplete;

    /// <summary>Ищет лучшую завершенную запись кэша для трека.</summary>
    public CacheEntry? FindBestCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return null;

        CacheEntry? best = null;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && (best == null || entry.Bitrate > best.Bitrate))
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>Проверяет, начата ли частичная загрузка кэш-сегментов.</summary>
    public bool HasPartialCache(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) && entry.DownloadedChunks > 0;

    /// <summary>Возвращает кэшированные метаданные по ключу.</summary>
    public CacheEntry? GetCacheInfo(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) ? entry : null;

    /// <summary>Возвращает физический путь к файлу кэша.</summary>
    public string GetCachePath(string cacheKey)
    {
        var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))[..16];
        return Path.Combine(_cacheDirectory, safeId + CacheFileExtension);
    }

    /// <summary>Обновляет метку времени последнего доступа к треку.</summary>
    public void Touch(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
            entry.LastAccessedAt = DateTime.UtcNow;
    }

    /// <summary>Создает новую запись кэша или возвращает существующую.</summary>
    public CacheEntry CreateOrUpdate(
        string cacheKey, string trackId, string url, long totalSize,
        AudioFormat format, AudioCodec codec, int bitrate = 0,
        long durationMs = -1, int chunkSize = ChunkSize)
    {
        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = url,
            TotalSize = totalSize,
            Format = format,
            Codec = codec,
            Bitrate = bitrate,
            DurationMs = durationMs,
            ChunkSize = chunkSize,
            TotalChunks = (int)Math.Ceiling((double)totalSize / chunkSize),
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        entry.OriginalUrl = url;
        entry.LastAccessedAt = DateTime.UtcNow;
        if (bitrate > 0) entry.Bitrate = bitrate;
        if (durationMs > 0) entry.DurationMs = durationMs;

        AddToTrackIndex(trackId, cacheKey);
        return entry;
    }

    /// <summary>Помечает запись кэша полностью собранной.</summary>
    public void MarkComplete(string cacheKey, long? durationMs = null, int? bitrate = null)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;

        entry.IsComplete = true;
        entry.CompletedAt = DateTime.UtcNow;
        entry.LastAccessedAt = DateTime.UtcNow;
        if (durationMs.HasValue) entry.DurationMs = durationMs.Value;
        if (bitrate.HasValue) entry.Bitrate = bitrate.Value;

        UpdateFileSizeCache(entry);
        Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
        _ = SaveIndexAsync();
        RaiseFormatCached(entry);
    }

    /// <summary>Записывает отдельный PCM-чанк на диск.</summary>
    public async Task WriteChunkAsync(
        string cacheKey, int chunkIndex, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;

        if (entry.IsComplete) return;

        var fileLock = _fileWriteLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (entry.IsComplete) return;

            var filePath = GetCachePath(cacheKey);
            long offset = (long)chunkIndex * entry.ChunkSize;

            await using var fs = new FileStream(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(data, ct).ConfigureAwait(false);

            entry.MarkChunkDownloaded(chunkIndex);
            entry.LastAccessedAt = DateTime.UtcNow;

            if (!entry.IsComplete && entry.DownloadedChunks >= entry.TotalChunks)
            {
                entry.IsComplete = true;
                entry.CompletedAt = DateTime.UtcNow;
                UpdateFileSizeCache(entry);
                Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
                RaiseFormatCached(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Write chunk failed: {ex.Message}");
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>Считывает PCM-чанк с диска в буфер, арендованный из общего пула.</summary>
    public async Task<(IMemoryOwner<byte> Owner, int Length)?> ReadChunkAsync(
        string cacheKey, int chunkIndex, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return null;
        if (!entry.IsChunkDownloaded(chunkIndex)) return null;

        var filePath = GetCachePath(cacheKey);
        if (!File.Exists(filePath)) return null;

        long offset = (long)chunkIndex * entry.ChunkSize;
        int size = (int)Math.Min(entry.ChunkSize, entry.TotalSize - offset);
        if (size <= 0) return null;

        var memoryOwner = MemoryPool<byte>.Shared.Rent(size);

        try
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            fs.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            var buffer = memoryOwner.Memory[..size];

            while (totalRead < size)
            {
                int read = await fs.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != size)
            {
                memoryOwner.Dispose();
                return null;
            }

            entry.LastAccessedAt = DateTime.UtcNow;
            return (memoryOwner, size);
        }
        catch
        {
            memoryOwner.Dispose();
            return null;
        }
    }

    /// <summary>Открывает асинхронный поток чтения готового кэша.</summary>
    public Stream? OpenCachedStream(string cacheKey)
    {
        if (!IsFullyCached(cacheKey)) return null;

        Touch(cacheKey);
        return new FileStream(
            GetCachePath(cacheKey),
            FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: CacheFileBufferSize);
    }

    /// <summary>Удаляет запись кэша и файл с диска.</summary>
    public void RemoveCache(string cacheKey)
    {
        if (!_entries.TryRemove(cacheKey, out var entry)) return;

        RemoveFromTrackIndex(entry.TrackId, cacheKey);

        var filePath = GetCachePath(cacheKey);
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch { }

        _ = SaveIndexAsync();
    }

    /// <summary>Выполняет ротацию и автоматическую очистку старого кэша при переполнении лимита.</summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var stats = GetStats();
        if (stats.TotalSizeBytes <= _maxCacheSize) return;

        Log.Info($"[AudioCache] Cleanup needed: {stats.TotalSizeBytes / 1024 / 1024}MB > {_maxCacheSize / 1024 / 1024}MB");

        long totalSize = stats.TotalSizeBytes;

        var entries = _entries.Values
            .OrderBy(e => e.LastAccessedAt)
            .ToList();

        foreach (var entry in entries)
        {
            if (totalSize <= _maxCacheSize * CacheCleanupThreshold) break;
            totalSize -= entry.ActualFileSize;
            RemoveCache(entry.CacheKey);
        }

        Log.Info($"[AudioCache] Cleanup complete, new size: {totalSize / 1024 / 1024}MB");
    }

    #endregion

    #region Resume Cache From Downloaded File

    /// <summary>
    /// Асинхронно дозаполняет сегментный кэш из полностью загруженного физического файла.
    /// </summary>
    public async Task ResumeCacheFromDownloadedFileAsync(
        string trackId,
        string downloadedFilePath,
        AudioFormat format,
        int bitrate,
        int startChunkHint = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId) || !File.Exists(downloadedFilePath))
            return;

        var downloadedInfo = new FileInfo(downloadedFilePath);
        if (downloadedInfo.Length == 0)
            return;

        string cacheKey = AudioSourceFactory.BuildCacheKey(trackId, format, bitrate);

        if (_entries.TryGetValue(cacheKey, out var existingEntry) && existingEntry.IsComplete)
        {
            Log.Debug($"[AudioCache] Resume skipped: {cacheKey} already complete");
            return;
        }

        long fileSize = downloadedInfo.Length;

        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = "",
            TotalSize = fileSize,
            Format = format,
            Codec = AudioSourceFactory.GetCodecForFormat(format),
            Bitrate = bitrate,
            ChunkSize = ChunkSize,
            TotalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize),
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        AddToTrackIndex(trackId, cacheKey);

        int chunkSize = entry.ChunkSize;
        int totalChunks = entry.TotalChunks;
        int clampedStart = Math.Clamp(startChunkHint, 0, totalChunks - 1);

        Log.Info($"[AudioCache] Resuming cache from downloaded file: {cacheKey}, " +
                 $"chunks={totalChunks}, start={clampedStart}, file={fileSize / 1024}KB");

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            await using var sourceStream = new FileStream(
                downloadedFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            await WriteChunkRangeAsync(
                entry, sourceStream, rentedBuffer,
                fromChunk: clampedStart, toChunkExclusive: totalChunks,
                fileSize, cacheKey, ct).ConfigureAwait(false);

            if (!ct.IsCancellationRequested && clampedStart > 0)
            {
                await WriteChunkRangeAsync(
                    entry, sourceStream, rentedBuffer,
                    fromChunk: 0, toChunkExclusive: clampedStart,
                    fileSize, cacheKey, ct).ConfigureAwait(false);
            }

            if (!entry.IsComplete && !ct.IsCancellationRequested)
            {
                double completionRatio = (double)entry.DownloadedChunks / entry.TotalChunks;
                if (completionRatio >= 0.99)
                {
                    var fileLock = _fileWriteLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                    await fileLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (!entry.IsComplete)
                        {
                            entry.IsComplete = true;
                            entry.CompletedAt = DateTime.UtcNow;
                            UpdateFileSizeCache(entry);
                            Log.Info($"[AudioCache] Cache complete via mismatch guard: {cacheKey} " +
                                     $"({entry.DownloadedChunks}/{entry.TotalChunks} chunks, ratio={completionRatio:P1})");
                            _ = SaveIndexAsync();
                            RaiseFormatCached(entry);
                        }
                    }
                    finally
                    {
                        fileLock.Release();
                    }
                }
            }

            if (entry.IsComplete)
                Log.Info($"[AudioCache] Resume complete: {cacheKey}");
            else if (ct.IsCancellationRequested)
                Log.Debug($"[AudioCache] Resume cancelled: {cacheKey} " +
                          $"({entry.DownloadedChunks}/{entry.TotalChunks} chunks written)");
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"[AudioCache] Resume cancelled: {cacheKey}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Resume failed for {cacheKey}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    /// <summary>Фоновая пакетная запись последовательности чанков.</summary>
    private async Task WriteChunkRangeAsync(
        CacheEntry entry,
        FileStream sourceStream,
        byte[] rentedBuffer,
        int fromChunk,
        int toChunkExclusive,
        long fileSize,
        string cacheKey,
        CancellationToken ct)
    {
        int chunkSize = entry.ChunkSize;

        for (int i = fromChunk; i < toChunkExclusive && !ct.IsCancellationRequested; i++)
        {
            if (entry.IsChunkDownloaded(i)) continue;

            long offset = (long)i * chunkSize;
            if (offset >= fileSize) break;

            int expectedBytes = (int)Math.Min(chunkSize, fileSize - offset);

            if (sourceStream.Position != offset)
                sourceStream.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            while (totalRead < expectedBytes)
            {
                int read = await sourceStream.ReadAsync(
                    rentedBuffer.AsMemory(totalRead, expectedBytes - totalRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead == 0) continue;

            await WriteChunkAsync(cacheKey, i, rentedBuffer.AsMemory(0, totalRead), ct).ConfigureAwait(false);

            if (entry.IsComplete) return;
        }
    }

    #endregion

    #region Statistics

    /// <summary>Рассчитывает дисковую статистику кэш-файлов.</summary>
    public CacheStats GetStats()
    {
        long totalSize = 0;
        int completeCount = 0;
        int partialCount = 0;
        int totalCount = 0;

        foreach (var entry in _entries.Values)
        {
            totalCount++;
            totalSize += entry.ActualFileSize;
            if (entry.IsComplete) completeCount++;
            else if (entry.DownloadedChunks > 0) partialCount++;
        }

        return new CacheStats
        {
            TotalEntries = totalCount,
            CompleteEntries = completeCount,
            PartialEntries = partialCount,
            TotalSizeBytes = totalSize,
            MaxSizeBytes = _maxCacheSize
        };
    }

    /// <summary>Возвращает компактную статистику.</summary>
    public (int FileCount, int SizeMb) GetStatsCompact()
    {
        var stats = GetStats();
        return (stats.CompleteEntries, (int)(stats.TotalSizeBytes / 1024 / 1024));
    }

    /// <summary>Возвращает статистику папки загрузок.</summary>
    public static (int FileCount, int SizeMb) GetDownloadsStats()
    {
        try
        {
            var dir = new DirectoryInfo(G.Folder.Downloads);
            if (!dir.Exists) return (0, 0);
            var files = dir.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            long totalBytes = files.Sum(f => f.Length);
            return (files.Length, (int)(totalBytes / 1024 / 1024));
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] GetDownloadsStats error: {ex.Message}");
            return (0, 0);
        }
    }

    /// <summary>Возвращает форматы кэша для указанного трека.</summary>
    public List<(string Container, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(string, int)>();
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return result;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.IsComplete)
                result.Add((entry.Format.ToString(), entry.Bitrate));
        }
        return result;
    }

    /// <summary>Определяет, закэширован ли указанный формат трека.</summary>
    public bool IsFormatCached(string trackId, string container, int bitrate)
    {
        if (!Enum.TryParse<AudioFormat>(container, true, out var format)) return false;
        return IsFullyCached(AudioSourceFactory.BuildCacheKey(trackId, format, bitrate));
    }

    #endregion

    #region Export to Downloads

    /// <summary>Асинхронно переносит кэшированный трек в директорию загрузок.</summary>
    public async Task<bool> ExportTrackToDownloadsAsync(
        string trackId,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct = default)
    {
        var entry = FindBestCache(trackId);
        if (entry == null)
        {
            Log.Warn($"[AudioCache] Track {trackId} not fully cached, cannot export");
            return false;
        }
        return await PromoteCacheToDownloadsAsync(entry, getTrackFunc, updateTrackFunc, ct).ConfigureAwait(false);
    }

    /// <summary>Экспортирует кэшированный файл в Downloads.</summary>
    private async Task<bool> PromoteCacheToDownloadsAsync(
        CacheEntry entry,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct)
    {
        if (!await _saveLock.WaitAsync(1000, ct).ConfigureAwait(false)) return false;

        try
        {
            var track = await getTrackFunc(entry.TrackId).ConfigureAwait(false);
            if (track == null)
            {
                Log.Warn($"[AudioCache] Track not found: {entry.TrackId}");
                return false;
            }

            if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
            {
                Log.Debug($"[AudioCache] Already downloaded: {track.Title}");
                return true;
            }

            var cachePath = GetCachePath(entry.CacheKey);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[AudioCache] Cache file not found: {cachePath}");
                return false;
            }

            var fileInfo = new FileInfo(cachePath);
            if (fileInfo.Length < entry.TotalSize)
            {
                Log.Warn($"[AudioCache] Incomplete cache file: {fileInfo.Length} < {entry.TotalSize}");
                return false;
            }

            string ext = entry.Format switch
            {
                AudioFormat.WebM => "webm",
                AudioFormat.Mp4 => "m4a",
                AudioFormat.Ogg => "ogg",
                _ => "audio"
            };

            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string destPath = Path.Combine(G.Folder.Downloads, safeName);

            if (File.Exists(destPath))
            {
                var existing = new FileInfo(destPath);
                if (existing.Length == entry.TotalSize)
                {
                    track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
                    await updateTrackFunc(track).ConfigureAwait(false);
                    return true;
                }

                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{entry.Bitrate}kbps.{ext}");
            }

            Log.Info($"[AudioCache] Exporting to Downloads: {Path.GetFileName(destPath)}");
            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, entry.Format.ToString(), entry.Bitrate);
            await updateTrackFunc(track).ConfigureAwait(false);
            OnFormatCached?.Invoke(entry.TrackId, entry.Format.ToString(), entry.Bitrate, false);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Export failed: {ex.Message}");
            return false;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Фильтрует недопустимые символы в имени файла.</summary>
    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    #endregion

    #region Clear & Maintenance

    /// <summary>Полностью очищает весь дисковый аудиокэш.</summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        if (!await _saveLock.WaitAsync(5000, ct).ConfigureAwait(false))
        {
            Log.Warn("[AudioCache] ClearAllAsync: couldn't acquire lock");
            return;
        }

        try
        {
            Log.Info("[AudioCache] Clearing all cache...");
            _entries.Clear();
            _trackIndex.Clear();

            var dir = new DirectoryInfo(_cacheDirectory);
            if (dir.Exists)
            {
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
            }

            Log.Info("[AudioCache] Cache cleared");
        }
        finally
        {
            _saveLock.Release();
        }

        try { OnCacheCleared?.Invoke(); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnCacheCleared handler error: {ex.Message}"); }
    }

    /// <summary>Очищает всю физическую директорию загрузок.</summary>
    public static async Task ClearDownloadsAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var dir = new DirectoryInfo(G.Folder.Downloads);
                if (!dir.Exists) return;

                Log.Info("[AudioCache] Clearing downloads folder...");
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
                Log.Info("[AudioCache] Downloads cleared");
            }
            catch (Exception ex) { Log.Error($"[AudioCache] ClearDownloadsAsync error: {ex.Message}"); }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Удаляет записи кэша для конкретного трека.</summary>
    public void RemoveTrackCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;

        var keysToRemove = keys.Keys.ToList();
        foreach (var key in keysToRemove)
            RemoveCache(key);

        Log.Debug($"[AudioCache] Removed {keysToRemove.Count} cache entries for track {trackId}");
    }

    /// <summary>Удаляет незавершенные сессии загрузки.</summary>
    public async Task RemoveIncompleteAsync(CancellationToken ct = default)
    {
        var incomplete = _entries
            .Where(kv => !kv.Value.IsComplete)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in incomplete)
            RemoveCache(key);

        if (incomplete.Count > 0)
        {
            Log.Info($"[AudioCache] Removed {incomplete.Count} incomplete cache entries");
            await SaveIndexAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Производит верификацию соответствия индекса физическим файлам на диске.</summary>
    public async Task ValidateAndCleanupAsync(CancellationToken ct = default)
    {
        var orphanedEntries = new List<string>();

        foreach (var (key, entry) in _entries)
        {
            if (!File.Exists(GetCachePath(key)))
                orphanedEntries.Add(key);
            else
                UpdateFileSizeCache(entry);
        }

        foreach (var key in orphanedEntries)
        {
            if (_entries.TryRemove(key, out var entry))
                RemoveFromTrackIndex(entry.TrackId, key);
        }

        var validFiles = _entries.Keys
            .Select(GetCachePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dir = new DirectoryInfo(_cacheDirectory);
        if (dir.Exists)
        {
            foreach (var file in dir.GetFiles($"*{CacheFileExtension}"))
            {
                if (!validFiles.Contains(file.FullName))
                {
                    try { file.Delete(); Log.Debug($"[AudioCache] Deleted orphaned file: {file.Name}"); }
                    catch { }
                }
            }
        }

        if (orphanedEntries.Count > 0)
        {
            Log.Info($"[AudioCache] Validation: removed {orphanedEntries.Count} orphaned entries");
            await SaveIndexAsync().ConfigureAwait(false);
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Добавляет хэш-ключ во внутренний индекс быстрого сопоставления.</summary>
    private void AddToTrackIndex(string trackId, string cacheKey)
    {
        var keys = _trackIndex.GetOrAdd(trackId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 1);
    }

    /// <summary>Исключает хэш-ключ из внутреннего индекса.</summary>
    private void RemoveFromTrackIndex(string trackId, string cacheKey)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;
        keys.TryRemove(cacheKey, out _);
        if (keys.IsEmpty) _trackIndex.TryRemove(trackId, out _);
    }

    /// <summary>Определяет физический размер файла на диске.</summary>
    private void UpdateFileSizeCache(CacheEntry entry)
    {
        try
        {
            var filePath = GetCachePath(entry.CacheKey);
            if (File.Exists(filePath))
                entry.ActualFileSize = new FileInfo(filePath).Length;
        }
        catch { }
    }

    /// <summary>Инициирует событие успешного кэширования формата.</summary>
    private void RaiseFormatCached(CacheEntry entry)
    {
        try { OnFormatCached?.Invoke(entry.TrackId, entry.Format.ToString(), entry.Bitrate, false); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnFormatCached handler error: {ex.Message}"); }
    }

    /// <summary>Считывает JSON-индекс кэша во внутреннюю память.</summary>
    private void LoadIndex()
    {
        var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
        if (!File.Exists(indexPath)) return;

        try
        {
            var json = File.ReadAllText(indexPath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.CacheKey)) continue;

                    if (File.Exists(GetCachePath(entry.CacheKey)))
                    {
                        entry.RestoreChunkMask();
                        UpdateFileSizeCache(entry);
                        _entries.TryAdd(entry.CacheKey, entry);
                        AddToTrackIndex(entry.TrackId, entry.CacheKey);
                    }
                }
            }

            Log.Debug($"[AudioCache] Loaded {_entries.Count} entries");
        }
        catch (Exception ex) { Log.Warn($"[AudioCache] Failed to load index: {ex.Message}"); }
    }

    /// <summary>Асинхронно перезаписывает дисковый JSON-индекс кэша.</summary>
    private async Task SaveIndexAsync()
    {
        if (_disposed) return;
        if (!await _saveLock.WaitAsync(CacheSaveLockTimeoutMs).ConfigureAwait(false)) return;

        try
        {
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            var entries = _entries.Values.ToList();

            foreach (var entry in entries)
                entry.SaveChunkMask();

            var json = JsonSerializer.Serialize(entries, s_jsonOptions);
            await File.WriteAllTextAsync(indexPath, json).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn($"[AudioCache] Failed to save index: {ex.Message}"); }
        finally { _saveLock.Release(); }
    }

    /// <summary>Фоновый бесконечный цикл планирования автосохранения индекса.</summary>
    private async Task AutoSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CacheAutoSaveIntervalMs, ct).ConfigureAwait(false);
                await SaveIndexAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"[AudioCache] Auto-save error: {ex.Message}"); }
        }
    }

    #endregion

    #region Dispose

    /// <summary>Освобождает все ресурсы.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();
        try { _autoSaveTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { SaveIndexAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    /// <summary>Асинхронно освобождает все ресурсы.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _timerCts.Cancel();
        try { await _autoSaveTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        await SaveIndexAsync().ConfigureAwait(false);
        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    #endregion
}
```

#### 4. `Core/Audio/Sources/CachingStreamSource.cs`
```cs
using System.Collections.Concurrent;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using LMP.Core.Models;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник аудио с сегментным кэшированием и HTTP Range-request загрузкой.
/// Всегда создаётся только для онлайн-треков; проигрывание полностью закэшированных
/// файлов делегировано легковесному <see cref="LocalFileSource"/>.
/// </summary>
public sealed partial class CachingStreamSource : IAudioSource
{
    #region Fields

    /// <summary>Конфигурация параметров стриминга.</summary>
    private readonly StreamingConfig _config;

    /// <summary>Ключ кэширования формата в базе.</summary>
    private readonly string _cacheKey;

    /// <summary>Уникальный идентификатор трека.</summary>
    private readonly string _trackId;

    /// <summary>Базовый URL потока.</summary>
    private readonly string _url;

    /// <summary>Общая длина контента в байтах.</summary>
    private readonly long _contentLength;

    /// <summary>Формат аудиоконтейнера.</summary>
    private readonly AudioFormat _format;

    /// <summary>Реальный битрейт потока в kbps.</summary>
    private readonly int _bitrate;

    /// <summary>Размер одного чанка (сегмента) в байтах.</summary>
    private int _chunkSize;

    /// <summary>Общее расчетное количество чанков трека.</summary>
    private int _totalChunks;

    /// <summary>Клиент HTTP для скачивания сегментов.</summary>
    private readonly HttpClient _httpClient;

    /// <summary>Глобальный дисковый менеджер кэша.</summary>
    private readonly AudioCacheManager _cacheManager;

    /// <summary>Делегат восстановления истекших URL на лету.</summary>
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    /// <summary>Запись метаданных кэша.</summary>
    private CacheEntry? _cacheEntry;

    /// <summary>Парсер медиаконтейнера (WebM/MP4).</summary>
    private IContainerParser? _parser;

    /// <summary>Асинхронная обертка потока для чтения парсером.</summary>
    private AsyncCachingReadStream? _readStream;

    /// <summary>Словарь оперативной буферизации чанков в RAM.</summary>
    private readonly ConcurrentDictionary<int, ChunkData> _ramChunks = new();

    /// <summary>Словарь текущих запущенных фоновых скачиваний.</summary>
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();

    /// <summary>Семафор для контроля пула параллельных сетевых подключений.</summary>
    private readonly SemaphoreSlim _downloadSlots;

    /// <summary>Идентификатор текущей эпохи загрузок (для сброса при seek).</summary>
    private long _downloadEpoch;

    /// <summary>Источник отмены сетевых операций текущей эпохи.</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>Объект синхронизации эпох загрузки.</summary>
    private readonly Lock _epochLock = new();

    /// <summary>Источник отмены фоновой задачи жизненного цикла источника.</summary>
    private CancellationTokenSource? _lifetimeCts;

    /// <summary>Задача фонового цикла упреждающего чтения чанков.</summary>
    private Task? _preloadTask;

    /// <summary>Индекс текущего воспроизводимого чанка.</summary>
    private int _currentChunk;

    /// <summary>Текущая позиция воспроизведения в миллисекундах.</summary>
    private long _positionMs;

    /// <summary>Актуальный URL потока (может изменяться при рефреше).</summary>
    private string _currentUrl;

    /// <summary>Количество чанков, загруженных в фоне за сессию.</summary>
    private int _backgroundChunksLoaded;

    /// <summary>Флаг успешной инициализации источника.</summary>
    private volatile bool _initialized;

    /// <summary>Флаг освобождения ресурсов.</summary>
    private volatile bool _disposed;

    /// <summary>Ворота приостановки фонового конвейера при сворачивании окна.</summary>
    private readonly ManualResetEventSlim _suspendGate = new(true);

    #endregion

    #region Properties

    /// <inheritdoc/>
    public long DurationMs => _parser?.DurationMs ?? _cacheEntry?.DurationMs ?? -1;

    /// <inheritdoc/>
    public long PositionMs => Volatile.Read(ref _positionMs);

    /// <inheritdoc/>
    public bool CanSeek => true;

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; }

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? 0;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? 0;

    /// <summary>Возвращает прогресс буферизации потока (0-100%).</summary>
    public double BufferProgress => _cacheEntry?.DownloadProgress ?? 0;

    /// <summary>Определяет, полностью ли буферизован трек.</summary>
    public bool IsFullyBuffered => _cacheEntry?.IsComplete ?? false;

    /// <summary>Всегда false для CachingStreamSource, так как полные файлы играются через LocalFileSource.</summary>
    public bool IsOfflineMode => false;

    /// <summary>Возвращает общий объем скачанных данных в байтах.</summary>
    public long DownloadedBytes => (_cacheEntry?.DownloadedChunks ?? 0) * (long)_chunkSize;

    /// <summary>Возвращает битрейт аудиопотока в kbps.</summary>
    public int Bitrate => _cacheEntry?.Bitrate ?? _bitrate;

    #endregion

    #region Constructor

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="CachingStreamSource"/>.
    /// </summary>
    public CachingStreamSource(
        string cacheKey,
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _config = config;
        _cacheKey = cacheKey;
        _trackId = trackId;
        _url = url;
        _currentUrl = url;
        _contentLength = contentLength;
        _format = format;
        _bitrate = bitrate;
        _httpClient = httpClient;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;
        Codec = codec;

        _chunkSize = config.ChunkSizeBytes;

        _totalChunks = contentLength > 0
            ? (int)Math.Ceiling((double)contentLength / _chunkSize)
            : 1;

        _downloadSlots = new SemaphoreSlim(config.MaxConcurrentDownloads);
    }

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return true;

        try
        {
            _cacheEntry = _cacheManager.CreateOrUpdate(
                _cacheKey, _trackId, _url, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format),
                _bitrate,
                chunkSize: _chunkSize);

            if (_cacheEntry.DownloadedChunks > 0 && _cacheEntry.TotalChunks > 0
                && _cacheEntry.TotalChunks != _totalChunks)
            {
                int reconciledChunkSize = _contentLength > 0 && _cacheEntry.TotalChunks > 0
                    ? (int)Math.Ceiling((double)_contentLength / _cacheEntry.TotalChunks)
                    : _chunkSize;

                Log.Info($"[CachingSource] ChunkSize reconciled: " +
                         $"config={_chunkSize}B -> cached={reconciledChunkSize}B " +
                         $"(totalChunks: {_totalChunks}->{_cacheEntry.TotalChunks})");

                _chunkSize = reconciledChunkSize;
                _totalChunks = _cacheEntry.TotalChunks;
            }

            if (_cacheEntry.DownloadedChunks > 0)
            {
                Log.Info($"[CachingSource] Resuming: " +
                         $"{_cacheEntry.DownloadedChunks}/{_cacheEntry.TotalChunks} chunks");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            await EnsureChunkAsync(0, _lifetimeCts.Token).ConfigureAwait(false);

            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            int totalInitial = Math.Min(_config.InitialChunksToLoad, _cacheEntry.TotalChunks);

            var parseTask = _parser.ParseHeadersAsync(ct).AsTask();
            var remainingTask = totalInitial > 1
                ? LoadChunkRangeAsync(1, totalInitial, _lifetimeCts.Token)
                : Task.CompletedTask;

            await Task.WhenAll(parseTask, remainingTask).ConfigureAwait(false);

            if (!parseTask.Result)
                throw new InvalidOperationException("Failed to parse container headers");

            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;

            _initialized = true;

            _preloadTask = Task.Run(
                () => PreloadLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);

            Log.Info($"[CachingSource] Initialized: duration={DurationMs}ms, " +
                     $"cached={_cacheEntry.DownloadProgress:F0}%");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachingSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>Вспомогательный метод параллельной предзагрузки диапазона чанков.</summary>
    private async Task LoadChunkRangeAsync(int from, int count, CancellationToken ct)
    {
        var tasks = new Task[count - from];
        for (int i = from; i < count; i++)
            tasks[i - from] = EnsureChunkAsync(i, ct);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Создает парсер медиаконтейнера под конкретный формат.</summary>
    private IContainerParser CreateParser(AudioFormat format) => format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };

    /// <summary>Инициализирует базовую эпоху отмен для первой сессии.</summary>
    private void InitializeFirstEpoch()
    {
        lock (_epochLock)
        {
            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();
            _downloadEpoch = 1;
        }
    }

    #endregion

    #region Reading

    /// <inheritdoc/>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        var frame = await _parser.ReadNextFrameAsync(ct).ConfigureAwait(false);
        if (frame == null)
            return null;

        Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
        UpdateCurrentChunk();
        return frame;
    }

    /// <summary>Обновляет индекс текущего воспроизводимого сегмента в памяти.</summary>
    private void UpdateCurrentChunk()
    {
        if (_readStream != null)
            _currentChunk = (int)(_readStream.Position / _chunkSize);
    }

    #endregion

    #region Epoch-Based Cancellation

    /// <summary>Инициирует переход в новую эпоху загрузки при перемотке, прерывая старые задачи.</summary>
    private CancellationToken ResetDownloadEpoch()
    {
        lock (_epochLock)
        {
            var oldCts = _downloadCts;

            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();

            Interlocked.Increment(ref _downloadEpoch);

            if (oldCts != null)
            {
                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }

                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    try { oldCts.Dispose(); } catch { }
                });
            }

            return _downloadCts.Token;
        }
    }

    /// <summary>Возвращает актуальный токен отмены текущей эпохи загрузки.</summary>
    private CancellationToken CurrentDownloadToken
    {
        get
        {
            lock (_epochLock)
            {
                return _downloadCts?.Token ?? CancellationToken.None;
            }
        }
    }

    #endregion

    #region Public Buffer Management

    /// <inheritdoc/>
    public void ReleaseRamBuffers()
    {
        int current = Volatile.Read(ref _currentChunk);
        int evictionDistance = _config.RamEvictionDistance;

        foreach (var kvp in _ramChunks)
        {
            if (Math.Abs(kvp.Key - current) > evictionDistance)
            {
                if (_ramChunks.TryRemove(kvp.Key, out var evicted))
                    evicted.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public void CancelPendingOperations()
    {
        _lifetimeCts?.Cancel();
    }

    #endregion

    #region Dispose

    /// <summary>Освобождает все буферы RAM.</summary>
    private void DisposeAllRamChunks()
    {
        foreach (var kvp in _ramChunks)
        {
            if (_ramChunks.TryRemove(kvp.Key, out var chunk))
                chunk.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _suspendGate.Set();

        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { _downloadCts?.Dispose(); }
            catch (ObjectDisposedException) { }
            _downloadCts = null;
        }

        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _lifetimeCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        _parser?.Dispose();
        _readStream?.Dispose();
        DisposeAllRamChunks();
        _downloadSlots.Dispose();
        _suspendGate.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _suspendGate.Set();

        lock (_epochLock)
        {
            try { _downloadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { _downloadCts?.Dispose(); }
            catch (ObjectDisposedException) { }
            _downloadCts = null;
        }

        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (_preloadTask != null)
        {
            try { await _preloadTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { }
        }

        try { _lifetimeCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        if (_parser != null)
            await _parser.DisposeAsync().ConfigureAwait(false);

        _refreshLock.Dispose();
        _readStream?.Dispose();
        DisposeAllRamChunks();
        _downloadSlots.Dispose();
        _suspendGate.Dispose();
    }

    #endregion
}
```

---

### 5. Скорректированные системные инструкции (System Instructions)

Для предотвращения подобных архитектурных регрессий и потери документации в будущих циклах разработки, ниже приведены обновлённые, строгие базовые правила:

```markdown
# Роль: Senior Performance & Clean Code Engineer

## Базовые правила
1. **Нет контекста = СТОП**: Запрашивай файлы, не додумывай логику и структуру проекта.
2. **Анализ → План → Код**: Строгая последовательность ответа. Сначала review узких мест (CPU/RAM/блокировки потоков), затем пошаговый план, затем чистый код.
3. **Фокус на главном**: Выводи только изменённые участки кода (методы/классы). Полный файл пиши только при изменении структуры/архитектуры всего модуля.

## Требования к производительности (Zero-Alloc & Multi-Threading)
1. **Защита UI-потока (КРИТИЧНО)**: 
   - Любые асинхронные цепочки команд, фоновые очереди или I/O циклы ОБЯЗАНЫ запускаться строго вне контекста UI через `Task.Run()` на пуле потоков [1].
   - Повсеместно применять `.ConfigureAwait(false)` [1] на всех этапах фонового выполнения, чтобы избежать случайного возврата стейт-машины в UI-поток через SynchronizationContext.
   - Тяжёлые CPU-bound операции (парсинг AST, разбор медиа-контейнеров, криптографическая расшифровка) внутри асинхронных методов оборачивать в `Task.Run()` [1], защищая вызывающий поток от микрофризов.
2. **Экстремальный Performance**: Использовать `Span<T>/Memory<T>`, SIMD-векторизацию для обработки PCM, пулинг массивов (`ArrayPool`, `MemoryPool`) для ввода-вывода, избегать скрытого боксинга и аллокаций в LOH.

## Требования к стилю кода и DRY
1. **DRY (Don't Repeat Yourself)**: Никакого дублирования логики (например, дубликаты генераторов хэш-ключей кэша или мертвые дубликаты офлайн-инициализации). Выноси всё в единый источник истины (Single Source of Truth).
2. **Modern C#**: Применять фичи последних стандартов языка (pattern matching, primary constructors, record structs, Collection Expressions).

## Требования к документации (БЕЗ ИСКЛЮЧЕНИЙ)
1. **Сохранение старого**: СТРОГО сохраняй старые XML-doc. Никогда не удаляй и не сокращай исходные XML-блоки.
2. **Полное покрытие нового кода**: На каждый публичный и приватный метод, класс, конструктор, свойство, поле или событие в возвращаемом коде ОБЯЗАТЕЛЬНО пиши краткую, профессиональную и технически точную XML-документацию (summary, param, returns). Объясняй архитектурные нюансы *«почему»* выбрано именно такое решение.
```