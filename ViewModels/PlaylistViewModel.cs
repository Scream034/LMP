// ViewModels/PlaylistViewModel.cs
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

public class PlaylistViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;

    private string? _playlistId;

    [Reactive] public string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public string? ThumbnailUrl { get; private set; }
    [Reactive] public int TrackCount { get; private set; }
    [Reactive] public TimeSpan TotalDuration { get; private set; }
    [Reactive] public bool CanEdit { get; private set; }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }

    public PlaylistViewModel(
        LibraryService library,
        AudioEngine audio,
        DownloadService downloads)
    {
        _library = library;
        _audio = audio;
        _downloads = downloads;

        var hasTracks = this.WhenAnyValue(x => x.TrackCount, c => c > 0);

        PlayAllCommand = ReactiveCommand.Create(() =>
        {
            _audio.ClearQueue();
            _audio.ShuffleEnabled = false;
            foreach (var item in Tracks)
                _audio.Enqueue(item.Track);
        }, hasTracks);

        ShufflePlayCommand = ReactiveCommand.Create(() =>
        {
            _audio.ClearQueue();
            _audio.ShuffleEnabled = true;
            foreach (var item in Tracks)
                _audio.Enqueue(item.Track);
        }, hasTracks);

        DownloadAllCommand = ReactiveCommand.Create(() =>
        {
            foreach (var item in Tracks.Where(t => !t.IsDownloaded))
            {
                _downloads.StartDownload(item.Track);
            }
        }, hasTracks);
    }

    public void LoadPlaylist(string playlistId)
    {
        _playlistId = playlistId;
        var playlist = _library.GetPlaylist(playlistId);

        if (playlist == null) return;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        CanEdit = playlist.IsLocal && playlist.Id != "liked";

        Tracks.Clear();
        var tracks = _library.GetPlaylistTracks(playlistId);

        foreach (var track in tracks)
        {
            Tracks.Add(new TrackItemViewModel(track, _audio, _library, _downloads,
                onPlay: t => PlayFromPlaylist(t)));
        }

        TrackCount = Tracks.Count;
        TotalDuration = TimeSpan.FromSeconds(tracks.Sum(t => t.Duration.TotalSeconds));
    }

    private void PlayFromPlaylist(TrackInfo track)
    {
        _audio.ClearQueue();
        _audio.PlayTrack(track);

        bool found = false;
        foreach (var item in Tracks)
        {
            if (found)
                _audio.Enqueue(item.Track);
            if (item.Track.Id == track.Id)
                found = true;
        }

        _library.AddToRecentlyPlayed(track);
    }
}