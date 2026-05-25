using System.Reactive;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Microsoft.Extensions.DependencyInjection;
using LMP.Features.Shared;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Backends;
using LMP.Core.Helpers;

namespace LMP.Features.Debug;

/// <summary>
/// ViewModel для Debug-окна. Содержит логику YouTube/Memory/Audio вкладок
/// и предоставляет <see cref="TestRunner"/> для вкладки Tests.
/// </summary>
public sealed class DebugViewModel : ViewModelBase, IDisposable
{
    private readonly YoutubeProvider _youtube;

    // ═══════════════════════════════════════════════════════════════
    // TEST RUNNER (вкладка Tests)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>ViewModel для вкладки тестов. Создаётся лениво при первом обращении.</summary>
    public TestRunnerViewModel TestRunner { get; } = new();

    // ═══════════════════════════════════════════════════════════════
    // EXISTING PROPERTIES (YouTube / Memory / Audio)
    // ═══════════════════════════════════════════════════════════════

    [Reactive] public string LogOutput { get; set; } = "Debug Session Started...\n";
    [Reactive] public string SearchQuery { get; set; } = "Linkin Park";
    [Reactive] public bool IsBusy { get; set; }

    [Reactive] public string AudioTestInput { get; set; } = "aG_i7fvGSXU";
    [Reactive] public int AudioTestDuration { get; set; } = 10;
    [Reactive] public bool IsAudioPlaying { get; set; }

    private CancellationTokenSource? _audioTestCts;
    private AudioPlayer? _testPlayer;
    private AudioCacheManager? _testCacheManager;

    public ReactiveCommand<Unit, Unit> GetLikedVideosCommand { get; }
    public ReactiveCommand<Unit, Unit> GetLikedMusicCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchVideosCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchMusicCommand { get; }
    public ReactiveCommand<Unit, string> ClearLogCommand { get; }

    public ReactiveCommand<Unit, Unit> DumpMemoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceGcCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCachesCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckVmLeaksCommand { get; }

    public ReactiveCommand<Unit, Unit> PlayYoutubeAudioCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayYoutubeWithCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> StopAudioTestCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowCacheStatsCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAudioCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> TestLocalFileCommand { get; }

