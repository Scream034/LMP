// ViewModels/PlayerBarViewModel.cs
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;

namespace MyLiteMusicPlayer.ViewModels;

public class PlayerBarViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DispatcherTimer _positionTimer;
    private bool _isSeeking;

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public float Volume { get; set; }
    [Reactive] public bool IsMuted { get; private set; }
    [Reactive] public bool ShuffleEnabled { get; set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }
    [Reactive] public bool IsLiked { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }

    // Commands
    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }

    private float _volumeBeforeMute;

    public PlayerBarViewModel(AudioEngine audio, LibraryService library)
    {
        _audio = audio;
        _library = library;

        Volume = _audio.GetVolume();
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        // Position update timer
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePosition();
        _positionTimer.Start();

        // Subscribe to track changes
        Observable.FromEvent<TrackInfo>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnTrackChanged);

        Observable.FromEvent(
                h => _audio.OnPlaybackStopped += h,
                h => _audio.OnPlaybackStopped -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => OnPlaybackStopped());

        // Seek when slider changes (debounced)
        this.WhenAnyValue(x => x.PositionSeconds)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .Where(_ => _isSeeking)
            .Subscribe(pos =>
            {
                _audio.Seek(TimeSpan.FromSeconds(pos));
                _isSeeking = false;
            });

        // Volume binding
        this.WhenAnyValue(x => x.Volume)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .Subscribe(v =>
            {
                _audio.SetVolume(v);
                IsMuted = v == 0;
            });

        // Commands
        var hasTrack = this.WhenAnyValue(x => x.HasTrack);

        PlayPauseCommand = ReactiveCommand.Create(() =>
        {
            _audio.TogglePlayPause();
            UpdatePlayState();
        }, hasTrack);

        PreviousCommand = ReactiveCommand.Create(() =>
        {
            _audio.PlayPrevious();
        }, hasTrack);

        NextCommand = ReactiveCommand.Create(() =>
        {
            _audio.PlayNext();
        }, hasTrack);

        ToggleShuffleCommand = ReactiveCommand.Create(() =>
        {
            ShuffleEnabled = !ShuffleEnabled;
            _audio.ShuffleEnabled = ShuffleEnabled;
        });

        ToggleRepeatCommand = ReactiveCommand.Create(() =>
        {
            RepeatMode = RepeatMode switch
            {
                RepeatMode.None => RepeatMode.RepeatAll,
                RepeatMode.RepeatAll => RepeatMode.RepeatOne,
                RepeatMode.RepeatOne => RepeatMode.None,
                _ => RepeatMode.None
            };
            _audio.RepeatMode = RepeatMode;
        });

        ToggleLikeCommand = ReactiveCommand.Create(() =>
        {
            if (CurrentTrack != null)
            {
                _library.ToggleLike(CurrentTrack);
                IsLiked = CurrentTrack.IsLiked;
            }
        }, hasTrack);

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            if (IsMuted)
            {
                Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 0.5f;
            }
            else
            {
                _volumeBeforeMute = Volume;
                Volume = 0;
            }
        });
    }

    private void OnTrackChanged(TrackInfo track)
    {
        CurrentTrack = track;
        HasTrack = true;
        Duration = _audio.TotalDuration;
        DurationSeconds = Duration.TotalSeconds;
        IsLiked = track.IsLiked;
        UpdatePlayState();
    }

    private void OnPlaybackStopped()
    {
        IsPlaying = false;
        IsPaused = false;
        CurrentTrack = null;
        HasTrack = false;
        Position = TimeSpan.Zero;
        PositionSeconds = 0;
    }

    private void UpdatePosition()
    {
        if (HasTrack && !_isSeeking)
        {
            Position = _audio.CurrentPosition;
            PositionSeconds = Position.TotalSeconds;
            
            // Update duration if it changed (stream loaded)
            if (Math.Abs(DurationSeconds - _audio.TotalDuration.TotalSeconds) > 1)
            {
                Duration = _audio.TotalDuration;
                DurationSeconds = Duration.TotalSeconds;
            }
        }
        UpdatePlayState();
    }

    private void UpdatePlayState()
    {
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
    }

    public void StartSeek() => _isSeeking = true;
    public void EndSeek() => _isSeeking = false;

    public void Dispose()
    {
        _positionTimer.Stop();
    }
}