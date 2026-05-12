using System.Threading.Channels;
using LMP.Core.Audio;
using LMP.Core.Exceptions;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using LMP.Core.Youtube.Exceptions;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Services;

/// <summary>
/// Центральный движок аудио воспроизведения.
/// Координирует AudioPlayer, очередь треков, громкость и UI события.
/// </summary>
/// <remarks>
/// <para><b>Обработка ошибок (SOLID — Single Responsibility):</b></para>
/// <para>AudioEngine НЕ принимает решения о показе диалогов. Он только:</para>
/// <list type="bullet">
///   <item>Ловит исключения из AudioPlayer и YoutubeProvider</item>
///   <item>Генерирует событие <see cref="OnErrorOccurred"/> с типизированным исключением</item>
///   <item>Логирует ошибки</item>
/// </list>
/// <para>Решение о реакции принимает <see cref="PlaybackErrorOrchestrator"/>.</para>
///
/// <para><b>Session-aware cancellation:</b></para>
/// <para>Каждая новая операция воспроизведения вызывает <see cref="BeginNewSession"/>,
/// что отменяет <see cref="_sessionCts"/>. Все сетевые вызовы (YouTube API, n-token
/// decryption, stream resolution) привязаны к этому токену и мгновенно прерываются
/// при переключении трека пользователем.</para>
///
/// <para><b>Command queue vs playback transition:</b></para>
/// <para>Command queue сериализует только изменение состояния очереди (O(1) операции).
/// Сетевой resolve и запуск pipeline выполняются вне очереди через
/// <see cref="StartPlaybackTransition"/>, что гарантирует мгновенную отзывчивость
/// управления вне зависимости от состояния сети.</para>
/// </remarks>
public sealed class AudioEngine : ViewModelBase, IDisposable
{
    #region Constants

    private const int CommandQueueCapacity = 32;
    private const int SeekDebounceMs = 100;
    private const int VolumeSaveIntervalMs = 2000;
    private const int SmoothVolumeUpdateIntervalMs = 16;

    /// <summary>
    /// Базовый диапазон громкости (0-200 = 0-100% без boost).
    /// </summary>
    public const int VolumeNormalRange = 200;

    /// <summary>
    /// Максимальный gain (аппаратное ограничение для защиты).
    /// </summary>
    public const float MaxGain = 4.0f;

    /// <summary>
    /// Минимальный интервал между переключениями качества (мс).
    /// Предотвращает rate limiting YouTube при быстрых переключениях.
    /// </summary>
    private const int QualitySwitchCooldownMs = 2000;

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
    private readonly Lock _volumeLock = new();
    private readonly Lock _seekLock = new();

    private int _session;

    /// <summary>
    /// CancellationTokenSource текущей пользовательской сессии воспроизведения.
    /// Отменяется при каждом <see cref="BeginNewSession"/>, мгновенно прерывая
    /// все сетевые операции (YouTube API, n-token decryption, pipeline creation).
    /// </summary>
    private CancellationTokenSource? _sessionCts;
    private readonly Lock _sessionLock = new();

    #endregion

    #region Seek State

    private CancellationTokenSource? _seekDebounceCts;
    private TimeSpan _pendingSeekPosition;
    private bool _hasScheduledSeek;

    #endregion

    #region Volume State

    private int _volumePercent;
    private float _currentGain;
    private bool _volumeInitialized;
    private CancellationTokenSource? _smoothVolumeCts;

    #endregion

    #region Playback State

    private volatile bool _isSuspended;

    /// <summary>
    /// Время последнего переключения качества.
    /// </summary>
    private DateTime _lastQualitySwitchTime = DateTime.MinValue;

    /// <summary>
    /// Идентификатор сессии, для которой было отправлено предупреждение о n-token.
    /// Используется для фильтрации ложных уведомлений от отменённых сессий.
    /// </summary>
    private int _activeNTokenWarningSession;

    /// <summary>
    /// Последняя сессия, для которой уже было показано предупреждение.
    /// Предотвращает дублирование предупреждений при нескольких событиях подряд.
    /// </summary>
    private int _lastNTokenWarningSession;

