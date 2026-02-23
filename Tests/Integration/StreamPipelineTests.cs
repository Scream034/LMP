#if DEBUG

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Services;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// End-to-end тесты полного pipeline: видео → стрим → дешифровка → воспроизведение.
/// </summary>
public static class StreamPipelineTests
{
    private static readonly string[] TestVideoIds =
    [
        "dQw4w9WgXcQ",  // Never Gonna Give You Up
        "jNQXAC9IVRw",  // Me at the zoo (первое видео YouTube)
        "kJQP7kiw5Fk",  // Despacito
    ];
    
    // ══════════════════════════════════════════════════════════════════
    // STREAM RESOLUTION
    // ══════════════════════════════════════════════════════════════════
    
    public static async Task TestStreamResolutionAsync(IServiceProvider services)
    {
        var youtube = services.GetRequiredService<YoutubeProvider>().GetClient();
        var videoId = TestVideoIds[0];
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var sw = Stopwatch.StartNew();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            VideoId.Parse(videoId), cts.Token);
        sw.Stop();
        
        var audioStreams = manifest.GetAudioOnlyStreams().ToList();
        Assert(audioStreams.Count > 0, "No audio streams found");
        
        var best = audioStreams.GetWithHighestBitrate();
        Log.Info($"[Test] Resolved {audioStreams.Count} streams in {sw.ElapsedMilliseconds}ms");
        Log.Info($"[Test] Best: itag={best.Itag}, {best.AudioCodec}, {best.Bitrate.KiloBitsPerSecond:F0}kbps");
        
        // Проверяем URL
        using var request = new HttpRequestMessage(HttpMethod.Head, best.Url);
        using var response = await SharedHttpClient.Instance.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        
        Assert(response.IsSuccessStatusCode, 
            $"Stream URL failed: HTTP {(int)response.StatusCode}");
    }
    
    public static async Task TestMultiVideoAsync(IServiceProvider services)
    {
        var youtube = services.GetRequiredService<YoutubeProvider>().GetClient();
        
        int success = 0;
        int failed = 0;
        
        var options = new ParallelOptions { MaxDegreeOfParallelism = 2 };
        
        await Parallel.ForEachAsync(TestVideoIds, options, async (videoId, ct) =>
        {
            try
            {
                var manifest = await youtube.Videos.Streams.GetManifestAsync(
                    VideoId.Parse(videoId), ct);
                
                var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                
                using var request = new HttpRequestMessage(HttpMethod.Head, stream.Url);
                using var response = await SharedHttpClient.Instance.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref success);
                    Log.Info($"[Test] ✓ {videoId} (itag={stream.Itag})");
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    Log.Warn($"[Test] ✗ {videoId}: HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Log.Error($"[Test] ✗ {videoId}: {ex.Message}");
            }
        });
        
        Assert(failed == 0, $"Multi-video: {failed}/{TestVideoIds.Length} failed");
        Log.Info($"[Test] Multi-video: {success}/{TestVideoIds.Length} passed");
    }
    
    // ══════════════════════════════════════════════════════════════════
    // AUDIO DOWNLOAD
    // ══════════════════════════════════════════════════════════════════
    
    public static async Task TestAudioDownloadAsync(IServiceProvider services)
    {
        var youtube = services.GetRequiredService<YoutubeProvider>().GetClient();
        var videoId = TestVideoIds[0];
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            VideoId.Parse(videoId), cts.Token);
        
        var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        
        // Скачиваем первые 64KB
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        request.Headers.Range = new RangeHeaderValue(0, 65535);
        
        using var response = await SharedHttpClient.Instance.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        
        Assert(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent,
            $"Download failed: HTTP {(int)response.StatusCode}");
        
        var buffer = await response.Content.ReadAsByteArrayAsync(cts.Token);
        
        // Проверяем magic bytes
        bool isWebM = buffer.Length >= 4 && 
            buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3;
        bool isMp4 = buffer.Length >= 8 && 
            buffer[4] == 'f' && buffer[5] == 't' && buffer[6] == 'y' && buffer[7] == 'p';
        
        Assert(isWebM || isMp4, 
            $"Invalid format. Magic: {BitConverter.ToString(buffer, 0, Math.Min(8, buffer.Length))}");
        
        Log.Info($"[Test] Downloaded {buffer.Length} bytes, format: {(isWebM ? "WebM/Opus" : "MP4/AAC")}");
    }
    
    // ══════════════════════════════════════════════════════════════════
    // FULL PIPELINE (sig + n-token)
    // ══════════════════════════════════════════════════════════════════
    
    public static async Task TestFullPipelineAsync(IServiceProvider services, string videoId = "dQw4w9WgXcQ")
    {
        Log.Info($"[Test] Full pipeline for {videoId}...");
        
        var youtube = services.GetRequiredService<YoutubeProvider>().GetClient();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var sw = Stopwatch.StartNew();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            VideoId.Parse(videoId), cts.Token);
        sw.Stop();
        
        var audioStreams = manifest.GetAudioOnlyStreams().ToList();
        Assert(audioStreams.Count > 0, "No audio streams");
        
        Log.Info($"[Test] Got {audioStreams.Count} streams in {sw.ElapsedMilliseconds}ms");
        
        // Проверяем ВСЕ стримы
        int verified = 0;
        foreach (var stream in audioStreams.Take(5))
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, stream.Url);
            using var response = await SharedHttpClient.Instance.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                verified++;
                Log.Info($"[Test] ✓ itag={stream.Itag} ({stream.AudioCodec}, " +
                        $"{stream.Bitrate.KiloBitsPerSecond:F0}kbps)");
            }
            else
            {
                Log.Warn($"[Test] ✗ itag={stream.Itag}: HTTP {(int)response.StatusCode}");
            }
        }
        
        Assert(verified > 0, "All stream URLs failed (sig decryption broken?)");
        Log.Info($"[Test] Pipeline OK: {verified}/{Math.Min(5, audioStreams.Count)} verified");
    }
    
    // ══════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════
    
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

#endif