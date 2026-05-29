using System.Buffers;
using LMP.Core.Audio;
using LMP.Core.Models;

namespace LMP.Core.Services;

public sealed class DownloadService
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

    /// <summary>
    /// Запускает процесс загрузки трека.
    /// <para>Если трек полностью кэширован, выполняется мгновенный фоновый экспорт на диск без использования сетевых лимитов.</para>
    /// </summary>
    /// <param name="track">Объект информации о треке.</param>
    public void StartDownload(TrackInfo track)
    {
        lock (_lock)
        {
            if (_activeTasks.ContainsKey(track.Id) || track.IsDownloaded)
                return;
        }

        // Интеграция быстрого пути: Проверяем, есть ли трек в локальном дисковом кэше
        var cache = AudioSourceFactory.GlobalCache;
        if (cache != null && cache.IsTrackFullyCached(track.Id))
        {
            Log.Info($"[DownloadService] Track '{track.Title}' ({track.Id}) is fully cached. Promoting instantly, bypassing net queue.");
            
            lock (_lock)
            {
                _activeTasks[track.Id] = new DownloadTask { Progress = 0f };
            }

            Task.Run(async () =>
            {
                try
                {
                    OnProgress?.Invoke(track.Id, 0f);

                    // Экспортируем готовый файл кэша в Downloads
                    bool success = await cache.ExportTrackToDownloadsAsync(
                        track.Id,
                        async id => await _library.GetTrackAsync(id).ConfigureAwait(false),
                        async t => await _library.AddOrUpdateTrackAsync(t).ConfigureAwait(false),
                        CancellationToken.None).ConfigureAwait(false);

                    if (success)
                    {
                        var updatedTrack = await _library.GetTrackAsync(track.Id).ConfigureAwait(false);
                        if (updatedTrack != null)
                        {
                            track.IsDownloaded = true;
                            track.LocalPath = updatedTrack.LocalPath;
                            OnProgress?.Invoke(track.Id, 1.0f); // 100%
                            OnCompleted?.Invoke(track.Id, true, track.LocalPath);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[DownloadService] Instant promotion failed for {track.Id}: {ex.Message}. Falling back to queue.");
                }
                finally
                {
                    lock (_lock)
                    {
                        _activeTasks.Remove(track.Id);
                    }
                }

                // Если мгновенный экспорт сорвался (например, I/O ошибка), отправляем в обычную очередь докачки
                EnqueueNormalDownload(track);
            });

            return;
        }

        EnqueueNormalDownload(track);
    }

    /// <summary>
    /// Помещает задачу загрузки в стандартную очередь с ограничением параллелизма.
    /// </summary>
    private void EnqueueNormalDownload(TrackInfo track)
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
            await _downloadSemaphore.WaitAsync().ConfigureAwait(false);

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

                string? path = await _youtube.DownloadTrackAsync(track, progress, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(path))
                {
                    track.IsDownloaded = true;
                    track.LocalPath = path;
                    await _library.AddOrUpdateTrackAsync(track).ConfigureAwait(false);
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
                Log.Error($"[DownloadService] Failed to download {track.Id}: {ex.Message}");
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