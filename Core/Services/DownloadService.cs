using LMP.Core.Models;

namespace LMP.Core.Services;

public class DownloadService
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    private readonly Dictionary<string, DownloadTask> _activeTasks = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(3);

    public event Action<string, float>? OnProgress;
    public event Action<string, bool, string?>? OnCompleted;

    public DownloadService(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
    }

    public bool IsDownloading(string trackId)
    {
        lock (_lock)
        {
            return _activeTasks.ContainsKey(trackId);
        }
    }

    public float GetProgress(string trackId)
    {
        lock (_lock)
        {
            return _activeTasks.TryGetValue(trackId, out var task) ? task.Progress : 0f;
        }
    }

    public int ActiveDownloadsCount
    {
        get
        {
            lock (_lock)
            {
                return _activeTasks.Count;
            }
        }
    }

    public void StartDownload(TrackInfo track)
    {
        lock (_lock)
        {
            if (_activeTasks.ContainsKey(track.Id) || track.IsDownloaded)
                return;

            var cts = new CancellationTokenSource();
            _activeTasks[track.Id] = new DownloadTask { CancellationSource = cts };
        }

        Task.Run(async () =>
        {
            await _downloadSemaphore.WaitAsync();

            try
            {
                var progress = new Progress<float>(p =>
                {
                    lock (_lock)
                    {
                        if (_activeTasks.TryGetValue(track.Id, out var task))
                            task.Progress = p;
                    }
                    OnProgress?.Invoke(track.Id, p);
                });

                CancellationToken ct;
                lock (_lock)
                {
                    if (!_activeTasks.TryGetValue(track.Id, out var task))
                        return;
                    ct = task.CancellationSource.Token;
                }

                string? path = await _youtube.DownloadTrackAsync(track, progress, ct);

                if (!string.IsNullOrEmpty(path))
                {
                    track.IsDownloaded = true;
                    track.LocalPath = path;
                    _library.AddOrUpdateTrack(track);
                    OnCompleted?.Invoke(track.Id, true, path);
                }
                else
                {
                    OnCompleted?.Invoke(track.Id, false, null);
                }
            }
            catch (OperationCanceledException)
            {
                OnCompleted?.Invoke(track.Id, false, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Download error: {ex.Message}\n{ex.StackTrace}");
                OnCompleted?.Invoke(track.Id, false, null);
            }
            finally
            {
                lock (_lock)
                {
                    _activeTasks.Remove(track.Id);
                }
                _downloadSemaphore.Release();
            }
        });
    }

    public void CancelDownload(string trackId)
    {
        lock (_lock)
        {
            if (_activeTasks.TryGetValue(trackId, out var task))
            {
                task.CancellationSource.Cancel();
            }
        }
    }

    public void CancelAllDownloads()
    {
        lock (_lock)
        {
            foreach (var task in _activeTasks.Values)
            {
                task.CancellationSource.Cancel();
            }
        }
    }

    private class DownloadTask
    {
        public float Progress { get; set; }
        public CancellationTokenSource CancellationSource { get; set; } = new();
    }
}