    #endregion

    #region Queue

    private readonly List<TrackInfo> _queue = new(64);
    private IReadOnlyList<TrackInfo>? _queueSnapshot;
    private int _currentIndex = -1;

    #endregion

    #region Observable Properties

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public AudioStreamInfo StreamInfo { get; private set; } = AudioStreamInfo.Empty;

    public bool IsPlaying => _player.State == PlaybackState.Playing;
    public bool IsPaused => _player.State == PlaybackState.Paused;
    public bool IsLoading => _player.State is PlaybackState.Loading or PlaybackState.Buffering;

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

    public TimeSpan CurrentPosition => _player.Position;
    public TimeSpan TotalDuration => _player.Duration;
    public double BufferProgress => _player.BufferProgress;
    public bool IsFullyBuffered => _player.IsFullyBuffered;

    /// <summary>
    /// Текущий фактически применённый gain после кривой громкости,
    /// boost и пользовательской dB-коррекции.
    /// Используется UI для отображения реального уровня усиления.
    /// </summary>
    public float CurrentGain => _currentGain;

    #endregion

    #region Events

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<TimeSpan>? OnSeekCompleted;
    public event Action<bool, bool>? OnPlaybackStateChanged;
    public event Action? OnQueueChanged;
    public event Action<bool>? OnLoadingStateChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action<AudioStreamInfo>? OnStreamInfoChanged;
    public event Action<BufferState>? OnBufferStateChanged;

    /// <summary>
    /// Событие ошибки воспроизведения.
    /// </summary>
    public event Action<Exception>? OnErrorOccurred;

    /// <summary>
    /// Событие предупреждения: текущий трек требует сложной расшифровки n-токена.
    /// Передаёт контекст трека и флаг авто-пропуска.
    /// Публикуется не более одного раза на сессию воспроизведения.
    /// </summary>
    public event Action<NTokenWarningInfo>? OnNTokenDecryptionWarning;

    /// <summary>
    /// Контекст предупреждения о сложной расшифровке n-токена.
    /// </summary>
    /// <param name="Track">Трек, для которого потребовалась расшифровка.</param>
    /// <param name="WasSkipped">
    /// Был ли трек автоматически пропущен из-за настройки
    /// <see cref="AudioSettings.SkipNTokenTracks"/>.
    /// </param>
    public readonly record struct NTokenWarningInfo(TrackInfo? Track, bool WasSkipped);

    #endregion

