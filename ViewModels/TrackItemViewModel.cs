// ViewModels/TrackItemViewModel.cs
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class TrackItemViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;

    public TrackInfo Track { get; }

    [Reactive] public bool IsPlaying { get; set; }
    [Reactive] public bool IsCurrentTrack { get; set; }
    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDownloading { get; set; }
    [Reactive] public float DownloadProgress { get; set; }
    [Reactive] public bool IsHovered { get; set; }

    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;
    public bool IsDownloaded => Track.IsDownloaded;

    // Commands
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> StartRadioCommand { get; }

    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        Action<TrackInfo>? onPlay = null,
        Action<TrackInfo>? onRadio = null)
    {
        Track = track;
        _audio = audio;
        _library = library;
        _downloads = downloads;

        IsLiked = track.IsLiked;

        // Subscribe to playback changes
        // FIX: Проверяем на null, т.к. при остановке может прийти null
        Observable.FromEvent<TrackInfo>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                // FIX: t может быть null при остановке воспроизведения
                if (t == null)
                {
                    IsCurrentTrack = false;
                    IsPlaying = false;
                }
                else
                {
                    IsCurrentTrack = t.Id == Track.Id;
                    IsPlaying = IsCurrentTrack && _audio.IsPlaying;
                }
            });

        // Subscribe to download progress
        Observable.FromEvent<Action<string, float>, (string, float)>(
                h => (id, p) => h((id, p)),
                h => _downloads.OnProgress += h,
                h => _downloads.OnProgress -= h)
            .Where(x => x.Item1 == Track.Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                IsDownloading = true;
                DownloadProgress = x.Item2;
            });

        // Commands
        PlayCommand = ReactiveCommand.Create(() =>
        {
            if (onPlay != null)
                onPlay(Track);
            else
                _audio.PlayTrack(Track);
        });

        ToggleLikeCommand = ReactiveCommand.Create(() =>
        {
            _library.ToggleLike(Track);
            IsLiked = Track.IsLiked;
        });

        AddToQueueCommand = ReactiveCommand.Create(() =>
        {
            _audio.Enqueue(Track);
        });

        var canDownload = this.WhenAnyValue(x => x.IsDownloading, d => !d);
        DownloadCommand = ReactiveCommand.Create(() =>
        {
            if (!Track.IsDownloaded)
                _downloads.StartDownload(Track);
        }, canDownload);

        StartRadioCommand = ReactiveCommand.Create(() =>
        {
            onRadio?.Invoke(Track);
        });
    }
}