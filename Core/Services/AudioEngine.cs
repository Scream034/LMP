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
/// что отменяет <see cref="_sessionCts"/>. Все сетевые вызовы привязаны к этому токену.</para>
///
/// <para><b>Архитектура громкости:</b></para>
/// <para>Gain вычисляется в AudioEngine (volume curve, boost, target dB) и передаётся
/// в <see cref="AudioPipeline.SetGain"/>.
/// Pipeline применяет gain программно к PCM сэмплам в audio callback.
/// Hardware volume backend'а НЕ используется для управления громкостью.
/// Плавность обеспечивается архитектурно — provider buffer (~500ms) в NAudioBackend.</para>
///
/// <para><b>Failure Barrier:</b></para>
/// <para>ID трека, вызвавшего фатальную ошибку, запечатывается в <see cref="_sealedFailedTrackId"/>.
/// Барьер сбрасывается только явным действием пользователя.</para>
/// </remarks>
public sealed class AudioEngine : ViewModelBase, IDisposable
{
    #region Constants

    private const int CommandQueueCapacity = 32;
    private const int SeekDebounceMs = 100;
    private const int VolumeSaveIntervalMs = 2000;

    /// <summary>
    /// Базовый диапазон громкости (0-200 = 0-100% без boost).
    /// </summary>
    public const int VolumeNormalRange = 200;

    /// <summary>
    /// Максимальный gain (защита от перегрузки).
    /// </summary>
    public const float MaxGain = 4.0f;

    /// <summary>
    /// Минимальный интервал между переключениями качества (мс).
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

    #endregion

    #region Playback State

    private volatile bool _isSuspended;
    private DateTime _lastQualitySwitchTime = DateTime.MinValue;

    /// <summary>
    /// ID трека, для которого активна расшифровка n-token в текущей сессии.
    /// </summary>
    private string? _nTokenActiveTrackId;

    /// <summary>
    /// ID трека, для которого предупреждение уже было показано.
    /// </summary>
    private string? _nTokenWarnedTrackId;

    /// <summary>
    /// ID трека, терминально прерванного после фатальной ошибки.
    /// </summary>
    private string? _sealedFailedTrackId;

    /// <summary>
    /// Трек, подготавливаемый к воспроизведению. Устанавливается синхронно
    /// до вызова <see cref="AudioPlayer.PlayAsync"/>, гарантируя доступность
    /// в <see cref="ConfigurePipelineBeforeStart"/> без гонки с UI-потоком.
    /// </summary>
    /// <remarks>
    /// <para><b>Проблема:</b> <see cref="CurrentTrack"/> обновляется через
    /// <see cref="Avalonia.Threading.Dispatcher.Post"/> (асинхронно на UI-поток).
    /// При кэшированном треке pipeline создаётся за ~5ms — быстрее, чем UI-поток
    /// обработает Post. <see cref="ConfigurePipelineBeforeStart"/> читает
    /// <see cref="CurrentTrack"/> = null → gain не резолвится → ненужный pre-scan.</para>
    /// <para><b>Решение:</b> синхронная запись до <see cref="AudioPlayer.PlayAsync"/>,
    /// чтение из <see cref="ConfigurePipelineBeforeStart"/> на command thread AudioPlayer.
    /// Visibility гарантирована volatile + happens-before семантикой Channel.</para>
    /// </remarks>
    private volatile TrackInfo? _preparingTrack;

    /// <summary>
    /// Очередь отложенных записей gain нормализации в БД.
    /// Заполняется из fill thread (через <see cref="HandleGainLocked"/>)
    /// и из command flow (<see cref="PlayTrackCoreAsync"/>).
    /// Дренируется in-place в <see cref="VolumeSaveLoopAsync"/> каждые 2 секунды
    /// и при <see cref="Dispose"/> — защита от потери данных при kill.
    /// </summary>
    private readonly ConcurrentQueue<(string TrackId, float Gain)> _pendingGainWrites = new();

    #endregion

    #region Queue

    private readonly List<TrackInfo> _queue = new(64);
    private IReadOnlyList<TrackInfo>? _queueSnapshot;
    private int _currentIndex = -1;

    private bool _queueMutatedByNavigation;

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
    /// Текущий фактически применённый gain после volume curve, boost и dB-коррекции.
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
    /// </summary>
    public event Action<NTokenWarningInfo>? OnNTokenDecryptionWarning;

    /// <summary>
    /// Контекст предупреждения о сложной расшифровке n-токена.
    /// </summary>
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
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _ = Task.Run(ProcessCommandsAsync);
        _ = Task.Run(() => VolumeSaveLoopAsync());

