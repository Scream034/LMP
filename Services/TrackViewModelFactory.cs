using System.Runtime.CompilerServices;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.ViewModels;

namespace MyLiteMusicPlayer.Services;

public class TrackViewModelFactory(
    AudioEngine audio,
    LibraryService library,
    DownloadService downloads,
    MusicLibraryManager manager)
{
    private readonly AudioEngine _audio = audio;
    private readonly LibraryService _library = library;
    private readonly DownloadService _downloads = downloads;
    private readonly MusicLibraryManager _manager = manager;

    // Weak reference cache: ViewModels are automatically collected when TrackInfo is collected.
    // This prevents memory leaks while ensuring we reuse VMs for the same TrackInfo object instance.
    private readonly ConditionalWeakTable<TrackInfo, TrackItemViewModel> _cache = new();

    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? onPlay = null)
    {
        // Try to get existing VM or create a new one
        if (!_cache.TryGetValue(track, out var vm))
        {
            vm = new TrackItemViewModel(track, _audio, _library, _downloads, _manager, onPlay);
            _cache.Add(track, vm);
        }
        else
        {
            // Update the play action if the context changed but we are reusing the VM
            vm.UpdatePlayAction(onPlay);
        }

        return vm;
    }
}