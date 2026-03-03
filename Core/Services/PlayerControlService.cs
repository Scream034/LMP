using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LMP.Core.Audio;
using LMP.Core.Helpers;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Единый координатор управления воспроизведением.
/// Предоставляет реактивные свойства и команды, синхронизированные между всеми UI компонентами.
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Является единственным подписчиком на события AudioEngine для state tracking</item>
///   <item>Предоставляет BehaviorSubject-based IObservable для PlayerBar, TrayIcon, MediaKeys</item>
///   <item>Работает независимо от suspend/resume состояния окна</item>
/// </list>
/// 
/// <para><b>ForceSync:</b></para>
/// <para>Не публикует повторные значения в CurrentTrack если объект тот же (по Id).
/// Это предотвращает ложный TrackReset при восстановлении из трея.</para>
/// </summary>
public sealed class PlayerControlService : IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;

    private readonly BehaviorSubject<bool> _isPlayingSubject;
    private readonly BehaviorSubject<bool> _isPausedSubject;
    private readonly BehaviorSubject<bool> _isLoadingSubject;
    private readonly BehaviorSubject<TrackInfo?> _currentTrackSubject;
    private readonly BehaviorSubject<RepeatMode> _repeatModeSubject;
    private readonly BehaviorSubject<bool> _shuffleEnabledSubject;
    private readonly BehaviorSubject<int> _queueCountSubject;

    /// <summary>
    /// Сигнал принудительной синхронизации. Подписчики должны обновить
    /// все свои состояния без полного TrackReset.
    /// Значение — true при вызове ForceSync.
    /// </summary>
    private readonly Subject<Unit> _forceSyncSubject = new();

    private bool _disposed;

    public PlayerControlService(AudioEngine audio, LibraryService library)
    {
        _audio = audio;
        _library = library;

        _isPlayingSubject = new BehaviorSubject<bool>(_audio.IsPlaying);
        _isPausedSubject = new BehaviorSubject<bool>(_audio.IsPaused);
        _isLoadingSubject = new BehaviorSubject<bool>(_audio.IsLoading);
        _currentTrackSubject = new BehaviorSubject<TrackInfo?>(_audio.CurrentTrack);
        _repeatModeSubject = new BehaviorSubject<RepeatMode>(_audio.RepeatMode);
        _shuffleEnabledSubject = new BehaviorSubject<bool>(_audio.ShuffleEnabled);
        _queueCountSubject = new BehaviorSubject<int>(_audio.Queue.Count);

        _audio.OnPlaybackStateChanged += OnPlaybackStateChanged;
        _audio.OnTrackChanged += OnTrackChanged;
        _audio.OnQueueChanged += OnQueueChanged;
        _audio.OnLoadingStateChanged += OnLoadingStateChanged;

        Log.Debug("[PlayerControl] Service initialized");
    }

    #region Properties

    public bool IsPlaying => _isPlayingSubject.Value;
    public bool IsPaused => _isPausedSubject.Value;
    public bool IsLoading => _isLoadingSubject.Value;
    public TrackInfo? CurrentTrack => _currentTrackSubject.Value;
    public RepeatMode RepeatMode => _repeatModeSubject.Value;
    public bool ShuffleEnabled => _shuffleEnabledSubject.Value;
    public bool HasTrack => _currentTrackSubject.Value != null;
    public int QueueCount => _queueCountSubject.Value;

    #endregion

    #region Observables

    public IObservable<bool> IsPlayingObservable => _isPlayingSubject.AsObservable();
    public IObservable<bool> IsPausedObservable => _isPausedSubject.AsObservable();
    public IObservable<bool> IsLoadingObservable => _isLoadingSubject.AsObservable();

    /// <summary>
    /// Поток изменений текущего трека.
    /// Публикуется ТОЛЬКО при реальной смене трека (другой Id).
    /// При ForceSync не переиздаётся — используйте ForceSyncObservable.
    /// </summary>
    public IObservable<TrackInfo?> CurrentTrackObservable => _currentTrackSubject.AsObservable();

    public IObservable<RepeatMode> RepeatModeObservable => _repeatModeSubject.AsObservable();
    public IObservable<bool> ShuffleEnabledObservable => _shuffleEnabledSubject.AsObservable();
    public IObservable<int> QueueCountObservable => _queueCountSubject.AsObservable();

    public IObservable<(bool IsPlaying, bool IsPaused)> PlaybackStateObservable =>
        _isPlayingSubject.CombineLatest(_isPausedSubject, (p, u) => (p, u));

    /// <summary>
    /// Сигнал для подписчиков: "обнови все состояния без TrackReset".
    /// Вызывается при восстановлении из трея / minimize.
    /// </summary>
    public IObservable<Unit> ForceSyncObservable => _forceSyncSubject.AsObservable();

    #endregion

    #region Commands

    public async Task PlayPauseAsync()
    {
        try
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayPause error: {ex.Message}");
        }
    }

    public async Task NextAsync()
    {
        try
        {
            await _audio.PlayNextAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Next error: {ex.Message}");
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            await _audio.PlayPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Previous error: {ex.Message}");
        }
    }

    public void ToggleRepeat()
    {
        var newMode = _audio.RepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.None,
            _ => RepeatMode.None
        };

        _audio.RepeatMode = newMode;
        _library.UpdateSettings(s => s.RepeatMode = newMode);
        _repeatModeSubject.OnNext(newMode);

        Log.Debug($"[PlayerControl] RepeatMode changed to {newMode}");
    }

    public void ShuffleQueue()
    {
        _audio.ShuffleQueue();
        Log.Debug("[PlayerControl] Queue shuffled");
    }

    public void ToggleAutoShuffle()
    {
        bool newState = !_audio.ShuffleEnabled;
        _audio.ShuffleEnabled = newState;
        _library.UpdateSettings(s => s.ShuffleEnabled = newState);
        _shuffleEnabledSubject.OnNext(newState);

        Log.Debug($"[PlayerControl] AutoShuffle changed to {newState}");
    }

    #endregion

    #region AudioEngine Event Handlers

    private void OnPlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        _isPlayingSubject.OnNext(isPlaying);
        _isPausedSubject.OnNext(isPaused);
    }

    private void OnTrackChanged(TrackInfo? track)
    {
        _currentTrackSubject.OnNext(track);
    }

    private void OnQueueChanged()
    {
        _queueCountSubject.OnNext(_audio.Queue.Count);
    }

    private void OnLoadingStateChanged(bool isLoading)
    {
        _isLoadingSubject.OnNext(isLoading);
    }

    #endregion

    #region Sync

    /// <summary>
    /// Принудительная синхронизация всех состояний при восстановлении из трея.
    /// 
    /// <para><b>ВАЖНО:</b> НЕ переиздаёт CurrentTrack если трек тот же самый.
    /// Это предотвращает ложный BeginTrackReset → замораживание UI.</para>
    /// 
    /// <para>Вместо этого публикует ForceSyncObservable, на который PlayerBarViewModel
    /// подписывается для мягкого обновления (позиция, буфер, стрим-инфо).</para>
    /// </summary>
    public void ForceSync()
    {
        _isPlayingSubject.OnNext(_audio.IsPlaying);
        _isPausedSubject.OnNext(_audio.IsPaused);
        _isLoadingSubject.OnNext(_audio.IsLoading);
        _repeatModeSubject.OnNext(_audio.RepeatMode);
        _shuffleEnabledSubject.OnNext(_audio.ShuffleEnabled);
        _queueCountSubject.OnNext(_audio.Queue.Count);

        // НЕ переиздаём CurrentTrack — это вызвало бы HandleTrackChanged → BeginTrackReset
        // Вместо этого сигнализируем "мягкую" синхронизацию
        _forceSyncSubject.OnNext(Unit.Default);

        Log.Debug("[PlayerControl] Forced sync completed (soft, no track reset)");
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audio.OnPlaybackStateChanged -= OnPlaybackStateChanged;
        _audio.OnTrackChanged -= OnTrackChanged;
        _audio.OnQueueChanged -= OnQueueChanged;
        _audio.OnLoadingStateChanged -= OnLoadingStateChanged;

        _isPlayingSubject.Dispose();
        _isPausedSubject.Dispose();
        _isLoadingSubject.Dispose();
        _currentTrackSubject.Dispose();
        _repeatModeSubject.Dispose();
        _shuffleEnabledSubject.Dispose();
        _queueCountSubject.Dispose();
        _forceSyncSubject.Dispose();

        Log.Debug("[PlayerControl] Service disposed");
    }

    #endregion
}