        Log.Info($"[AudioEngine] Ready. Volume={_volumePercent}%");
    }

    /// <summary>
    /// Конфигурирует pipeline до открытия gate.
    /// Использует <see cref="NormalizationGainResolver"/> как единственный источник истины
    /// для определения gain нормализации.
    /// </summary>
    /// <remarks>
    /// <para>Читает <see cref="_preparingTrack"/> вместо <see cref="CurrentTrack"/>,
    /// поскольку CurrentTrack обновляется асинхронно через Dispatcher.Post
    /// и может быть null/stale на момент вызова (race condition при кэшированных треках).</para>
    /// </remarks>
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

    private void SubscribeToProviderEvents()
    {
        _youtube.OnNTokenDecryptionStarted += HandleNTokenDecryptionStarted;
    }

    private void InitializeFromSettings()
    {
        var settings = _library.Settings;
        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;
        _volumePercent = settings.Volume > 0 ? (int)settings.Volume : 60;
        ApplyGainToPipeline();
    }

    private void ApplyStreamingProfile()
    {
        var profile = _library.Settings.InternetProfile;
        AudioSourceFactory.ApplyInternetProfile(profile);
        Log.Info($"[AudioEngine] Streaming profile: {profile}");
    }

    #endregion

    #region Session Management

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

    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock) return _sessionCts?.Token ?? _lifetimeCts.Token;
    }

    private bool IsSessionStale(int session, CancellationToken ct) =>
        ct.IsCancellationRequested || Volatile.Read(ref _session) != session;

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
    /// Возвращает true, если указанный трек уже был терминально прерван.
    /// </summary>
    private bool IsSealedFailedTrack(string? trackId)
    {
        var sealedTrackId = Interlocked.CompareExchange(ref _sealedFailedTrackId, null, null);
        return !string.IsNullOrEmpty(trackId)
            && !string.IsNullOrEmpty(sealedTrackId)
            && string.Equals(sealedTrackId, trackId, StringComparison.Ordinal);
    }

    private void ResetSealedFailedTrack() => Interlocked.Exchange(ref _sealedFailedTrackId, null);

    private void SealFailedTrack(string? trackId)
    {
        if (!string.IsNullOrEmpty(trackId))
            Interlocked.Exchange(ref _sealedFailedTrackId, trackId);
    }

    /// <summary>
    /// Прерывает текущий playback-flow для фатально сбойного трека.
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
    /// Полностью завершает воспроизведение после фатальной ошибки текущего трека.
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

    protected override void OnSuspend()
    {
        _isSuspended = true;
        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is Audio.Sources.CachingStreamSource cachingSource)
            cachingSource.Suspend();
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is Audio.Sources.CachingStreamSource cachingSource)
            cachingSource.Resume();
        _ = PreWarmHttpConnectionAsync();
    }

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
    /// Вызывается при фиксации gain нормализации.
    /// </summary>
    /// <remarks>
    /// <para>Вызывается из fill thread — блокировка и await запрещены.
    /// DB-персистенция откладывается в <see cref="_pendingGainWrites"/>
    /// и дренируется в <see cref="VolumeSaveLoopAsync"/> (каждые 2 сек).</para>
    /// </remarks>
    private void HandleGainLocked(string trackId, float gain)
    {
        var canonical = _library.GetTrack(trackId);
        canonical?.SetGain(gain);

        var current = CurrentTrack;
        if (current != null && current.Id == trackId && !ReferenceEquals(current, canonical))
            current.SetGain(gain);

        _pendingGainWrites.Enqueue((trackId, gain));
    }

    private bool TryMoveNextSkippingTrack(string? skippedTrackId)
    {
        if (_queue.Count <= 1 || _currentIndex < 0 || _currentIndex >= _queue.Count)
            return false;

        int startIndex = _currentIndex;
        for (int step = 1; step < _queue.Count; step++)
        {
            int candidateIndex = (startIndex + step) % _queue.Count;
            if (_queue[candidateIndex].Id != skippedTrackId)
            {
                _currentIndex = candidateIndex;
                return true;
            }
        }
        return false;
    }

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

    private void StartPlaybackTransition(int session, TrackInfo track)
    {
        if (IsSealedFailedTrack(track.Id)) return;
        var ct = GetSessionToken();
        _ = PlayTrackCoreAsync(track, session, ct);
    }

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

    public Task PlayNextAsync() { ResetSealedFailedTrack(); return NavigateAsync(forward: true, userInitiated: true); }
    public Task PlayPreviousAsync() { ResetSealedFailedTrack(); return NavigateAsync(forward: false, userInitiated: true); }

    #endregion

    #region Navigation

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

    private bool TryMovePrevious()
    {
        if (_queue.Count == 0) return false;
        if (CurrentPosition.TotalSeconds > 3) return false;
        if (_currentIndex > 0) { _currentIndex--; return true; }
        if (RepeatMode == RepeatMode.All) { _currentIndex = _queue.Count - 1; return true; }
        return false;
    }

    /// <summary> Fisher-Yates shuffle очереди на месте. </summary>
    private void ApplyShuffleInPlace(bool preserveCurrentAtStart)
    {
        if (_queue.Count < 2) return;

        if (preserveCurrentAtStart && _currentIndex >= 0 && _currentIndex < _queue.Count)
        {
            if (_currentIndex != 0)
                (_queue[0], _queue[_currentIndex]) = (_queue[_currentIndex], _queue[0]);

            for (int n = _queue.Count - 1; n > 1; n--)
            {
                int k = 1 + Random.Shared.Next(n);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }

            _currentIndex = 0;
        }
        else
        {
            for (int n = _queue.Count - 1; n > 0; n--)
            {
                int k = Random.Shared.Next(n + 1);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }
        }

        InvalidateQueueSnapshot();
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

    private void HandleStreamInfoChanged(AudioStreamInfo info)
    {
        RaiseOnUI(() => { StreamInfo = info; OnStreamInfoChanged?.Invoke(info); });
    }

    private async ValueTask<string?> RefreshUrlCallbackAsync(string trackId, CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId)) return null;
        var track = await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);
        if (track == null || IsSealedFailedTrack(trackId)) return null;

        var sessionToken = GetSessionToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);
        try
        {
            var info = await _youtube.RefreshStreamUrlAsync(track, true, linked.Token).ConfigureAwait(false);
            return info?.Url;
        }
        catch (Exception ex) when (linked.IsCancellationRequested || sessionToken.IsCancellationRequested || IsCancellationLike(ex) || !string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(trackId);
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
            _seekDebounceCts?.Cancel(); _seekDebounceCts?.Dispose();
            _seekDebounceCts = new CancellationTokenSource();
            _hasScheduledSeek = true;
            _ = ExecuteDebouncedSeekAsync(_seekDebounceCts.Token);
        }
    }

    public ValueTask SeekAsync(TimeSpan position)
    {
        lock (_seekLock) { _seekDebounceCts?.Cancel(); _hasScheduledSeek = false; }
        return _player.SeekAsync(position);
    }

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

    /// <summary> Возвращает текущее значение громкости в процентах (0–MaxVolume). </summary>
    public float GetVolume() => _volumePercent;

    /// <summary> Устанавливает громкость мгновенно. </summary>
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

    /// <summary> Сохраняет текущую громкость в настройки немедленно. </summary>
    public void SaveVolumeNow() => _library.UpdateSettings(s => s.Volume = _volumePercent);

    /// <summary> Обрабатывает изменение максимального лимита громкости из настроек. </summary>
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

    /// <summary> Пересчитывает и применяет gain после изменения аудио-настроек ... </summary>
    public void UpdateAudioSettings()
    {
        ApplyGainToPipeline();
        ApplyNormalizationToPipeline();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
    }

    /// <summary> Инициализирует громкость из сохранённых настроек при первом запуске. </summary>
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

    /// <summary> Вычисляет финальный gain и передаёт его в активный pipeline. </summary>
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

    /// <summary> Пробрасывает настройки нормализации в активный pipeline. </summary>
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

    /// <summary> Вычисляет raw gain из процента громкости с применением volume curve. </summary>
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
            float boostExtra = (volumePercent - VolumeNormalRange) / (float)VolumeNormalRange;
            return 1.0f + boostExtra;
        }

        float normalized = (float)volumePercent / maxVolume;
        return ApplyVolumeCurve(normalized, audioSettings.VolumeCurve);
    }

    /// <summary> Применяет кривую громкости к нормализованному значению t ∈ [0, 1]. </summary>
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

    /// <summary> Периодически сохраняет текущую громкость и отложенные gain нормализации в БД. </summary>
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

    /// <summary> Дренирует очередь отложенных записей gain нормализации в БД. </summary>
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

    private void StartQualitySwitchTransition(int session, TrackInfo track, TimeSpan position)
    {
        var ct = GetSessionToken();
        _ = SwitchQualityCoreAsync(track, position, session, ct);
    }

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

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        lock (_queueLock) { _queue.AddRange(tracks); InvalidateQueueSnapshot(); }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

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

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _player.GetActivePipeline();
    public long GetDownloadedBytes() => _player.GetDownloadedBytes();
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => _player.GetBufferedRanges();

    /// <summary>
    /// Возвращает краткую информацию о текущем потоке для UI-слоя.
    /// </summary>
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
            await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                try { await cmd().ConfigureAwait(false); }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task EnqueueCommandAsync(Func<Task> command) =>
        _commandQueue.Writer.WriteAsync(command).AsTask();

    private static void RaiseOnUI(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    private void InvalidateQueueSnapshot() => _queueSnapshot = null;

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        AudioSourceFactory.ApplyInternetProfile(profile);
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