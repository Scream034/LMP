using System.Numerics;
using System.Runtime.InteropServices;
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
/// </remarks>
public sealed class AudioEngine : ViewModelBase, IDisposable
{
    #region Constants

    private const int MaxHistorySize = 100;
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
    /// Целевой уровень для простой нормализации (peak).
    /// </summary>
    private const float NormalizationTargetPeak = 0.95f;

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

    private float _normalizationFactor = 1.0f;

    #endregion

    #region Queue

    private readonly List<TrackInfo> _queue = new(64);
    private readonly List<TrackInfo> _history = new(MaxHistorySize);
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

    #endregion

    #region Events

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
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
    /// <remarks>
    /// <para>Типизированное исключение для обработки в <see cref="PlaybackErrorOrchestrator"/>.</para>
    /// <para>Возможные типы:</para>
    /// <list type="bullet">
    ///   <item><see cref="BotDetectionException"/></item>
    ///   <item><see cref="LoginRequiredException"/></item>
    ///   <item><see cref="StreamUnavailableException"/></item>
    ///   <item><see cref="ChunkDownloadFatalException"/></item>
    ///   <item>Другие <see cref="Exception"/></item>
    /// </list>
    /// </remarks>
    public event Action<Exception>? OnErrorOccurred;

    /// <summary>
    /// Legacy событие для совместимости — только строка сообщения.
    /// </summary>
    [Obsolete("Use OnErrorOccurred instead for typed exception handling")]
    public event Action<string>? OnError;

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

        // Ошибки из AudioPlayer → пробрасываем в наше событие
        _player.Events.ErrorOccurred += err =>
        {
            var exception = err.Exception ?? new Exception(err.Message);
            RaiseError(exception);
        };
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

    #region Internal Playback

    /// <summary>
    /// Воспроизводит трек по текущему индексу в очереди.
    /// </summary>
    /// <remarks>
    /// <para><b>Обработка ошибок:</b></para>
    /// <para>Все исключения пробрасываются через <see cref="OnErrorOccurred"/>.
    /// Этот метод НЕ показывает диалоги и НЕ принимает решения о UI.</para>
    /// </remarks>
    private async Task PlayCurrentIndexAsync(int session)
    {
        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
            track = _queue[_currentIndex];
        }

        if (track == null) return;

        _normalizationFactor = 1.0f;

        // Останавливаем через StopAsync — ждём реальной остановки pipeline
        await _player.StopAsync();

        // Проверяем сессию после async stop
        if (Volatile.Read(ref _session) != session) return;

        // Обновляем UI
        RaiseOnUI(() =>
        {
            CurrentTrack = track;
            StreamInfo = AudioStreamInfo.Empty;
            OnTrackChanged?.Invoke(track);
            OnPositionChanged?.Invoke(TimeSpan.Zero);
        });