    #region Constructor

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
            UseNullBackend = false
        });

        SubscribeToPlayerEvents();
        SubscribeToProviderEvents();
        InitializeFromSettings();

        _commandQueue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(CommandQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _ = ProcessCommandsAsync();
        _ = VolumeSaveLoopAsync();

        Log.Info($"[AudioEngine] Ready. Volume={_volumePercent}%");
    }

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
            var ex = err.Exception;
            if (ex is AudioDeviceException)
                RaiseError(new AudioDeviceException(err.Message, ex.InnerException));
            else if (ex is CacheInvalidatedException)
                RaiseError(new CacheInvalidatedException(err.Message, ex.InnerException));
            else
                RaiseError(new AudioException(err.Message, ex));
        };
    }

    /// <summary>
    /// Подписывает движок на события YoutubeProvider, которые должны быть
    /// проброшены в UI-слой без прямой привязки к конкретной реализации уведомлений.
    /// </summary>
    private void SubscribeToProviderEvents()
    {
        _youtube.OnNTokenDecryptionStarted += HandleNTokenDecryptionStarted;
    }

    private void InitializeFromSettings()
    {
        var settings = _library.Settings;

        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;

        _volumePercent = settings.Volume > 0
            ? Math.Clamp((int)settings.Volume, 0, settings.MaxVolumeLimit)
            : 60;

        ApplyVolume(instant: true);
    }

    private void ApplyStreamingProfile()
    {
        var profile = _library.Settings.InternetProfile;
        AudioSourceFactory.ApplyInternetProfile(profile);
        Log.Info($"[AudioEngine] Streaming profile: {profile}");
    }

    #endregion

    #region Session Management

    /// <summary>
    /// Начинает новую пользовательскую сессию воспроизведения.
    /// Все незавершённые сетевые операции предыдущей сессии (YouTube manifest,
    /// n-token decryption, HLS fallback, pipeline creation) отменяются немедленно.
    /// </summary>
    /// <returns>Новый идентификатор сессии.</returns>
    private int BeginNewSession()
    {
        int session = Interlocked.Increment(ref _session);

        lock (_sessionLock)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        }

        Volatile.Write(ref _activeNTokenWarningSession, 0);
        return session;
    }

    /// <summary>
    /// Возвращает токен текущей пользовательской сессии.
    /// Linked с lifetime — отменяется и при Dispose, и при смене трека.
    /// </summary>
    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock)
        {
            return _sessionCts?.Token ?? _lifetimeCts.Token;
        }
    }

    #endregion

    #region ViewModelBase

    /// <summary>
    /// Окно свёрнуто — приостанавливаем фоновую загрузку.
    /// НЕ приостанавливаем воспроизведение!
    /// </summary>
    protected override void OnSuspend()
    {
        _isSuspended = true;

        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is Audio.Sources.CachingStreamSource cachingSource)
            cachingSource.Suspend();

        Log.Debug("[AudioEngine] Suspended (background downloads paused)");
    }

    /// <summary>
    /// Окно развёрнуто — возобновляем фоновую загрузку.
    /// </summary>
    protected override void OnResume()
    {
        _isSuspended = false;

        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is Audio.Sources.CachingStreamSource cachingSource)
            cachingSource.Resume();

        // ═══ Pre-warm HTTP connections после длительной паузы ═══
        // После паузы >30s TCP соединения могут протухнуть.
        // Отправляем легковесный HEAD запрос чтобы обновить connection pool.
        _ = PreWarmHttpConnectionAsync();

        Log.Debug("[AudioEngine] Resumed (background downloads active)");
    }

    /// <summary>
    /// Отправляет легковесный запрос для обновления HTTP connection pool.
    /// Fire-and-forget — не блокирует Resume.
    /// </summary>
    private static async Task PreWarmHttpConnectionAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head,
                "https://redirector.googlevideo.com/");
            request.Version = System.Net.HttpVersion.Version11;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Audio.Http.SharedHttpClient.Instance
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch
        {
            // Не критично — просто прогрев connection pool
        }
    }

    #endregion

    #region Internal Playback

    /// <summary>
    /// Запускает воспроизведение трека по текущему индексу очереди.
    /// Намеренно синхронный и быстрый: только фиксирует трек и запускает
    /// cancellable transition. Command queue не блокируется сетевыми операциями.
    /// </summary>
    private Task PlayCurrentIndexAsync(int session)
    {
        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count)
                return Task.CompletedTask;

            track = _queue[_currentIndex];
        }

        if (track != null)
            StartPlaybackTransition(session, track);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Запускает асинхронный переход к выбранному треку вне command queue.
    /// Это сохраняет мгновенную отзывчивость управления даже при долгих
    /// сетевых операциях и расшифровке YouTube.
    /// </summary>
    private void StartPlaybackTransition(int session, TrackInfo track)
    {
        var ct = GetSessionToken();
        _ = PlayTrackCoreAsync(track, session, ct);
    }

    /// <summary>
    /// Выполняет реальный запуск воспроизведения для уже выбранного трека.
    /// </summary>
    /// <remarks>
    /// <para>Все сетевые операции (YouTube manifest, n-token decryption, HLS fallback)
    /// привязаны к <paramref name="ct"/> текущей пользовательской сессии.
    /// При переключении трека пользователем <see cref="BeginNewSession"/> отменяет
    /// этот токен, что немедленно прерывает все незавершённые операции.</para>
    ///
    /// <para>Счётчик <see cref="_activeNTokenWarningSession"/> позволяет фильтровать
    /// ложные предупреждения о n-token от уже отменённых сессий.</para>
    /// </remarks>
    private async Task PlayTrackCoreAsync(TrackInfo track, int session, CancellationToken ct)
    {
        if (Volatile.Read(ref _session) != session)
            return;

        _player.Stop();

        if (Volatile.Read(ref _session) != session)
            return;

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

            Volatile.Write(ref _activeNTokenWarningSession, session);

            var (streamUrl, bitrateHint) = await ResolveStreamUrlAsync(track, ct);

            if (Volatile.Read(ref _session) != session)
                return;

            ct.ThrowIfCancellationRequested();

            await _player.PlayAsync(streamUrl, track.Id, bitrateHint, ct);

            if (_isSuspended)
            {
                var pipeline = _player.GetActivePipeline();
                if (pipeline?.Source is Audio.Sources.CachingStreamSource cachingSource)
                    cachingSource.Suspend();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || Volatile.Read(ref _session) != session)
        {
            Log.Debug($"[AudioEngine] Playback cancelled for track {track.Id}, session={session}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Play error: {ex.GetType().Name}: {ex.Message}");
            RaiseError(ex);
        }
        finally
        {
            if (Volatile.Read(ref _activeNTokenWarningSession) == session)
                Volatile.Write(ref _activeNTokenWarningSession, 0);
        }
    }

    /// <summary>
    /// Резолвит URL стрима для трека.
    ///
    /// <para><b>Возвращает реальный битрейт</b> (например, 134 kbps), не нормализованный.
    /// Нормализация применяется только внутри <see cref="AudioSourceFactory.BuildCacheKey"/>.</para>
    ///
    /// <para><b>Отмена привязана к сессии</b>: при смене трека пользователем
    /// <paramref name="ct"/> отменяется, что немедленно прерывает YouTube API запросы.</para>
    /// </summary>
    /// <returns>
    /// Кортеж (Url, Bitrate) где Bitrate — реальный битрейт из YouTube API
    /// или из CacheEntry.
    /// </returns>
    /// <exception cref="BotDetectionException">При rate limiting.</exception>
    /// <exception cref="StreamUnavailableException">При недоступности стрима.</exception>
    /// <exception cref="LoginRequiredException">При требовании авторизации.</exception>
    private async Task<(string Url, int Bitrate)> ResolveStreamUrlAsync(TrackInfo track, CancellationToken ct)
    {
        string? streamUrl = track.StreamUrl;
        int bitrateHint = track.TransientBitrate;

        // ═══ ПРИОРИТЕТ 1: ПОЛНЫЙ КЭШ ═══
        var cached = AudioSourceFactory.FindAnyCachedTrack(track.Id);
        if (cached != null)
        {
            var cachedBitrate = cached.Value.Entry.Bitrate;
            track.TransientBitrate = cachedBitrate;

            Log.Debug($"[AudioEngine] Using cache: {cached.Value.Entry.Format}/{cachedBitrate}kbps");
            return ("", cachedBitrate);
        }

        ct.ThrowIfCancellationRequested();

        // ═══ ПРИОРИТЕТ 2: СЕТЕВОЙ РЕЗОЛВ ═══
        if (string.IsNullOrEmpty(streamUrl))
        {
            var streamInfo = await _youtube.RefreshStreamUrlAsync(track, false, ct)
                ?? throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

            streamUrl = streamInfo.Url;

            if (bitrateHint <= 0)
                bitrateHint = streamInfo.Bitrate;

            track.TransientBitrate = bitrateHint;
        }

        return (streamUrl ?? "", bitrateHint);
    }

    #endregion

    #region N-Token Warning

       /// <summary>
    /// Пробрасывает предупреждение о сложной расшифровке n-токена только для активной сессии.
    /// Если включён <see cref="AudioSettings.SkipNTokenTracks"/>, текущий трек немедленно
    /// пропускается. Для skip используется режим userInitiated=true, чтобы не зациклиться
    /// на том же треке при <see cref="RepeatMode.One"/>.
    /// </summary>
    private void HandleNTokenDecryptionStarted()
    {
        int activeSession = Volatile.Read(ref _activeNTokenWarningSession);
        int currentSession = Volatile.Read(ref _session);

        if (activeSession == 0 || activeSession != currentSession)
            return;

        bool wasSkipped = _library.Settings.Audio.SkipNTokenTracks;
        bool firstWarningForSession = Interlocked.Exchange(ref _lastNTokenWarningSession, activeSession) != activeSession;

        if (!firstWarningForSession)
            return;

        var warning = new NTokenWarningInfo(CurrentTrack, wasSkipped);
        RaiseOnUI(() => OnNTokenDecryptionWarning?.Invoke(warning));

        if (!wasSkipped)
            return;

        Log.Info($"[AudioEngine] SkipNTokenTracks: skipping {CurrentTrack?.Id ?? "unknown"}");

        _ = Task.Run(() => NavigateAsync(forward: true, userInitiated: true));
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Генерирует событие ошибки на UI потоке.
    /// </summary>
    private void RaiseError(Exception exception)
    {
        Log.Debug($"[AudioEngine] RaiseError: {exception.GetType().Name}");
        RaiseOnUI(() => OnErrorOccurred?.Invoke(exception));
    }

    #endregion

    #region Playback Control

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;

        int session = BeginNewSession();

        return EnqueueCommandAsync(async () =>
        {
            if (Volatile.Read(ref _session) != session) return;

            lock (_queueLock)
            {
                int idx = _queue.FindIndex(t => t.Id == track.Id);
                if (idx >= 0)
                {
                    _currentIndex = idx;
                    _queue[idx] = track;
                    InvalidateQueueSnapshot();
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
            await PlayCurrentIndexAsync(session);
        });
    }

    public Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
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
                InvalidateQueueSnapshot();
            }

            RaiseOnUI(() => OnQueueChanged?.Invoke());
            await PlayCurrentIndexAsync(session);
        });
    }

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (shouldPlay)
        {
            if (_player.State == PlaybackState.Paused)
            {
                _player.Resume();
            }
            else if (_player.State == PlaybackState.Stopped && CurrentTrack != null)
            {
                int session = BeginNewSession();
                await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
            }
        }
        else
        {
            _player.Pause();
        }
    }

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

    public Task PlayNextAsync() => NavigateAsync(forward: true, userInitiated: true);
    public Task PlayPreviousAsync() => NavigateAsync(forward: false, userInitiated: true);

    #endregion

    #region Navigation

    private async Task NavigateAsync(bool forward, bool userInitiated)
    {
        int session = BeginNewSession();
        bool canMove;

        lock (_queueLock)
        {
            canMove = forward ? TryMoveNext(userInitiated) : TryMovePrevious();
        }

        if (canMove)
        {
            await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
        }
        else if (!forward && _player.State != PlaybackState.Stopped)
        {
            await _player.SeekAsync(TimeSpan.Zero);
        }
        else
        {
            Stop();
        }
    }

    private bool TryMoveNext(bool userInitiated)
    {
        if (_queue.Count == 0) return false;
        if (!userInitiated && RepeatMode == RepeatMode.One) return true;
        if (_currentIndex + 1 < _queue.Count) { _currentIndex++; return true; }
        if (RepeatMode == RepeatMode.All) { _currentIndex = 0; return true; }
        return false;
    }

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

    private void HandlePlayerTrackEnded()
    {
        // Если плеер в состоянии Loading/Buffering, TrackEnded пришёл
        // от устаревшего decoder loop. Игнорируем.
        var playerState = _player.State;
        if (playerState is PlaybackState.Loading or PlaybackState.Buffering)
        {
            Log.Debug($"[AudioEngine] Ignoring TrackEnded during {playerState}");
            return;
        }

        int session = BeginNewSession();

        _ = Task.Run(async () =>
        {
            bool canAdvance;
            lock (_queueLock) { canAdvance = TryMoveNext(userInitiated: false); }

            if (canAdvance)
                await EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
            else
                Stop();
        });
    }

    private void HandleStreamInfoChanged(AudioStreamInfo info)
    {
        RaiseOnUI(() =>
        {
            StreamInfo = info;
            OnStreamInfoChanged?.Invoke(info);
        });
    }

    private async ValueTask<string?> RefreshUrlCallbackAsync(string trackId, CancellationToken ct)
    {
        var track = await _library.GetTrackAsync(trackId, ct);
        if (track == null) return null;

        // Linked token: отменяется и по incoming ct, и по session
        var sessionToken = GetSessionToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);

        try
        {
            var info = await _youtube.RefreshStreamUrlAsync(track, true, linked.Token);
            return info?.Url;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[AudioEngine] URL refresh cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] URL refresh failed: {ex.Message}");
            RaiseError(ex);
            return null;
        }
    }

    #endregion

    #region Seek

    public void SeekDebounced(TimeSpan position)
    {
        lock (_seekLock)
        {
            _pendingSeekPosition = position;

            if (_hasScheduledSeek) return;

            _seekDebounceCts?.Cancel();
            _seekDebounceCts?.Dispose();
            _seekDebounceCts = new CancellationTokenSource();
            _hasScheduledSeek = true;

            _ = ExecuteDebouncedSeekAsync(_seekDebounceCts.Token);
        }
    }

    public ValueTask SeekAsync(TimeSpan position)
    {
        lock (_seekLock)
        {
            _seekDebounceCts?.Cancel();
            _hasScheduledSeek = false;
        }

        return _player.SeekAsync(position);
    }

    private async Task ExecuteDebouncedSeekAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SeekDebounceMs, ct);

            TimeSpan pos;
            lock (_seekLock)
            {
                pos = _pendingSeekPosition;
                _hasScheduledSeek = false;
            }

            await _player.SeekAsync(pos, ct);
        }
        catch (OperationCanceledException)
        {
            lock (_seekLock) _hasScheduledSeek = false;
        }
        catch (Exception ex)
        {
            lock (_seekLock) _hasScheduledSeek = false;
            Log.Warn($"[AudioEngine] Seek error: {ex.Message}");
        }
    }

    #endregion

    #region Volume

    public float GetVolume() => _volumePercent;

    public void SetVolumeInstant(float value)
    {
        int maxVol = Math.Max(_library.Settings.MaxVolumeLimit, 100);

        lock (_volumeLock)
        {
            _volumePercent = Math.Clamp((int)Math.Round(value), 0, maxVol);
        }

        ApplyVolume(instant: true);
    }

    public void SaveVolumeNow()
    {
        _library.UpdateSettings(s => s.Volume = _volumePercent);
    }

    public void OnMaxVolumeLimitChanged(int newMaxVolume)
    {
        int currentMax = Math.Max(_library.Settings.MaxVolumeLimit, 100);

        if (currentMax == newMaxVolume) return;

        lock (_volumeLock)
        {
            if (_volumePercent > newMaxVolume)
                _volumePercent = newMaxVolume;
        }

        ApplyVolume(instant: true);
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(newMaxVolume));
    }

    public void UpdateAudioSettings()
    {
        ApplyVolume(instant: false);
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
    }

    public void InitializeVolumeFromSettings()
    {
        if (_volumeInitialized) return;

        var settings = _library.Settings;
        int maxVol = Math.Max(settings.MaxVolumeLimit, 100);

        float savedVolume = settings.Volume;
        _volumePercent = savedVolume switch
        {
            > 0 and <= 1.0f => (int)(savedVolume * 100),
            > 1 => Math.Clamp((int)savedVolume, 0, maxVol),
            _ => 50
        };

        _volumeInitialized = true;
        ApplyVolume(instant: true);

        Log.Info($"[AudioEngine] Volume initialized: {_volumePercent}");
    }

    private void ApplyVolume(bool instant)
    {
        var settings = _library.Settings;
        var audioSettings = settings.Audio;
        int maxVolume = Math.Max(settings.MaxVolumeLimit, 100);

        float gain = ComputeGain(_volumePercent, maxVolume, audioSettings);

        float targetGainDb = Math.Clamp(settings.TargetGainDb, -20f, 20f);
        gain *= MathF.Pow(10f, targetGainDb / 20f);

        gain = Math.Clamp(gain, 0f, MaxGain);

        if (instant || !audioSettings.SmoothVolumeEnabled)
        {
            _currentGain = gain;
            _player.Volume = gain;
        }
        else
        {
            StartSmoothVolumeTransition(gain, audioSettings.SmoothVolumeDurationMs);
        }
    }

    private static float ComputeGain(int volumePercent, int maxVolume, AudioSettings audioSettings)
    {
        if (volumePercent <= 0) return 0f;

        if (audioSettings.VolumeBoostEnabled)
        {
            if (volumePercent <= VolumeNormalRange)
            {
                float t = volumePercent / (float)VolumeNormalRange;
                return ApplyVolumeCurve(t, audioSettings.VolumeCurve);
            }

            float boostUnits = volumePercent - VolumeNormalRange;
            return 1.0f + boostUnits / VolumeNormalRange;
        }

        float normalized = (float)volumePercent / maxVolume;
        return ApplyVolumeCurve(normalized, audioSettings.VolumeCurve);
    }

    private static float ApplyVolumeCurve(float t, VolumeCurveType curve)
    {
        t = Math.Clamp(t, 0f, 1f);

        return curve switch
        {
            VolumeCurveType.Linear => t,
            VolumeCurveType.Quadratic => t * t,
            VolumeCurveType.Logarithmic => MathF.Log2(1f + t),
            VolumeCurveType.Cubic => t * t * t,
            VolumeCurveType.SpeedOfLight => (MathF.Exp(t * 2f) - 1f) / (MathF.Exp(2f) - 1f),
            _ => t * t
        };
    }

    private void StartSmoothVolumeTransition(float targetGain, int durationMs)
    {
        _smoothVolumeCts?.Cancel();
        _smoothVolumeCts?.Dispose();
        _smoothVolumeCts = new CancellationTokenSource();

        var ct = _smoothVolumeCts.Token;
        float startGain = _currentGain;
        var startTime = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    float progress = (float)(DateTime.UtcNow - startTime).TotalMilliseconds / durationMs;

                    if (progress >= 1f)
                    {
                        _currentGain = targetGain;
                        _player.Volume = targetGain;
                        break;
                    }

                    _currentGain = startGain + (targetGain - startGain) * progress;
                    _player.Volume = _currentGain;

                    await Task.Delay(SmoothVolumeUpdateIntervalMs, ct);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private async Task VolumeSaveLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(VolumeSaveIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(_lifetimeCts.Token))
            {
                _library.UpdateSettings(s => s.Volume = _volumePercent);
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Quality Switching

    public async Task SwitchQualityAsync(string container, int bitrate)
    {
        if (CurrentTrack == null) return;

        int session = BeginNewSession();
        var ct = GetSessionToken();

        try
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastQualitySwitchTime).TotalMilliseconds;
            if (elapsed < QualitySwitchCooldownMs)
            {
                int waitMs = QualitySwitchCooldownMs - (int)elapsed;
                Log.Debug($"[AudioEngine] Quality switch debounced, waiting {waitMs}ms");
                await Task.Delay(waitMs, ct);
            }

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

            Log.Info($"[AudioEngine] Switching quality to {container}/{bitrate}kbps at {pos.TotalSeconds:F1}s");

            await EnqueueCommandAsync(() =>
            {
                if (Volatile.Read(ref _session) == session)
                    StartQualitySwitchTransition(session, track, pos);

                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || Volatile.Read(ref _session) != session)
        {
            Log.Debug("[AudioEngine] Quality switch cancelled");
        }
    }

    /// <summary>
    /// Запускает смену качества вне command queue.
    /// Предотвращает блокировку пользовательского управления на время YouTube resolve.
    /// </summary>
    private void StartQualitySwitchTransition(int session, TrackInfo track, TimeSpan position)
    {
        var ct = GetSessionToken();
        _ = SwitchQualityCoreAsync(track, position, session, ct);
    }

    /// <summary>
    /// Выполняет сетевой resolve потока для смены качества с поддержкой отмены по сессии.
    /// </summary>
    private async Task SwitchQualityCoreAsync(
        TrackInfo track, TimeSpan position, int session, CancellationToken ct)
    {
        try
        {
            _player.Stop();
            track.StreamUrl = "";

            Volatile.Write(ref _activeNTokenWarningSession, session);

            ct.ThrowIfCancellationRequested();

            var streamInfo = await _youtube.RefreshStreamUrlAsync(track, false, ct)
                          ?? await _youtube.RefreshStreamUrlAsync(track, true, ct);

            if (streamInfo == null)
            {
                RaiseError(new InvalidOperationException("Failed to switch quality: no stream available"));
                return;
            }

            if (Volatile.Read(ref _session) != session)
                return;

            ct.ThrowIfCancellationRequested();

            var realBitrate = streamInfo.Value.Bitrate;
            track.TransientBitrate = realBitrate;

            TimeSpan? seekPosition = position.TotalSeconds > 1 ? position : null;

            await _player.PlayAsync(
                streamInfo.Value.Url,
                track.Id,
                realBitrate,
                ct,
                seekPosition: seekPosition);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || Volatile.Read(ref _session) != session)
        {
            Log.Debug("[AudioEngine] Quality switch cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioEngine] Quality switch failed: {ex.Message}");
            RaiseError(ex);
        }
        finally
        {
            if (Volatile.Read(ref _activeNTokenWarningSession) == session)
                Volatile.Write(ref _activeNTokenWarningSession, 0);
        }
    }

    #endregion

    #region Queue Management

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
            int session = BeginNewSession();
            StartPlaybackTransition(session, playbackTrack);
        }
    }

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

            var current = _currentIndex >= 0 && _currentIndex < _queue.Count
                ? _queue[_currentIndex]
                : null;

            // Fisher-Yates shuffle
            for (int n = _queue.Count - 1; n > 0; n--)
            {
                int k = Random.Shared.Next(n + 1);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }

            if (current != null)
            {
                int newIndex = _queue.IndexOf(current);
                if (newIndex > 0)
                {
                    _queue.RemoveAt(newIndex);
                    _queue.Insert(0, current);
                }
                _currentIndex = 0;
            }
            else
            {
                _currentIndex = -1;
            }

            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
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

    public void MoveQueueItem(int from, int to)
    {
        lock (_queueLock)
        {
            if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count)
                return;

            var item = _queue[from];
            _queue.RemoveAt(from);
            _queue.Insert(to, item);

            if (_currentIndex == from)
                _currentIndex = to;
            else if (from < _currentIndex && to >= _currentIndex)
                _currentIndex--;
            else if (from > _currentIndex && to <= _currentIndex)
                _currentIndex++;

            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    #endregion

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _player.GetActivePipeline();

    public long GetDownloadedBytes() => _player.GetDownloadedBytes();
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => _player.GetBufferedRanges();

    public (string Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        var info = StreamInfo;
        return (info.Codec, info.Bitrate, info.IsValid);
    }

    #endregion

    #region Helpers

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(_lifetimeCts.Token))
            {
                try
                {
                    await cmd();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error($"[AudioEngine] Command error: {ex.Message}");
                    RaiseError(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task EnqueueCommandAsync(Func<Task> command)
    {
        return _commandQueue.Writer.WriteAsync(command).AsTask();
    }

    private static void RaiseOnUI(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            action();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    private void InvalidateQueueSnapshot() => _queueSnapshot = null;

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        AudioSourceFactory.ApplyInternetProfile(profile);
        Log.Info($"[AudioEngine] Profile switched to {profile}");
        return Task.CompletedTask;
    }

    #endregion

    #region Dispose

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

            _smoothVolumeCts?.Cancel();
            _smoothVolumeCts?.Dispose();

            lock (_sessionLock)
            {
                _sessionCts?.Cancel();
                _sessionCts?.Dispose();
            }

            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();

            _player.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}