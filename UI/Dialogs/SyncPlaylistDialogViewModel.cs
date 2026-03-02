using System.Reactive;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

public sealed class SyncPlaylistDialogViewModel : ViewModelBase
{
    public PlaylistSyncPreview Preview { get; }

    [Reactive] public int SelectedStrategyIndex { get; set; }

    public PlaylistSyncStrategy SelectedStrategy => SelectedStrategyIndex switch
    {
        0 => PlaylistSyncStrategy.Merge,
        1 => PlaylistSyncStrategy.ReplaceLocal,
        2 => PlaylistSyncStrategy.ReplaceCloud,
        _ => PlaylistSyncStrategy.Merge
    };

    [Reactive] public bool SyncName { get; set; } = true;
    [Reactive] public bool SyncDescription { get; set; } = true;
    [Reactive] public bool SyncThumbnail { get; set; }
    [Reactive] public bool SyncTracks { get; set; } = true;

    public bool HasNameDiff => Preview.NameDiffers;
    public bool HasDescDiff => Preview.DescriptionDiffers;
    public bool HasTrackDiff => Preview.TracksDiffer;

    public string TrackDiffSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (Preview.CommonTrackCount > 0)
                parts.Add(string.Format(SL["Playlist_SyncCommon"] ?? "common: {0}", Preview.CommonTrackCount));
            if (Preview.LocalOnlyTrackCount > 0)
                parts.Add(string.Format(SL["Playlist_SyncLocalOnly"] ?? "local only: {0}", Preview.LocalOnlyTrackCount));
            if (Preview.CloudOnlyTrackCount > 0)
                parts.Add(string.Format(SL["Playlist_SyncCloudOnly"] ?? "cloud only: {0}", Preview.CloudOnlyTrackCount));
            return string.Join("  •  ", parts);
        }
    }

    public bool CanSyncThumbnail => SelectedStrategy switch
    {
        PlaylistSyncStrategy.ReplaceCloud => !string.IsNullOrEmpty(Preview.LocalThumbnailUrl),
        PlaylistSyncStrategy.Merge or PlaylistSyncStrategy.ReplaceLocal => !string.IsNullOrEmpty(Preview.CloudThumbnailUrl),
        _ => false
    };

    public string StrategyDescription => SelectedStrategy switch
    {
        PlaylistSyncStrategy.Merge => SL["Playlist_SyncStrategy_MergeDesc"] ?? "Add missing tracks to both sides. No deletions.",
        PlaylistSyncStrategy.ReplaceLocal => SL["Playlist_SyncStrategy_ReplaceLocalDesc"] ?? "Replace local tracks with YouTube. Local-only tracks will be removed.",
        PlaylistSyncStrategy.ReplaceCloud => SL["Playlist_SyncStrategy_ReplaceCloudDesc"] ?? "Replace YouTube tracks with local. Cloud-only tracks will be removed.",
        _ => ""
    };

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<PlaylistSyncOptions?>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> SyncCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public SyncPlaylistDialogViewModel(PlaylistSyncPreview preview)
    {
        Preview = preview;

        SyncName = preview.NameDiffers;
        SyncDescription = preview.DescriptionDiffers;

        SyncThumbnail = !string.IsNullOrEmpty(preview.CloudThumbnailUrl) &&
                        !string.Equals(preview.CloudThumbnailUrl, preview.LocalThumbnailUrl,
                                      StringComparison.Ordinal);

        SyncTracks = preview.TracksDiffer;

        SyncCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var result = new PlaylistSyncOptions
            {
                Strategy = SelectedStrategy,
                SyncName = SyncName,
                SyncDescription = SyncDescription,
                SyncThumbnail = SyncThumbnail,
                SyncTracks = SyncTracks
            };
            OnResult?.Invoke(result);
        }));

        CancelCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            OnResult?.Invoke(null);
        }));

        this.WhenAnyValue(x => x.SelectedStrategyIndex)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(StrategyDescription));
                this.RaisePropertyChanged(nameof(CanSyncThumbnail));
            });
    }
}