        try
        {
            var (streamUrl, bitrateHint) = await ResolveStreamUrlAsync(track);

            if (Volatile.Read(ref _session) != session) return;

            await _player.PlayAsync(streamUrl, track.Id, bitrateHint, CancellationToken.None);
            AddToHistory(track);
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка, просто выходим
            Log.Debug("[AudioEngine] PlayCurrentIndex cancelled");
        }
        catch (Exception ex)
        {
            // ВСЕ ИСКЛЮЧЕНИЯ → OnErrorOccurred
            // Оркестратор решит что делать (диалог, toast, skip)
            Log.Error($"[AudioEngine] Play error: {ex.GetType().Name}: {ex.Message}");
            RaiseError(ex);
        }
    }

    /// <summary>
    /// Резолвит URL стрима для трека.
    /// </summary>
    /// <exception cref="BotDetectionException">При rate limiting.</exception>
    /// <exception cref="StreamUnavailableException">При недоступности стрима.</exception>
    /// <exception cref="LoginRequiredException">При требовании авторизации.</exception>
    private async Task<(string Url, int Bitrate)> ResolveStreamUrlAsync(TrackInfo track)
    {
        string? streamUrl = track.StreamUrl;
        int bitrateHint = track.TransientBitrate;

        // ВСЕГДА проверяем кэш первым
        var cached = AudioSourceFactory.FindAnyCachedTrack(track.Id);
        if (cached != null)
        {
            Log.Debug($"[AudioEngine] Using cache: {cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps");
            return ("", cached.Value.Entry.Bitrate);
        }

        if (string.IsNullOrEmpty(streamUrl))
        {
            // Может выбросить BotDetectionException, StreamUnavailableException, LoginRequiredException
            var streamInfo = await _youtube.RefreshStreamUrlAsync(track, false, CancellationToken.None);

            if (streamInfo == null)
                throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

            streamUrl = streamInfo.Value.Url;
            if (bitrateHint <= 0)
                bitrateHint = streamInfo.Value.Bitrate;
        }

        return (streamUrl ?? "", bitrateHint);
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Генерирует события ошибки.
    /// Использует InvokeAsync для гарантированной доставки (не Post).
    /// </summary>
    private void RaiseError(Exception exception)
    {
        Log.Debug($"[AudioEngine] RaiseError: {exception.GetType().Name}");

        // Для ошибок используем прямой вызов если мы на UI,
        // иначе InvokeAsync (не Post!) для гарантированной доставки
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            OnErrorOccurred?.Invoke(exception);
            OnError?.Invoke(exception.Message);
        }
        else
        {
            // InvokeAsync гарантирует выполнение, Post — нет
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnErrorOccurred?.Invoke(exception);
                OnError?.Invoke(exception.Message);
            });
        }
    }


    #endregion

    #region Playback Control

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;

        int session = Interlocked.Increment(ref _session);

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
        int session = Interlocked.Increment(ref _session);

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
                int session = Interlocked.Increment(ref _session);
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
        Interlocked.Increment(ref _session);
        _player.Stop();
        _normalizationFactor = 1.0f;

        RaiseOnUI(() =>
        {
            CurrentTrack = null;
            StreamInfo = AudioStreamInfo.Empty;
            OnTrackChanged?.Invoke(null);
            OnPlaybackStopped?.Invoke();
        });
    }

    public Task PlayNextAsync() => NavigateAsync(forward: true, userInitiated: true);
    public Task PlayPreviousAsync() => NavigateAsync(forward: false, userInitiated: true);

    #endregion

    #region Navigation

    private async Task NavigateAsync(bool forward, bool userInitiated)
    {
        int session = Interlocked.Increment(ref _session);
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
        // Если плеер в состоянии Loading/Buffering/Seeking, значит TrackEnded
        // пришёл от устаревшего decoder loop (например, при быстром seek).
        // AudioPlayer уже имеет свою защиту (OnTrackEnded проверяет state),
        // но дополнительная проверка здесь — defense in depth.
        var playerState = _player.State;
        if (playerState is PlaybackState.Loading or PlaybackState.Buffering)
        {
            Log.Debug($"[AudioEngine] Ignoring TrackEnded during {playerState}");
            return;
        }

        int session = Interlocked.Increment(ref _session);

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
        var track = await _library.GetTrackAsync(trackId);
        if (track == null) return null;

        try
        {
            var info = await _youtube.RefreshStreamUrlAsync(track, true, ct);
            return info?.Url;
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

        // Не вызываем если значение не изменилось
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

        // Применяем пользовательский gain из настроек
        float targetGainDb = Math.Clamp(settings.TargetGainDb, -20f, 20f);
        gain *= MathF.Pow(10f, targetGainDb / 20f);

        // Применяем нормализацию если включена
        if (audioSettings.NormalizationEnabled)
            gain *= _normalizationFactor;

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
            // Boost режим: 0-200 = 0-100%, >200 = boost
            if (volumePercent <= VolumeNormalRange)
            {
                float t = volumePercent / (float)VolumeNormalRange;
                return ApplyVolumeCurve(t, audioSettings.VolumeCurve);
            }

            float boostUnits = volumePercent - VolumeNormalRange;
            return 1.0f + boostUnits / VolumeNormalRange;
        }

        // Точная настройка: 0-maxVolume = 0-100%
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
        var duration = TimeSpan.FromMilliseconds(durationMs);

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

    #region Normalization

    /// <summary>
    /// Вычисляет коэффициент нормализации на основе пиковых значений PCM данных.
    /// Вызывается из AudioPipeline после декодирования первых фреймов.
    /// </summary>
    public void ComputeNormalization(ReadOnlySpan<float> samples)
    {
        if (!_library.Settings.Audio.NormalizationEnabled || samples.IsEmpty)
        {
            _normalizationFactor = 1.0f;
            return;
        }

        float peakValue = FindPeakValue(samples);

        if (peakValue > 0.001f && peakValue > NormalizationTargetPeak)
        {
            _normalizationFactor = NormalizationTargetPeak / peakValue;
            Log.Debug($"[AudioEngine] Normalization: peak={peakValue:F3}, factor={_normalizationFactor:F3}");
        }
        else
        {
            _normalizationFactor = 1.0f;
        }

        // Переприменяем громкость с новым фактором
        ApplyVolume(instant: true);
    }

    private static float FindPeakValue(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        int i = 0;

        // SIMD поиск максимума
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var maxVec = Vector<float>.Zero;
            // var signMask = new Vector<float>(-0f); // Для абсолютного значения

            var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);

            foreach (var vec in vectors)
            {
                var abs = Vector.Abs(vec);
                maxVec = Vector.Max(maxVec, abs);
            }

            for (int j = 0; j < Vector<float>.Count; j++)
                peak = MathF.Max(peak, maxVec[j]);

            i = vectors.Length * Vector<float>.Count;
        }

        // Скалярный хвост
        for (; i < samples.Length; i++)
            peak = MathF.Max(peak, MathF.Abs(samples[i]));

        return peak;
    }

    /// <summary>
    /// Применяет boost усиление через SIMD (вызывается из AudioCallback).
    /// </summary>
    public static void ApplyGainSimd(Span<float> data, float gain)
    {
        if (MathF.Abs(gain - 1.0f) < 0.001f) return;

        int i = 0;

        if (Vector.IsHardwareAccelerated && data.Length >= Vector<float>.Count)
        {
            var vecGain = new Vector<float>(gain);
            var vecMin = new Vector<float>(-1.0f);
            var vecMax = new Vector<float>(1.0f);

            var vectors = MemoryMarshal.Cast<float, Vector<float>>(data);

            for (int j = 0; j < vectors.Length; j++)
                vectors[j] = Vector.Min(Vector.Max(vectors[j] * vecGain, vecMin), vecMax);

            i = vectors.Length * Vector<float>.Count;
        }

        for (; i < data.Length; i++)
            data[i] = Math.Clamp(data[i] * gain, -1f, 1f);
    }

    #endregion

    #region Quality Switching

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

        int session = Interlocked.Increment(ref _session);

        await EnqueueCommandAsync(async () =>
        {
            if (Volatile.Read(ref _session) != session) return;

            try
            {
                _player.Stop();
                track.StreamUrl = "";

                var streamInfo = await _youtube.RefreshStreamUrlAsync(track, true, CancellationToken.None);
                if (streamInfo == null)
                {
                    RaiseOnUI(() => OnError?.Invoke("Failed to switch quality"));
                    return;
                }

                await _player.PlayAsync(streamInfo.Value.Url, track.Id, bitrate, CancellationToken.None);

                if (pos.TotalSeconds > 1)
                {
                    await WaitForPlayerReadyAsync(TimeSpan.FromSeconds(2));
                    await _player.SeekAsync(pos);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioEngine] Quality switch failed: {ex.Message}");
                RaiseOnUI(() => OnError?.Invoke("Failed to switch quality"));
            }
        });
    }

    private async Task WaitForPlayerReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_player.State is PlaybackState.Playing or PlaybackState.Paused)
                return;

            await Task.Delay(50);
        }
    }

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
            int session = Interlocked.Increment(ref _session);
            _ = EnqueueCommandAsync(() => PlayCurrentIndexAsync(session));
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

            // Текущий трек в начало
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

            // Корректируем currentIndex
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

    private void AddToHistory(TrackInfo track)
    {
        if (_history.Count > 0 && _history[^1].Id == track.Id) return;

        _history.Add(track);

        if (_history.Count > MaxHistorySize)
            _history.RemoveAt(0);
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
            lock (_seekLock)
            {
                _seekDebounceCts?.Cancel();
                _seekDebounceCts?.Dispose();
            }

            _smoothVolumeCts?.Cancel();
            _smoothVolumeCts?.Dispose();

            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();

            _player.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}