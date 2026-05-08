using System.ComponentModel;
using System.Windows.Input;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Audio;
using LMP.Core.Helpers;
using LMP.UI.Controls;

namespace LMP.Features.Shared;

public sealed class TrackItemViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly MusicLibraryManager _manager;
    private readonly DownloadService _downloads;
    private readonly DialogService _dialog;
    private Action<TrackInfo>? _onPlay;

    public TrackInfo Track { get; }
    public bool IsDisposed { get; private set; }

    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;

    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    [Reactive] public bool IsActive { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsDownloading { get; private set; }
    [Reactive] public float DownloadProgress { get; private set; }
    [Reactive] public bool IsMenuOpen { get; set; }
    [Reactive] public bool IsSelected { get; set; }
    [Reactive] public bool IsPlaylistContext { get; set; }
    [Reactive] public bool IsQueueContext { get; set; }

    public bool ShowAddToQueue => !IsQueueContext;

    public string DownloadStatusText
    {
        get
        {
            if (Track.IsDownloaded) return L["Track_Downloaded"] ?? "Downloaded";
            if (Track.IsCached) return L["Track_SaveToFolder"] ?? "Save to folder";
            return L["Track_Download"] ?? "Download";
        }
    }

    public Action<TrackInfo>? StartRadioAction { get; set; }
    public Action<TrackInfo>? RemoveFromPlaylistAction { get; set; }
    public string? SourceContextId { get; set; }

    // ICommand вместо ReactiveCommand: ~6x меньше объектов на каждую команду.
    public ICommand PlayCommand { get; }
    public ICommand ToggleLikeCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand StartRadioCommand { get; }
    public ICommand SaveToDownloadsCommand { get; }
    public ICommand RemoveFromPlaylistCommand { get; }
    public ICommand RemoveFromQueueCommand { get; }
    public ICommand AddToPlaylistCommand { get; }
    public ICommand CopyLinkCommand { get; }

    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        DialogService dialog,
        Action<TrackInfo>? onPlay = null)
    {
        Track = track;
        _audio = audio;
        _manager = manager;
        _downloads = downloads;
        _dialog = dialog;
        _onPlay = onPlay;

        PlayCommand = new TrackAsyncCommand(PlayAsync);
        ToggleLikeCommand = new TrackAsyncCommand(() => _manager.ToggleLikeAsync(Track));
        AddToQueueCommand = new TrackSyncCommand(() => _audio.Enqueue(Track));
        StartRadioCommand = new TrackSyncCommand(() => StartRadioAction?.Invoke(Track));
        SaveToDownloadsCommand = new TrackAsyncCommand(SaveToDownloadsAsync);
        AddToPlaylistCommand = new TrackAsyncCommand(AddToPlaylistAsync);
        CopyLinkCommand = new TrackAsyncCommand(CopyLinkAsync);

        RemoveFromPlaylistCommand = new TrackSyncCommand(() =>
        {
            if (IsPlaylistContext) RemoveFromPlaylistAction?.Invoke(Track);
        });

        RemoveFromQueueCommand = new TrackSyncCommand(() =>
        {
            if (IsQueueContext) _audio.RemoveFromQueue(Track);
        });

        Track.PropertyChanged += OnTrackPropertyChanged;

        MemoryDiagnostics.TrackInstance("TrackVM.Created");
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Track.IsDownloaded) or nameof(Track.IsCached))
            this.RaisePropertyChanged(nameof(DownloadStatusText));
    }

    private async Task PlayAsync()
    {
        if (_audio.CurrentTrack?.Id == Id)
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        else
            _onPlay?.Invoke(Track);
    }

    private async Task SaveToDownloadsAsync()
    {
        if (Track.IsDownloaded) return;

        if (Track.IsCached)
        {
            var cache = AudioSourceFactory.GlobalCache;
            if (cache == null) return;

            bool success = await cache.ExportTrackToDownloadsAsync(
                Track.Id,
                async id => await Program.Services.GetRequiredService<LibraryService>().GetTrackAsync(id),
                async t => await Program.Services.GetRequiredService<LibraryService>().AddOrUpdateTrackAsync(t));

            if (success) Track.IsDownloaded = true;
        }
        else
        {
            _downloads.StartDownload(Track);
        }
    }

    /// <summary>
    /// Вызывается Smart Parent-ом при смене трека или состояния воспроизведения. O(1).
    /// </summary>
    public void SetActive(bool isActive, bool isPlaying)
    {
        IsActive = isActive;
        IsPlaying = isActive && isPlaying;
    }

    /// <summary>
    /// Вызывается Smart Parent-ом при обновлении прогресса загрузки. O(1).
    /// </summary>
    public void SetDownloadState(bool isDownloading, float progress)
    {
        IsDownloading = isDownloading;
        DownloadProgress = progress;
        if (!isDownloading) this.RaisePropertyChanged(nameof(DownloadStatusText));
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay) => _onPlay = onPlay;

    private async Task AddToPlaylistAsync()
    {
        var selectedIds = await _dialog.ShowAddToPlaylistDialogAsync(Track);
        if (selectedIds.Count == 0) return;

        foreach (var playlistId in selectedIds)
            await _manager.AddTrackToPlaylistAsync(playlistId, Track);
    }

    /// <summary>
    /// Копирует ссылку на трек и показывает hint через CopyHintService.
    /// Anchor не передаём — TrackListControl передаёт его через перегрузку.
    /// </summary>
    private async Task CopyLinkAsync()
    {
        if (IsDisposed) return;

        var url = Track.Url;
        if (string.IsNullOrEmpty(url))
            url = $"https://www.youtube.com/watch?v={Track.GetRawId()}";

        if (string.IsNullOrEmpty(url))
        {
            CopyHintService.Instance.Show(
                L["Track_CopyLink_NoUrl"] ?? "No link available",
                CopyHintKind.Warning,
                null);
            return;
        }

        await Clipboard.SetTextAsync(url);

        CopyHintService.Instance.Show(
            L["Track_Copied"] ?? "Copied!",
            CopyHintKind.Success,
            null); // ИСПОЛЬЗУЕМ ДЕФОЛТНОЕ ПОЛОЖЕНИЕ
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            Track.PropertyChanged -= OnTrackPropertyChanged;
            _onPlay = null;
            StartRadioAction = null;
            RemoveFromPlaylistAction = null;
            MemoryDiagnostics.UntrackInstance("TrackVM.Created");
        }
        base.Dispose(disposing);
        IsDisposed = true;
    }
}