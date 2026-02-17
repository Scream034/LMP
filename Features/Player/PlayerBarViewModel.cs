using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using LMP.Core.Audio;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Player;

/// <summary>
/// ViewModel для панели управления плеером.
/// </summary>
public sealed class PlayerBarViewModel : ViewModelBase
{
    #region Constants

    private const int SeekCooldownMs = 250;
    private const int NavigationDebounceMs = 300;
    private const int HintDisplayDurationMs = 1500;
    private const int CopyHighlightDurationMs = 800;
    private const int PositionUpdateThrottleMs = 50;
    private const int BufferUpdateIntervalMs = 300;

    #endregion

    #region Fields

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly IClipboardService _clipboard;
    private readonly YoutubeProvider _youtube;
    private readonly MusicLibraryManager _musicManager;
    private readonly StreamCacheManager _cacheManager;

    private readonly DispatcherTimer _speedUpdateTimer;
    private readonly DispatcherTimer _fallbackPositionTimer;
    private readonly Subject<Unit> _nextSubject = new();
    private readonly Subject<Unit> _prevSubject = new();

    private bool _isSeeking;
    private bool _justFinishedSeeking;
    private bool _wasPlayingBeforeSeek;
    private bool _isInitialized;
    private volatile bool _isSuspended;

    private DateTime _lastSeekTime = DateTime.MinValue;
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;

    private int _lastVolumeBeforeMute = 50;

    #endregion