    public DebugViewModel()
    {
        _youtube = Program.Services.GetRequiredService<YoutubeProvider>();

        GetLikedVideosCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteGetLikedVideos));
        GetLikedMusicCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteGetLikedMusic));
        SearchVideosCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteSearchVideos));
        SearchMusicCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteSearchMusic));
        ClearLogCommand = CreateCommand(ReactiveCommand.Create(() => LogOutput = ""));

        DumpMemoryCommand = CreateCommand(ReactiveCommand.Create(ExecuteDumpMemory));
        ForceGcCommand = CreateCommand(ReactiveCommand.Create(ExecuteForceGc));
        ClearCachesCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteClearCaches));

        CheckVmLeaksCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();

            var cacheField = vmFactory.GetType().GetField("_cache",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (cacheField?.GetValue(vmFactory) is System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<TrackItemViewModel>> cache)
            {
                int alive = 0, dead = 0, disposed = 0;

                foreach (var kvp in cache)
                {
                    if (kvp.Value.TryGetTarget(out var vm))
                    {
                        if (vm.IsDisposed) disposed++;
                        else alive++;
                    }
                    else dead++;
                }

                AppendLog($"\n--- TRACK VM CACHE ---");
                AppendLog($"Total entries: {cache.Count}");
                AppendLog($"Alive (not disposed): {alive}");
                AppendLog($"Disposed but in cache: {disposed}");
                AppendLog($"Dead (collected): {dead}");
                AppendLog($"--- END ---\n");
            }
        }));

        PlayYoutubeAudioCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecutePlayYoutubeAudio));
        PlayYoutubeWithCacheCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecutePlayYoutubeWithCache));
        StopAudioTestCommand = CreateCommand(ReactiveCommand.Create(ExecuteStopAudioTest));
        ShowCacheStatsCommand = CreateCommand(ReactiveCommand.Create(ExecuteShowCacheStats));
        ClearAudioCacheCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteClearAudioCache));
        TestLocalFileCommand = CreateCommand(ReactiveCommand.CreateFromTask(ExecuteTestLocalFile));
    }

    // ═══════════════════════════════════════════════════════════════
    // Audio methods — без изменений, оставлены как были
    // ═══════════════════════════════════════════════════════════════

    private async Task ExecutePlayYoutubeAudio() =>
        await PlayAudioTestAsync(useCache: false);

    private async Task ExecutePlayYoutubeWithCache() =>
        await PlayAudioTestAsync(useCache: true);

    private async Task PlayAudioTestAsync(bool useCache)
    {
        if (IsAudioPlaying)
        {
            AppendLog("⚠️ Audio test already running. Stop it first.");
            return;
        }

        IsBusy = true;
        IsAudioPlaying = true;
        _audioTestCts = new CancellationTokenSource();

        var cacheMode = useCache ? "WITH CACHE" : "NO CACHE";
        AppendLog($"\n╔════════════════════════════════════════╗");
        AppendLog($"║  🎵 AUDIO TEST ({cacheMode})");
        AppendLog($"╚════════════════════════════════════════╝");
        AppendLog($"  Input: {AudioTestInput}");
        AppendLog($"  Duration: {AudioTestDuration}s");

        try
        {
            var videoId = ExtractVideoId(AudioTestInput);
            if (string.IsNullOrEmpty(videoId))
            {
                AppendLog($"  ❌ Invalid YouTube URL/ID");
                return;
            }
            AppendLog($"  Video ID: {videoId}");

            AppendLog($"  → Getting stream URL...");
            var track = new TrackInfo
            {
                Id = videoId,
                Title = "Test Track",
                Author = "Unknown",
                Url = $"https://www.youtube.com/watch?v={videoId}"
            };

            try
            {
                var fullTrack = await _youtube.GetTrackByUrlAsync(track.Url);
                if (fullTrack != null) track = fullTrack;
                AppendLog($"  ✓ Title: {track.Title}");
                AppendLog($"  ✓ Author: {track.Author}");
            }
            catch (Exception ex)
            {
                AppendLog($"  ⚠️ Track info error: {ex.Message}");
            }

            var streamInfo = await _youtube.RefreshStreamUrlAsync(track, forceRefresh: true, _audioTestCts.Token);
            if (streamInfo == null)
            {
                AppendLog($"  ❌ Failed to get stream URL");
                return;
            }

            var (url, size, bitrate, codec, container) = streamInfo.Value;
            AppendLog($"  ✓ Codec: {codec}, Bitrate: {bitrate}kbps");
            AppendLog($"  ✓ Container: {container}, Size: {size / 1024.0 / 1024.0:F1}MB");
            AppendLog($"  ✓ HLS: {track.IsHlsOnly}");

            AppendLog($"  → Creating AudioPlayer...");

            if (useCache)
            {
                _testCacheManager = new AudioCacheManager();
                AudioSourceFactory.InitializeGlobalCache(_testCacheManager);
                AppendLog($"  ✓ Cache enabled");
            }

            var options = new AudioPlayerOptions
            {
                UrlRefreshCallback = async (trackId, ct) =>
                {
                    var newStream = await _youtube.RefreshStreamUrlAsync(track, forceRefresh: true, ct);
                    return newStream?.Url;
                }
            };

            _testPlayer = new AudioPlayer(options);

            // _testPlayer.StateChanged += state =>
            //     Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            //         AppendLog($"  State: {state}"));

            // _testPlayer.ErrorOccurred += ex =>
            //     Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            //         AppendLog($"  ❌ Error: {ex.Message}"));

            // _testPlayer.TrackEnded += () =>
            //     Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            //     {
            //         AppendLog($"  🏁 Track ended");
            //         IsAudioPlaying = false;
            //     });

            AppendLog($"  → Starting playback...");
            await _testPlayer.PlayAsync(url, track.Id, ct: _audioTestCts.Token);

            AppendLog($"  ▶️ Playing for {AudioTestDuration}s...");

            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < AudioTestDuration &&
                   !_audioTestCts.Token.IsCancellationRequested &&
                   _testPlayer.State == PlaybackState.Playing)
            {
                await Task.Delay(1000, _audioTestCts.Token);
                var pos = _testPlayer.Position.TotalSeconds;
                var dur = _testPlayer.Duration.TotalSeconds;
                var buf = _testPlayer.BufferProgress;
                var downloaded = _testPlayer.GetDownloadedBytes() / 1024.0;
                AppendLog($"  ⏱️ {pos:F1}s / {dur:F1}s | Buffer: {buf:F0}% | Downloaded: {downloaded:F0}KB");
            }

            AppendLog($"  ✓ Test completed");

            if (_testCacheManager != null)
            {
                var stats = _testCacheManager.GetStats();
                AppendLog($"  📦 Cache: {stats.CompleteEntries} complete, {stats.TotalSizeFormatted}");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog($"  ⏹️ Cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"  ❌ Error: {ex.Message}");
            AppendLog($"  Stack: {ex.StackTrace}");
        }
        finally
        {
            await CleanupAudioTest();
            IsBusy = false;
            IsAudioPlaying = false;
            AppendLog($"═══════════════════════════════════════\n");
        }
    }

    private void ExecuteStopAudioTest()
    {
        if (!IsAudioPlaying)
        {
            AppendLog("No audio test running.");
            return;
        }

        AppendLog("⏹️ Stopping audio test...");
        _audioTestCts?.Cancel();
        _ = CleanupAudioTest();
    }

    private async Task CleanupAudioTest()
    {
        if (_testPlayer != null)
        {
            await _testPlayer.DisposeAsync();
            _testPlayer = null;
        }

        if (_testCacheManager != null)
        {
            await _testCacheManager.DisposeAsync();
            _testCacheManager = null;
        }

        _audioTestCts?.Dispose();
        _audioTestCts = null;
        IsAudioPlaying = false;
    }

    private void ExecuteShowCacheStats()
    {
        AppendLog("\n--- AUDIO CACHE STATS ---");

        try
        {
            if (_testCacheManager != null)
            {
                var stats = _testCacheManager.GetStats();
                AppendLog($"  [Active Test Cache]");
                AppendLog($"  Total entries: {stats.TotalEntries}");
                AppendLog($"  Complete: {stats.CompleteEntries}");
                AppendLog($"  Partial: {stats.PartialEntries}");
                AppendLog($"  Size: {stats.TotalSizeFormatted} / {stats.MaxSizeFormatted}");
                AppendLog($"  Usage: {stats.UsagePercent:F1}%");
            }
            else
            {
                using var cacheManager = new AudioCacheManager();
                var stats = cacheManager.GetStats();
                AppendLog($"  [Disk Cache]");
                AppendLog($"  Total entries: {stats.TotalEntries}");
                AppendLog($"  Complete: {stats.CompleteEntries}");
                AppendLog($"  Partial: {stats.PartialEntries}");
                AppendLog($"  Size: {stats.TotalSizeFormatted} / {stats.MaxSizeFormatted}");
                AppendLog($"  Usage: {stats.UsagePercent:F1}%");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  Error: {ex.Message}");
        }

        AppendLog("--- END ---\n");
    }

    private async Task ExecuteClearAudioCache()
    {
        AppendLog("\n--- CLEARING AUDIO CACHE ---");
        IsBusy = true;

        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LMP", "AudioCache");

            if (Directory.Exists(cacheDir))
            {
                var files = Directory.GetFiles(cacheDir);
                foreach (var file in files)
                {
                    try { File.Delete(file); }
                    catch { /* ignored */ }
                }
                AppendLog($"  ✓ Deleted {files.Length} files");
            }
            else
            {
                AppendLog($"  Cache directory doesn't exist");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            AppendLog("--- CACHE CLEARED ---\n");
        }
    }

    private async Task ExecuteTestLocalFile()
    {
        AppendLog("\n--- LOCAL FILE TEST ---");
        AppendLog("  Select a .webm, .mp4, .m4a, or .ogg file to test.");

        var testPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "test.webm");

        if (!File.Exists(testPath))
        {
            AppendLog($"  ⚠️ Test file not found: {testPath}");
            AppendLog($"  Place a test audio file at this path.");
            return;
        }

        IsBusy = true;
        _audioTestCts = new CancellationTokenSource();
        IsAudioPlaying = true;

        try
        {
            AppendLog($"  File: {testPath}");

            var source = new LocalFileSource(testPath);
            if (!await source.InitializeAsync(_audioTestCts.Token))
            {
                AppendLog($"  ❌ Failed to initialize source");
                return;
            }

            AppendLog($"  ✓ Duration: {source.DurationMs}ms");
            AppendLog($"  ✓ Codec: {source.Codec}");
            AppendLog($"  ✓ Sample rate: {source.SampleRate}Hz");
            AppendLog($"  ✓ Channels: {source.Channels}");

            IAudioDecoder decoder = source.Codec == AudioCodec.Opus
                ? new OpusDecoder(source.SampleRate > 0 ? source.SampleRate : 48000,
                    source.Channels > 0 ? source.Channels : 2)
                : new AacDecoder(source.SampleRate > 0 ? source.SampleRate : 44100,
                    source.Channels > 0 ? source.Channels : 2);

            if (decoder is AacDecoder aac && source.DecoderConfig != null)
                aac.Initialize(source.DecoderConfig);

            IPlaybackBackend backend;
            try
            {
                backend = new NAudioBackend();
                AppendLog($"  ✓ NAudioBackend");
            }
            catch
            {
                backend = new NullAudioBackend();
                AppendLog($"  ⚠️ NullBackend (no audio output)");
            }

            var pcmBuffer = new LockFreeRingBuffer<float>(decoder.SampleRate * decoder.Channels * 4);
            var decodeOutput = new float[decoder.MaxFrameSize * decoder.Channels];

            backend.Initialize(decoder.SampleRate, decoder.Channels, buffer =>
            {
                int read = pcmBuffer.Read(buffer);
                if (read < buffer.Length) buffer[read..].Clear();
                return read / decoder.Channels;
            });

            var decodeTask = Task.Run(async () =>
            {
                try
                {
                    while (!_audioTestCts.Token.IsCancellationRequested)
                    {
                        while (pcmBuffer.Available < decodeOutput.Length)
                            await Task.Delay(5, _audioTestCts.Token);

                        var frame = await source.ReadFrameAsync(_audioTestCts.Token);
                        if (frame == null) break;

                        int samples = decoder.Decode(frame.Value.Data.Span, decodeOutput);
                        if (samples > 0)
                            pcmBuffer.Write(decodeOutput.AsSpan(0, samples * decoder.Channels));
                    }
                }
                catch (OperationCanceledException) { }
            });

            await Task.Delay(500, _audioTestCts.Token);

            backend.Start();
            AppendLog($"  ▶️ Playing for {AudioTestDuration}s...");

            var start = DateTime.Now;
            while ((DateTime.Now - start).TotalSeconds < AudioTestDuration &&
                   !_audioTestCts.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, _audioTestCts.Token);
                AppendLog($"  ⏱️ {source.PositionMs / 1000.0:F1}s");
            }

            backend.Stop();
            _audioTestCts.Cancel();

            backend.Dispose();
            decoder.Dispose();
            await source.DisposeAsync();

            AppendLog($"  ✓ Test completed");
        }
        catch (Exception ex)
        {
            AppendLog($"  ❌ Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsAudioPlaying = false;
            AppendLog("--- END ---\n");
        }
    }

    private static string? ExtractVideoId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        if (System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-zA-Z0-9_-]{11}$"))
            return input;

        var match = System.Text.RegularExpressions.Regex.Match(input, @"[?&]v=([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        match = System.Text.RegularExpressions.Regex.Match(input, @"youtu\.be/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        match = System.Text.RegularExpressions.Regex.Match(input, @"embed/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        match = System.Text.RegularExpressions.Regex.Match(input, @"shorts/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Memory methods
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteDumpMemory()
    {
        AppendLog("\n╔══════════════════════════════════════════╗");
        AppendLog("║         MEMORY REPORT                    ║");
        AppendLog("╠══════════════════════════════════════════╣");

        var gcInfo = GC.GetGCMemoryInfo();
        AppendLog($"║ GC Total:       {GC.GetTotalMemory(false) / 1024 / 1024,6} MB              ║");
        AppendLog($"║ GC Heap:        {gcInfo.HeapSizeBytes / 1024 / 1024,6} MB              ║");
        AppendLog($"║ Memory Load:    {gcInfo.MemoryLoadBytes / 1024 / 1024,6} MB              ║");
        AppendLog($"║ High Threshold: {gcInfo.HighMemoryLoadThresholdBytes / 1024 / 1024,6} MB              ║");
        AppendLog($"║ Gen0/1/2: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2),-6}                   ║");
        AppendLog("╚══════════════════════════════════════════╝\n");
    }

    private void ExecuteForceGc()
    {
        AppendLog("\n--- FORCING GARBAGE COLLECTION ---");
        var before = GC.GetTotalMemory(false) / 1024 / 1024;
        AppendLog($"Before: {before} MB");

        // Используем централизованный хелпер
        MemoryCleanupHelper.PerformCleanup(aggressive: true);

        // Ждём завершения (синхронно для Debug)
        Thread.Sleep(500);
        var after = GC.GetTotalMemory(true) / 1024 / 1024;
        AppendLog($"After:  {after} MB");
        AppendLog($"Freed:  {before - after} MB");
        AppendLog("--- GC COMPLETE ---\n");
    }

    private async Task ExecuteClearCaches()
    {
        AppendLog("\n--- CLEARING ALL CACHES ---");
        IsBusy = true;

        try
        {
            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            imageCache.ClearMemoryCache();
            AppendLog("✓ Image memory cache cleared");

            var searchCache = Program.Services.GetRequiredService<SearchCacheService>();
            searchCache.ClearAll();
            AppendLog("✓ Search cache cleared");

            _youtube.ClearCache();
            AppendLog("✓ YouTube stream URL cache cleared");

            var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();
            var cleaned = vmFactory.CleanupCache();
            AppendLog($"✓ TrackVM cache: cleaned {cleaned} dead refs");

            MemoryCleanupHelper.PerformCleanup(aggressive: true);
            AppendLog("✓ GC + Skia caches completed");

            await Task.Delay(300); // дать время фоновому Task завершиться
            var memMb = GC.GetTotalMemory(false) / 1024 / 1024;
            AppendLog($"\nCurrent GC memory: {memMb} MB");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            AppendLog("--- CACHES CLEARED ---\n");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // YouTube methods
    // ═══════════════════════════════════════════════════════════════

    private async Task ExecuteGetLikedVideos()
    {
        await RunSafe("YT LIKED (LL)", async () =>
        {
            var videos = await _youtube.GetClient().Playlists
                .GetVideosAsync(new Core.Youtube.Playlists.PlaylistId("LL"))
                .Take(10)
                .ToListAsync();
            return videos;
        });
    }

    private async Task ExecuteGetLikedMusic()
    {
        await RunSafe("YTM LIKED (VLLM)", async () =>
        {
            var tracks = await _youtube.GetClient().Music.GetLikedTracksAsync();
            return [.. tracks.Take(10)];
        });
    }

    private async Task ExecuteSearchVideos()
    {
        await RunSafe($"YT SEARCH: {SearchQuery}", async () =>
            await _youtube.SearchFastAsync(SearchQuery, 10, SearchFilter.Video));
    }

    private async Task ExecuteSearchMusic()
    {
        await RunSafe($"YTM SEARCH: {SearchQuery}", async () =>
            await _youtube.SearchFastAsync(SearchQuery, 10, SearchFilter.Music));
    }

    private async Task RunSafe(string title, Func<Task<List<TrackInfo>>> action)
    {
        IsBusy = true;
        AppendLog($"\n--- STARTING: {title} ---");
        try
        {
            var results = await action();
            AppendLog($"Success! Found {results.Count} items:");
            foreach (var item in results)
                AppendLog($"- [{item.Id}] {item.Title} by {item.Author} (Music: {item.IsMusic})");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            if (ex.InnerException != null) AppendLog($"INNER: {ex.InnerException.Message}");
        }
        finally
        {
            AppendLog($"--- FINISHED: {title} ---\n");
            IsBusy = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // LOG
    // ═══════════════════════════════════════════════════════════════

    private void AppendLog(string text) =>
        LogOutput += text + "\n";

    // ═══════════════════════════════════════════════════════════════
    // DISPOSE
    // ═══════════════════════════════════════════════════════════════

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _audioTestCts?.Cancel();
            _audioTestCts?.Dispose();
            _testPlayer?.Dispose();
            _testCacheManager?.Dispose();
            TestRunner.Dispose();
        }
        base.Dispose(disposing);
    }
}