    #region Properties - Playback State

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }
    [Reactive] public bool IsLiked { get; private set; }
    [Reactive] public bool IsNavigating { get; private set; }

    /// <summary>Заголовок трека или placeholder.</summary>
    public string SafeTitle => CurrentTrack?.Title ?? L["Player_NotPlaying"];

    /// <summary>Автор трека.</summary>
    public string SafeAuthor => CurrentTrack?.Author ?? "";

    /// <summary>URL миниатюры.</summary>
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    #endregion

    #region Properties - Queue Info

    [Reactive] public int CurrentTrackIndex { get; private set; }
    [Reactive] public int TotalTracksInQueue { get; private set; }
    [Reactive] public bool HasQueueToShuffle { get; private set; }

    /// <summary>Отображаемый номер трека (1-based).</summary>
    public string CurrentTrackIndexDisplay => (CurrentTrackIndex + 1).ToString();

    #endregion

    #region Properties - Seek & Duration

    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public bool IsSeekBusy { get; private set; }
    [Reactive] public bool IsSeekPreviewVisible { get; set; }

    #endregion

    #region Properties - Buffer Progress

    /// <summary>
    /// Прогресс буферизации в процентах (0-100).
    /// </summary>
    [Reactive] public double BufferProgressPercent { get; private set; }

    /// <summary>
    /// Закэшированные диапазоны для отрисовки сегментов.
    /// Формат: список (startPercent, endPercent) от 0 до 1.
    /// </summary>
    [Reactive] public IReadOnlyList<(double Start, double End)> BufferedRanges { get; private set; } = [];

    /// <summary>
    /// Использовать сегментную визуализацию (для прерывистого кэша).
    /// </summary>
    public bool UseSegmentedBuffer => BufferedRanges.Count > 1;

    /// <summary>
    /// Трек полностью закэширован.
    /// </summary>
    [Reactive] public bool IsFullyBuffered { get; private set; }

    #endregion

    #region Properties - Volume

    [Reactive] public int Volume { get; set; }
    [Reactive] public int MaxVolume { get; private set; } = 100;
    [Reactive] public bool IsVolumePopupOpen { get; set; }
    [Reactive] public bool IsVolumePreviewVisible { get; set; }

    /// <summary>Громкость выключена.</summary>
    public bool IsMuted => Volume < 1;

    /// <summary>Низкая громкость (1-33%).</summary>
    public bool IsVolumeLow => Volume >= 1 && Volume <= 33;

    /// <summary>Средняя громкость (34-66%).</summary>
    public bool IsVolumeMedium => Volume > 33 && Volume <= 66;

    /// <summary>Высокая громкость (67-100%).</summary>
    public bool IsVolumeHigh => Volume > 66 && Volume <= 100;

    /// <summary>Усиленная громкость (&gt;100%).</summary>
    public bool IsVolumeBoosted => Volume > 100;

    /// <summary>Кисть для отображения процента громкости.</summary>
    public IBrush VolumePercentBrush
    {
        get
        {
            var app = Application.Current;
            if (app == null) return Brushes.White;

            if (Volume > 100)
            {
                if (app.Resources.TryGetResource("SystemWarnOrangeBrush", app.ActualThemeVariant, out var warnBrush)
                    && warnBrush is IBrush warn)
                {
                    return warn;
                }
                return new SolidColorBrush(Color.Parse("#FFB86C"));
            }

            if (app.Resources.TryGetResource("TextPrimaryBrush", app.ActualThemeVariant, out var textBrush)
                && textBrush is IBrush text)
            {
                return text;
            }
            return Brushes.White;
        }
    }

    #endregion

    #region Properties - Repeat & Shuffle

    [Reactive] public bool ShuffleEnabled { get; private set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }

    [Reactive] public bool IsRepeatHintVisible { get; private set; }
    [Reactive] public string RepeatHintText { get; private set; } = "";

    #endregion

    #region Properties - Like & Copy Hints

    [Reactive] public bool IsLikeHintVisible { get; private set; }
    [Reactive] public string LikeHintText { get; private set; } = "";

    [Reactive] public bool IsCopyHintVisible { get; private set; }
    [Reactive] public bool IsCopyHighlighted { get; private set; }

    #endregion

    #region Properties - Stream Info

    [Reactive] public string StreamInfo { get; private set; } = "";
    [Reactive] public bool ShowStreamInfo { get; private set; }
    [Reactive] public string DownloadSpeedText { get; private set; } = "";

    /// <summary>Доступные форматы потока.</summary>
    public ObservableCollection<StreamOption> AvailableFormats { get; } = [];

    #endregion

    #region Properties - Tooltips

    /// <summary>Тултип кнопки Shuffle.</summary>
    public string ShuffleTooltip => L.Get("Player_Shuffle", "Shuffle");

    /// <summary>Тултип кнопки Previous.</summary>
    public string PreviousTooltip => L.Get("Player_Previous", "Previous");

    /// <summary>Тултип кнопки Next.</summary>
    public string NextTooltip => L.Get("Player_Next", "Next");

    /// <summary>Тултип кнопки Play/Pause.</summary>
    public string PlayPauseTooltip => IsPlaying
        ? L.Get("Player_Pause", "Pause")
        : L.Get("Player_Play", "Play");

    /// <summary>Тултип кнопки Repeat.</summary>
    public string RepeatTooltip => RepeatMode switch
    {
        RepeatMode.None => L.Get("Player_Repeat_Off", "Repeat Off"),
        RepeatMode.RepeatAll => L.Get("Player_Repeat_All", "Repeat Queue"),
        RepeatMode.RepeatOne => L.Get("Player_Repeat_One", "Repeat Track"),
        _ => ""
    };

    /// <summary>Тултип кнопки Like.</summary>
    public string LikeTooltip => IsLiked
        ? L.Get("Track_Unlike", "Remove from Liked")
        : L.Get("Track_Like", "Add to Liked");

    /// <summary>Тултип кнопки Copy.</summary>
    public string CopyTooltip => L.Get("Track_CopyLink", "Copy Link");

    /// <summary>Тултип кнопки Mute.</summary>
    public string MuteTooltip => IsMuted
        ? L.Get("Player_Unmute", "Unmute")
        : L.Get("Player_Mute", "Mute");

    /// <summary>Тултип номера трека: "Трек X из Y".</summary>
    public string TrackNumberTooltip => string.Format(
        L.Get("Player_TrackNumber", "Track {0} of {1}"),
        CurrentTrackIndex + 1,
        TotalTracksInQueue);

    /// <summary>Тултип длительности.</summary>
    public string DurationTooltip
    {
        get
        {
            if (IsLoading || DurationSeconds <= 0)
            {
                return L.Get("Player_Loading_Duration", "Loading duration...");
            }

            string posStr = FormatTime(Position);
            string durStr = FormatTime(Duration);
            return string.Format(L.Get("Player_Duration", "Duration: {0} / {1}"), posStr, durStr);
        }
    }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadFormatsCommand { get; }
    public ReactiveCommand<StreamOption, Unit> SwitchFormatCommand { get; }

    #endregion

    #region Constructor

    public PlayerBarViewModel(
        AudioEngine audio,
        LibraryService library,
        IClipboardService clipboard,
        YoutubeProvider youtube,
        MusicLibraryManager musicManager,
        StreamCacheManager cacheManager)
    {
        _audio = audio;
        _library = library;
        _clipboard = clipboard;
        _youtube = youtube;
        _musicManager = musicManager;
        _cacheManager = cacheManager;

        MaxVolume = 100;
        Volume = 50;
        _lastVolumeBeforeMute = 50;
        ShuffleEnabled = false;
        RepeatMode = RepeatMode.None;
        UpdateQueueState();

        Log.Debug("[PlayerBar] Created with default values, waiting for initialization...");

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // ИНИЦИАЛИЗАЦИЯ ИЗ НАСТРОЕК

        Observable.FromEvent(
            h => _library.OnInitialized += h,
            h => _library.OnInitialized -= h)
            .Take(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => OnLibraryInitialized())
            .DisposeWith(Disposables);

        // AUDIO ENGINE EVENTS

        Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                SyncPlaybackState(state.Item1, state.Item2);
                this.RaisePropertyChanged(nameof(PlayPauseTooltip));
            })
            .DisposeWith(Disposables);

        Observable.FromEvent(
            h => _audio.OnQueueChanged += h,
            h => _audio.OnQueueChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateQueueState())
            .DisposeWith(Disposables);

        // Throttle position updates для экономии CPU
        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
            h => _audio.OnPositionChanged += h,
            h => _audio.OnPositionChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(PositionUpdateThrottleMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pos =>
            {
                if (!_isSeeking && !_justFinishedSeeking)
                {
                    Position = pos;
                    PositionSeconds = pos.TotalSeconds;
                    this.RaisePropertyChanged(nameof(DurationTooltip));
                }
                if (IsSeekBusy && !IsLoading)
                {
                    IsSeekBusy = false;
                }
            })
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<int>, int>(
            h => _audio.OnMaxVolumeChanged += h,
            h => _audio.OnMaxVolumeChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleMaxVolumeChanged)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleTrackChanged)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<AudioStreamInfo>, AudioStreamInfo>(
            h => _audio.OnStreamInfoChanged += h,
            h => _audio.OnStreamInfoChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateStreamInfo)
            .DisposeWith(Disposables);

        // BUFFER PROGRESS — через событие, не через интервал
        Observable.FromEvent<Action<BufferState>, BufferState>(
                h => _audio.OnBufferStateChanged += h,
                h => _audio.OnBufferStateChanged -= h)
            .Where(_ => !_isSuspended)
            .Throttle(TimeSpan.FromMilliseconds(BufferUpdateIntervalMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleBufferStateChanged)
            .DisposeWith(Disposables);

        // CACHE & LIBRARY EVENTS

        Observable.FromEvent<Action<string, string, int, bool>, (string, string, int, bool)>(
            h => (t, c, b, d) => h((t, c, b, d)),
            h => _cacheManager.OnFormatCached += h,
            h => _cacheManager.OnFormatCached -= h)
            .Subscribe(x => OnFormatCached(x.Item1, x.Item2, x.Item3, x.Item4))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
            h => _library.OnTrackUpdated += h,
            h => _library.OnTrackUpdated -= h)
            .Where(t => CurrentTrack != null && t.Id == CurrentTrack.Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                IsLiked = t.IsLiked;
                CurrentTrack?.IsLiked = t.IsLiked;
                this.RaisePropertyChanged(nameof(LikeTooltip));
            })
            .DisposeWith(Disposables);

        // VOLUME BINDING

        this.WhenAnyValue(x => x.Volume)
            .Subscribe(v =>
            {
                _audio.SetVolumeInstant(v);
                RaiseVolumePropertiesChanged();

                if (_isInitialized && v > 0)
                {
                    _library.UpdateSettings(s => s.LastVolume = v);
                }
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.RepeatMode)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(RepeatTooltip)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsLiked)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(LikeTooltip)))
            .DisposeWith(Disposables);

        // Обновляем тултипы при изменении индекса/количества
        this.WhenAnyValue(x => x.CurrentTrackIndex, x => x.TotalTracksInQueue)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(TrackNumberTooltip)))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<bool>, bool>(
            h => _audio.OnLoadingStateChanged += h,
            h => _audio.OnLoadingStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                IsLoading = loading;
                IsSeekBusy = loading;
                this.RaisePropertyChanged(nameof(DurationTooltip));
            })
            .DisposeWith(Disposables);

        // TIMERS

        _fallbackPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fallbackPositionTimer.Tick += (_, _) => FallbackPositionUpdate();
        _fallbackPositionTimer.Start();

        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();

        // NAVIGATION SUBJECTS

        _nextSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _audio.PlayNextAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);

        _prevSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _audio.PlayPreviousAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);

        // COMMANDS

        var canNavigate = this.WhenAnyValue(x => x.HasTrack, x => x.IsNavigating, x => x.IsLoading,
            (hasTrack, isNav, loading) => hasTrack && !isNav && !loading);

        PlayPauseCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            bool wantsToPlay = !_audio.IsPlaying;
            await _audio.SetPlaybackStateAsync(wantsToPlay);
        }, this.WhenAnyValue(x => x.HasTrack)));

        NextCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsNavigating = true;
            _nextSubject.OnNext(Unit.Default);
        }, canNavigate));

        PreviousCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsNavigating = true;
            _prevSubject.OnNext(Unit.Default);
        }, canNavigate));

        var canShuffle = this.WhenAnyValue(x => x.HasQueueToShuffle, x => x.IsLoading,
            (hasTracks, loading) => hasTracks && !loading);

        ShuffleQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _audio.ShuffleQueue();
            ShuffleEnabled = true;
            _library.UpdateSettings(s => s.ShuffleEnabled = true);

            Observable.Timer(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ShuffleEnabled = false)
                .DisposeWith(Disposables);
        }, canShuffle));

        ToggleRepeatCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            RepeatMode = RepeatMode switch
            {
                RepeatMode.None => RepeatMode.RepeatAll,
                RepeatMode.RepeatAll => RepeatMode.RepeatOne,
                _ => RepeatMode.None
            };
            _audio.RepeatMode = RepeatMode;
            _library.UpdateSettings(s => s.RepeatMode = RepeatMode);
            ShowRepeatModeHint();
        }));

        ToggleMuteCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            if (IsMuted)
            {
                int restoreVolume = _lastVolumeBeforeMute > 0 ? _lastVolumeBeforeMute : 50;
                Volume = restoreVolume;
                Log.Debug($"[PlayerBar] Unmuted, restored volume: {restoreVolume}");
            }
            else
            {
                _lastVolumeBeforeMute = Volume;
                _library.UpdateSettings(s => s.LastVolume = Volume);
                Volume = 0;
                Log.Debug($"[PlayerBar] Muted, saved volume: {_lastVolumeBeforeMute}");
            }
            OnVolumeChangeComplete();
        }));

        ToggleLikeCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack != null)
            {
                await _musicManager.ToggleLikeAsync(CurrentTrack);
                ShowLikeHint();
            }
        }, this.WhenAnyValue(x => x.HasTrack)));

        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack?.Url != null)
            {
                await _clipboard.SetTextAsync(CurrentTrack.Url);
                ShowCopyHint();
            }
        }, this.WhenAnyValue(x => x.HasTrack)));

        LoadFormatsCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadFormatsAsync));

        SwitchFormatCommand = CreateCommand(ReactiveCommand.CreateFromTask<StreamOption>(async option =>
        {
            if (option == null) return;
            foreach (var f in AvailableFormats) f.IsActive = false;
            option.IsActive = true;
            await _audio.SwitchQualityAsync(option.Container, (int)option.Bitrate);
        }));
    }

    #endregion

    #region Buffer Progress

    /// <summary>
    /// Обрабатывает обновление состояния буфера от AudioEngine.
    /// </summary>
    private void HandleBufferStateChanged(BufferState state)
    {
        Log.Debug($"[PlayerBarVM] HandleBufferStateChanged: progress={state.Progress:F1}%, " +
                  $"ranges={state.Ranges.Count}, suspended={_isSuspended}");

        // Если трек загружен локально — всегда 100%
        if (CurrentTrack?.IsDownloaded == true)
        {
            if (!IsFullyBuffered)
            {
                BufferProgressPercent = 100;
                BufferedRanges = [(0.0, 1.0)];
                IsFullyBuffered = true;
                this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
            }
            return;
        }

        BufferProgressPercent = state.Progress;
        IsFullyBuffered = state.IsFullyBuffered;

        // Обновляем ranges только если они реально изменились
        var newRanges = state.Ranges;

        if (!RangesEqual(BufferedRanges, newRanges))
        {
            Log.Debug($"[PlayerBarVM] Updating BufferedRanges: {newRanges.Count} ranges");
            BufferedRanges = newRanges;
            this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
            this.RaisePropertyChanged(nameof(BufferedRanges)); // Явно!
        }
        else
        {
            Log.Debug("[PlayerBarVM] Ranges unchanged, skipping update");
        }
    }

    /// <summary>
    /// Сравнивает два списка диапазонов для предотвращения лишних перерисовок.
    /// </summary>
    private static bool RangesEqual(
        IReadOnlyList<(double Start, double End)> a,
        IReadOnlyList<(double Start, double End)> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            // Используем epsilon для сравнения double
            if (Math.Abs(a[i].Start - b[i].Start) > 0.001 ||
                Math.Abs(a[i].End - b[i].End) > 0.001)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Принудительное обновление буфера (для resume и т.д.).
    /// </summary>
    private void ForceUpdateBufferProgress()
    {
        if (!HasTrack || CurrentTrack == null) return;

        if (CurrentTrack.IsDownloaded)
        {
            BufferProgressPercent = 100;
            BufferedRanges = [(0.0, 1.0)];
            IsFullyBuffered = true;
        }
        else
        {
            BufferProgressPercent = _audio.BufferProgress;
            BufferedRanges = _audio.GetBufferedRanges();
            IsFullyBuffered = _audio.IsFullyBuffered;
        }

        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    #endregion

    #region Helpers

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    #endregion

    #region Initialization

    private void OnLibraryInitialized()
    {
        var settings = _library.Settings;

        int newMax = settings.MaxVolumeLimit < 100 ? 100 : settings.MaxVolumeLimit;
        MaxVolume = newMax;

        int savedVolume = settings.LastVolume;
        if (savedVolume > 0 && savedVolume <= MaxVolume)
        {
            Volume = savedVolume;
            _lastVolumeBeforeMute = savedVolume;
        }
        else if (savedVolume > MaxVolume)
        {
            Volume = MaxVolume;
            _lastVolumeBeforeMute = MaxVolume;
        }
        else
        {
            Volume = 50;
            _lastVolumeBeforeMute = 50;
        }

        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;
        _audio.RepeatMode = RepeatMode;

        _audio.SetVolumeInstant(Volume);

        _isInitialized = true;
        RaiseVolumePropertiesChanged();
        UpdateQueueState();

        Log.Info($"[PlayerBar] Initialized from settings: MaxVol={MaxVolume}, Vol={Volume}, Repeat={RepeatMode}, Shuffle={ShuffleEnabled}");
    }

    #endregion

    #region Volume Helpers

    private void RaiseVolumePropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsVolumeLow));
        this.RaisePropertyChanged(nameof(IsVolumeMedium));
        this.RaisePropertyChanged(nameof(IsVolumeHigh));
        this.RaisePropertyChanged(nameof(IsVolumeBoosted));
        this.RaisePropertyChanged(nameof(VolumePercentBrush));
        this.RaisePropertyChanged(nameof(MuteTooltip));
    }

    #endregion

    #region Language Change Handler

    private void OnLanguageChanged(object? sender, string newLang)
    {
        this.RaisePropertyChanged(nameof(ShuffleTooltip));
        this.RaisePropertyChanged(nameof(PreviousTooltip));
        this.RaisePropertyChanged(nameof(NextTooltip));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));
        this.RaisePropertyChanged(nameof(RepeatTooltip));
        this.RaisePropertyChanged(nameof(LikeTooltip));
        this.RaisePropertyChanged(nameof(CopyTooltip));
        this.RaisePropertyChanged(nameof(MuteTooltip));
        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
        this.RaisePropertyChanged(nameof(DurationTooltip));
    }

    #endregion

    #region Format Loading

    private async Task LoadFormatsAsync()
    {
        if (CurrentTrack == null) return;

        try
        {
            string videoId = CurrentTrack.Id.Replace("yt_", "");
            var formats = await _youtube.GetStreamOptionsAsync(videoId);

            var (currentFormat, currentBitrate, _) = _audio.GetCurrentStreamInfo();
            var cachedFormats = StreamCacheManager.GetCachedFormats(CurrentTrack.Id);

            AvailableFormats.Clear();

            foreach (var f in formats)
            {
                f.IsDownloaded = cachedFormats.Any(cached =>
                    string.Equals(f.Container, cached.Container, StringComparison.OrdinalIgnoreCase) &&
                    (int)f.Bitrate == cached.Bitrate);

                if (!f.IsDownloaded)
                {
                    f.IsDownloaded = _cacheManager.IsFormatCached(CurrentTrack.Id, f.Container, (int)f.Bitrate);
                }

                f.IsActive = string.Equals(f.Codec, currentFormat, StringComparison.OrdinalIgnoreCase) &&
                             (int)f.Bitrate == currentBitrate;

                AvailableFormats.Add(f);
            }
            Log.Debug($"Loaded {AvailableFormats.Count} formats, {cachedFormats.Count} cached");
        }
        catch (Exception ex)
        {
            Log.Error($"LoadFormatsAsync error: {ex.Message}");
        }
    }

    private void OnFormatCached(string trackId, string container, int bitrate, bool isDownloaded)
    {
        if (CurrentTrack == null || CurrentTrack.Id != trackId)
            return;

        bool found = false;
        foreach (var format in AvailableFormats)
        {
            if (string.Equals(format.Container, container, StringComparison.OrdinalIgnoreCase) &&
                (int)format.Bitrate == bitrate)
            {
                format.IsDownloaded = isDownloaded;
                found = true;
                break;
            }
        }

        if (!found && AvailableFormats.Count > 0)
        {
            _ = LoadFormatsAsync();
        }
    }

    #endregion

    #region Hint Methods

    private async void ShowRepeatModeHint()
    {
        RepeatHintText = RepeatMode switch
        {
            RepeatMode.None => L.Get("Player_Repeat_Off", "Repeat Off"),
            RepeatMode.RepeatAll => L.Get("Player_Repeat_All", "Repeat Queue"),
            RepeatMode.RepeatOne => L.Get("Player_Repeat_One", "Repeat Track"),
            _ => ""
        };

        IsRepeatHintVisible = true;
        await Task.Delay(HintDisplayDurationMs);
        IsRepeatHintVisible = false;
    }

    private async void ShowLikeHint()
    {
        LikeHintText = IsLiked
            ? L.Get("Track_Added", "Added to Liked")
            : L.Get("Track_Removed", "Removed from Liked");

        IsLikeHintVisible = true;
        await Task.Delay(HintDisplayDurationMs);
        IsLikeHintVisible = false;
    }

    private async void ShowCopyHint()
    {
        IsCopyHighlighted = true;
        IsCopyHintVisible = true;

        await Task.Delay(CopyHighlightDurationMs);
        IsCopyHighlighted = false;

        await Task.Delay(HintDisplayDurationMs - CopyHighlightDurationMs);
        IsCopyHintVisible = false;
    }

    #endregion

    #region Private Handlers

    private void HandleMaxVolumeChanged(int newMax)
    {
        int oldMax = MaxVolume;
        MaxVolume = newMax;

        if (Volume > MaxVolume)
        {
            Volume = MaxVolume;
        }

        if (_lastVolumeBeforeMute > MaxVolume)
        {
            _lastVolumeBeforeMute = MaxVolume;
        }

        RaiseVolumePropertiesChanged();
        Log.Info($"[PlayerBar] MaxVolume changed: {oldMax} -> {newMax}");
    }

    private void HandleTrackChanged(TrackInfo? track)
    {
        CurrentTrack = track;
        HasTrack = track != null;

        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(SafeAuthor));
        this.RaisePropertyChanged(nameof(SafeThumbnail));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));
        this.RaisePropertyChanged(nameof(DurationTooltip));

        IsSeekBusy = true;
        _lastDownloadedBytes = 0;

        AvailableFormats.Clear();

        Position = TimeSpan.Zero;
        PositionSeconds = 0;

        if (track != null)
        {
            Duration = track.Duration;
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;

            var storedTrack = _library.GetTrack(track.Id);
            IsLiked = storedTrack?.IsLiked ?? track.IsLiked;

            // Сбрасываем буфер для нового трека
            if (track.IsDownloaded)
            {
                BufferProgressPercent = 100;
                BufferedRanges = [(0.0, 1.0)];
                IsFullyBuffered = true;
            }
            else
            {
                BufferProgressPercent = 0;
                BufferedRanges = [];
                IsFullyBuffered = false;
            }

            ShowStreamInfo = true;
            StreamInfo = L.Get("Player_StreamInfo_Loading", "Loading...");
        }
        else
        {
            Duration = TimeSpan.Zero;
            DurationSeconds = 1;
            PositionSeconds = 0;

            // Полный сброс буфера
            BufferProgressPercent = 0;
            BufferedRanges = [];
            IsFullyBuffered = false;

            ShowStreamInfo = false;
            StreamInfo = "";
            IsLiked = false;
        }

        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        var queue = _audio.Queue;
        TotalTracksInQueue = queue.Count;
        HasQueueToShuffle = queue.Count > 1;

        if (CurrentTrack != null)
        {
            CurrentTrackIndex = queue.ToList().FindIndex(t => t.Id == CurrentTrack.Id);
            if (CurrentTrackIndex < 0) CurrentTrackIndex = 0;
        }
        else
        {
            CurrentTrackIndex = 0;
        }

        this.RaisePropertyChanged(nameof(CurrentTrackIndexDisplay));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
    }

    private void SyncPlaybackState(bool isPlaying, bool isPaused)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
    }

    private void UpdateStreamInfo(AudioStreamInfo info)
    {
        if (CurrentTrack == null)
        {
            ShowStreamInfo = false;
            StreamInfo = "";
            return;
        }

        if (info.IsValid)
        {
            StreamInfo = info.FormatDisplay;
            Duration = TimeSpan.FromMilliseconds(info.DurationMs);
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;

            foreach (var f in AvailableFormats)
            {
                f.IsActive = string.Equals(f.Codec, info.Codec, StringComparison.OrdinalIgnoreCase) &&
                             (int)f.Bitrate == info.Bitrate;
            }
        }
        else
        {
            StreamInfo = L.Get("Player_StreamInfo_Loading", "Loading...");
        }

        ShowStreamInfo = true;
        this.RaisePropertyChanged(nameof(DurationTooltip));
    }

    private void UpdateDownloadSpeed()
    {
        if (!HasTrack || CurrentTrack?.IsDownloaded == true || IsFullyBuffered)
        {
            DownloadSpeedText = "";
            return;
        }

        var currentBytes = _audio.GetDownloadedBytes();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedCheck).TotalSeconds;

        if (elapsed >= 0.5 && _lastSpeedCheck != DateTime.MinValue)
        {
            var kbs = ((currentBytes - _lastDownloadedBytes) / elapsed) / 1024.0;
            DownloadSpeedText = kbs > 10
                ? (kbs >= 1024
                    ? string.Format(L.Get("Stream_Speed_Mb", "{0:F1} MB/s"), kbs / 1024)
                    : string.Format(L.Get("Stream_Speed_Kb", "{0:F0} KB/s"), kbs))
                : "";
        }

        _lastDownloadedBytes = currentBytes;
        _lastSpeedCheck = now;
    }

    private void FallbackPositionUpdate()
    {
        if (!HasTrack || _isSeeking || _justFinishedSeeking) return;

        var realDur = _audio.TotalDuration;

        if (Math.Abs(DurationSeconds - realDur.TotalSeconds) > 1 && realDur.TotalSeconds > 0)
        {
            Duration = realDur;
            DurationSeconds = Duration.TotalSeconds;
            this.RaisePropertyChanged(nameof(DurationTooltip));
        }

        if (IsPlaying)
        {
            Position = _audio.CurrentPosition;
            PositionSeconds = Position.TotalSeconds;
        }
    }

    #endregion

    #region Public Interaction

    /// <summary>
    /// Начинает операцию seek (перетаскивание ползунка).
    /// </summary>
    public void StartSeek()
    {
        _isSeeking = true;
        _justFinishedSeeking = false;

        _wasPlayingBeforeSeek = IsPlaying;
        if (_wasPlayingBeforeSeek)
        {
            _ = _audio.SetPlaybackStateAsync(false);
        }
    }

    /// <summary>
    /// Обновляет позицию во время seek.
    /// </summary>
    /// <param name="seconds">Новая позиция в секундах.</param>
    public void UpdateSeekPosition(double seconds)
    {
        if (!_isSeeking) return;
        seconds = Math.Clamp(seconds, 0, DurationSeconds);
        PositionSeconds = seconds;
        Position = TimeSpan.FromSeconds(seconds);
        this.RaisePropertyChanged(nameof(DurationTooltip));
    }

    /// <summary>
    /// Завершает операцию seek.
    /// </summary>
    public async void EndSeek()
    {
        if (!HasTrack)
        {
            _isSeeking = false;
            return;
        }

        double target = PositionSeconds;
        _isSeeking = false;
        _justFinishedSeeking = true;

        var delta = DateTime.UtcNow - _lastSeekTime;
        if (delta.TotalMilliseconds < SeekCooldownMs)
            await Task.Delay(SeekCooldownMs - (int)delta.TotalMilliseconds);
        _lastSeekTime = DateTime.UtcNow;

        IsSeekBusy = true;
        await _audio.SeekAsync(TimeSpan.FromSeconds(target));
        await Task.Delay(300);

        if (_wasPlayingBeforeSeek)
        {
            await _audio.SetPlaybackStateAsync(true);
        }

        IsSeekBusy = false;
        _justFinishedSeeking = false;
    }

    /// <summary>
    /// Отменяет операцию seek.
    /// </summary>
    public void CancelSeek()
    {
        _isSeeking = false;
        _justFinishedSeeking = false;

        if (_wasPlayingBeforeSeek)
        {
            _ = _audio.SetPlaybackStateAsync(true);
        }
    }

    /// <summary>
    /// Сохраняет громкость немедленно.
    /// </summary>
    public void OnVolumeChangeComplete()
    {
        _audio.SaveVolumeNow();
    }

    #endregion

    #region LifeCycle

    protected override void OnSuspend()
    {
        _isSuspended = true;
        _fallbackPositionTimer.Stop();
        _speedUpdateTimer.Stop();
        Log.Debug("[PlayerBar] Suspended");
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        _fallbackPositionTimer.Start();
        _speedUpdateTimer.Start();

        // Обновляем данные которые могли измениться
        FallbackPositionUpdate();
        ForceUpdateBufferProgress();

        Log.Debug("[PlayerBar] Resumed");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

            if (_isInitialized && Volume > 0)
            {
                _library.UpdateSettings(s => s.LastVolume = Volume);
            }

            _audio.SaveVolumeNow();
            _fallbackPositionTimer.Stop();
            _speedUpdateTimer.Stop();
        }
        base.Dispose(disposing);
    }

    #endregion
}