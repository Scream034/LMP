// Core/Audio/Tests/QuickAudioTester.cs

using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;

namespace LMP.Core.Audio.Tests;

/// <summary>
/// Быстрый тестер аудио системы.
/// </summary>
public static class QuickAudioTester
{
    // Общий HttpClient для всех тестов (не создаём каждый раз новый)
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    /// <summary>
    /// 🎵 Проиграть YouTube видео по URL или Video ID.
    /// </summary>
    public static async Task PlayAsync(
        string youtubeUrlOrId,
        YoutubeProvider youtubeProvider,
        int seconds = 15,
        CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              🎵 YOUTUBE AUDIO TEST                          ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  Input: {youtubeUrlOrId}");
        Console.WriteLine($"  Duration: {seconds}s");
        Console.WriteLine();

        IAudioSource? source = null;
        IAudioDecoder? decoder = null;
        IPlaybackBackend? backend = null;

        try
        {
            // ═══════════════════════════════════════════════════════════════
            // STEP 1: Извлекаем Video ID
            // ═══════════════════════════════════════════════════════════════
            var videoId = ExtractVideoId(youtubeUrlOrId);
            if (string.IsNullOrEmpty(videoId))
            {
                Console.WriteLine($"  ❌ Cannot extract Video ID from: {youtubeUrlOrId}");
                return;
            }
            Console.WriteLine($"[1/5] Video ID: {videoId}");

            // ═══════════════════════════════════════════════════════════════
            // STEP 2: Создаём TrackInfo
            // ═══════════════════════════════════════════════════════════════
            Console.WriteLine("[2/5] Getting track info...");

            var track = new TrackInfo
            {
                Id = videoId,
                Title = "Test Track",
                Author = "Unknown",
                Url = $"https://www.youtube.com/watch?v={videoId}"
            };

            try
            {
                var fullTrack = await youtubeProvider.GetTrackByUrlAsync(track.Url);
                if (fullTrack != null)
                {
                    track = fullTrack;
                    Console.WriteLine($"  ✓ Title: {track.Title}");
                    Console.WriteLine($"  ✓ Author: {track.Author}");
                    Console.WriteLine($"  ✓ Duration: {track.Duration.TotalSeconds:F0}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ Could not get track info: {ex.Message}");
            }

            // ═══════════════════════════════════════════════════════════════
            // STEP 3: Получаем URL стрима
            // ═══════════════════════════════════════════════════════════════
            Console.WriteLine("[3/5] Getting stream URL...");

            var streamInfo = await youtubeProvider.RefreshStreamUrlAsync(track, forceRefresh: true, ct);

            if (streamInfo == null)
            {
                Console.WriteLine("  ❌ Failed to get stream URL");
                return;
            }

            var (url, size, bitrate, codec, container) = streamInfo.Value;

            Console.WriteLine($"  ✓ Codec: {codec}");
            Console.WriteLine($"  ✓ Bitrate: {bitrate} kbps");
            Console.WriteLine($"  ✓ Container: {container}");
            Console.WriteLine($"  ✓ Size: {(size > 0 ? $"{size / 1024.0 / 1024.0:F1} MB" : "Unknown")}");
            Console.WriteLine($"  ✓ HLS: {track.IsHlsOnly}");
            Console.WriteLine($"  ✓ URL: {url[..Math.Min(80, url.Length)]}...");

            // ═══════════════════════════════════════════════════════════════
            // STEP 4: Создаём пайплайн
            // ═══════════════════════════════════════════════════════════════
            Console.WriteLine("[4/5] Creating audio pipeline...");

            if (track.IsHlsOnly || container == "m3u8")
            {
                // HLS стрим
                Console.WriteLine("  → Using HLS source");
                source = new HlsStreamSource(url, SharedHttpClient);
                decoder = new AacDecoder(44100, 2);
            }
            else if (container == "webm" || codec == "Opus")
            {
                // WebM/Opus
                Console.WriteLine("  → Using WebM/Opus source");
                source = new UniversalStreamSource(
                    cacheId: videoId,
                    url: url,
                    contentLength: size > 0 ? size : 50 * 1024 * 1024, // 50MB fallback
                    expectedCodec: AudioCodec.Opus,
                    httpClient: SharedHttpClient);
                decoder = new OpusDecoder(48000, 2);
            }
            else
            {
                // MP4/AAC
                Console.WriteLine("  → Using MP4/AAC source");
                source = new UniversalStreamSource(
                    cacheId: videoId,
                    url: url,
                    contentLength: size > 0 ? size : 50 * 1024 * 1024,
                    expectedCodec: AudioCodec.Aac,
                    httpClient: SharedHttpClient);
                decoder = new AacDecoder(44100, 2);
            }

            // Инициализируем источник
            Console.WriteLine("  → Initializing source...");
            if (!await source.InitializeAsync(ct))
            {
                Console.WriteLine("  ❌ Failed to initialize source");
                return;
            }

            Console.WriteLine($"  ✓ Source initialized: duration={source.DurationMs}ms, codec={source.Codec}");

            // Обновляем декодер если нужно
            if (source.Codec != decoder.Codec)
            {
                Console.WriteLine($"  → Switching decoder to {source.Codec}");
                decoder.Dispose();
                decoder = source.Codec == AudioCodec.Opus
                    ? new OpusDecoder(48000, 2)
                    : new AacDecoder(44100, 2);
            }

            // Бэкенд
            Console.WriteLine("[5/5] Creating backend...");
            try
            {
                backend = new NAudioBackend();
                Console.WriteLine("  ✓ MiniaudioBackend (real audio)");
            }
            catch (Exception ex)
            {
                backend = new NullAudioBackend();
                Console.WriteLine($"  ⚠ NullBackend (no audio): {ex.Message}");
            }

            await PlayPipelineAsync(source, decoder, backend, seconds, ct);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n  Cancelled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  ❌ Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // Освобождаем ресурсы
            backend?.Dispose();
            decoder?.Dispose();
            if (source != null)
                await source.DisposeAsync();
        }
    }

    /// <summary>
    /// 📁 Проиграть локальный файл.
    /// </summary>
    public static async Task PlayFileAsync(string filePath, int seconds = 10)
    {
        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              📁 LOCAL FILE TEST                             ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  File: {filePath}");
        Console.WriteLine();

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"  ❌ File not found");
            return;
        }

        LocalFileSource? source = null;
        IAudioDecoder? decoder = null;
        IPlaybackBackend? backend = null;

        try
        {
            source = new LocalFileSource(filePath);

            if (!await source.InitializeAsync())
            {
                Console.WriteLine("  ❌ Failed to initialize");
                return;
            }

            Console.WriteLine($"  ✓ Duration: {source.DurationMs / 1000.0:F1}s");
            Console.WriteLine($"  ✓ Codec: {source.Codec}");

            decoder = source.Codec == AudioCodec.Opus
                ? new OpusDecoder(48000, 2)
                : new AacDecoder(44100, 2);

            try
            {
                backend = new MiniaudioBackend();
                Console.WriteLine("  ✓ MiniaudioBackend");
            }
            catch
            {
                backend = new NullAudioBackend();
                Console.WriteLine("  ⚠ NullBackend (no audio)");
            }

            await PlayPipelineAsync(source, decoder, backend, seconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Error: {ex.Message}");
        }
        finally
        {
            backend?.Dispose();
            decoder?.Dispose();
            if (source != null)
                await source.DisposeAsync();
        }
    }

    /// <summary>
    /// 🔗 Проиграть по прямой ссылке на аудио.
    /// </summary>
    public static async Task PlayDirectUrlAsync(string url, int seconds = 10)
    {
        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              🔗 DIRECT URL TEST                             ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  URL: {url[..Math.Min(70, url.Length)]}...");
        Console.WriteLine();

        IAudioSource? source = null;
        IAudioDecoder? decoder = null;
        IPlaybackBackend? backend = null;

        try
        {
            var format = AudioSourceFactory.DetectFormat(url);
            Console.WriteLine($"  Format: {format}");

            // Получаем размер
            long contentLength = await GetContentLengthAsync(url);
            Console.WriteLine($"  Size: {(contentLength > 0 ? $"{contentLength / 1024.0 / 1024.0:F1} MB" : "Unknown")}");

            var expectedCodec = AudioSourceFactory.GetCodecForFormat(format);

            source = new UniversalStreamSource(
                cacheId: Guid.NewGuid().ToString(),
                url: url,
                contentLength: contentLength > 0 ? contentLength : 50 * 1024 * 1024,
                expectedCodec: expectedCodec,
                httpClient: SharedHttpClient);

            if (!await source.InitializeAsync())
            {
                Console.WriteLine("  ❌ Failed to initialize");
                return;
            }

            Console.WriteLine($"  ✓ Duration: {source.DurationMs / 1000.0:F1}s");
            Console.WriteLine($"  ✓ Codec: {source.Codec}");

            decoder = source.Codec == AudioCodec.Opus
                ? new OpusDecoder(48000, 2)
                : new AacDecoder(44100, 2);

            try
            {
                backend = new MiniaudioBackend();
                Console.WriteLine("  ✓ MiniaudioBackend");
            }
            catch
            {
                backend = new NullAudioBackend();
                Console.WriteLine("  ⚠ NullBackend (no audio)");
            }

            await PlayPipelineAsync(source, decoder, backend, seconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            backend?.Dispose();
            decoder?.Dispose();
            if (source != null)
                await source.DisposeAsync();
        }
    }

    #region Pipeline

    private static async Task PlayPipelineAsync(
        IAudioSource source,
        IAudioDecoder decoder,
        IPlaybackBackend backend,
        int seconds,
        CancellationToken ct = default)
    {
        var pcmBuffer = new CircularBuffer<float>(decoder.SampleRate * decoder.Channels * 4);
        var decodeOutput = new float[decoder.MaxFrameSize * decoder.Channels];

        // Статистика
        int framesRead = 0;
        long bytesRead = 0;
        long lastTimestampMs = 0;
        bool endOfStream = false;
        string? error = null;

        // Настраиваем бэкенд
        backend.Initialize(decoder.SampleRate, decoder.Channels, buffer =>
        {
            int read = pcmBuffer.Read(buffer);
            if (read < buffer.Length)
                buffer[read..].Clear();
            return read / decoder.Channels;
        });

        // Декодирование в отдельном потоке
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var decodeTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // Ждём место в буфере
                    while (pcmBuffer.Available < decodeOutput.Length && !cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5, cts.Token);
                    }

                    var frame = await source.ReadFrameAsync(cts.Token);

                    if (frame == null)
                    {
                        endOfStream = true;
                        break;
                    }

                    framesRead++;
                    bytesRead += frame.Value.Data.Length;
                    lastTimestampMs = frame.Value.TimestampMs;

                    try
                    {
                        int samples = decoder.Decode(frame.Value.Data.Span, decodeOutput);
                        if (samples > 0)
                        {
                            pcmBuffer.Write(decodeOutput.AsSpan(0, samples * decoder.Channels));
                        }
                    }
                    catch (Exception ex)
                    {
                        if (framesRead < 10)
                            Console.WriteLine($"\n  ⚠ Decode: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }, cts.Token);

        // ═══════════════════════════════════════════════════════════════
        // Буферизация
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine();
        Console.WriteLine("Buffering...");
        int minBuffer = decoder.SampleRate * decoder.Channels / 5; // 200ms

        int dots = 0;
        while (pcmBuffer.Count < minBuffer && !endOfStream && error == null && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct);
            int pct = minBuffer > 0 ? pcmBuffer.Count * 100 / minBuffer : 0;
            Console.Write($"\r  [{new string('█', Math.Min(++dots % 20, 20))}{new string('░', Math.Max(0, 20 - dots % 20))}] {pct}%  ");
        }
        Console.WriteLine($"\r  Buffer ready: {pcmBuffer.Count} samples                              ");

        if (error != null)
        {
            Console.WriteLine($"  ❌ Buffering error: {error}");
            cts.Cancel();
            return;
        }

        // ═══════════════════════════════════════════════════════════════
        // Воспроизведение
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine();
        Console.WriteLine($"▶ Playing for {seconds} seconds...");
        Console.WriteLine();

        backend.Start();
        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime).TotalSeconds < seconds && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            // Отображение прогресса
            double bufferMs = (double)pcmBuffer.Count / decoder.SampleRate / decoder.Channels * 1000;
            double positionSec = lastTimestampMs / 1000.0;
            double durationSec = source.DurationMs > 0 ? source.DurationMs / 1000.0 : seconds;

            int progress = durationSec > 0 ? (int)(positionSec / durationSec * 40) : 0;
            progress = Math.Clamp(progress, 0, 40);
            string bar = new string('█', progress) + new string('░', 40 - progress);

            Console.Write($"\r  ▶ [{bar}] {positionSec:F1}s / {durationSec:F1}s | Buf: {bufferMs:F0}ms | Fr: {framesRead}  ");

            if (endOfStream)
            {
                Console.WriteLine("\n\n  🏁 End of stream");
                break;
            }

            if (error != null)
            {
                Console.WriteLine($"\n\n  ❌ Error: {error}");
                break;
            }
        }

        backend.Stop();
        cts.Cancel();

        try { await decodeTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { }

        // Итоги
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Frames decoded:  {framesRead}");
        Console.WriteLine($"  Data processed:  {bytesRead / 1024.0:F1} KB");
        Console.WriteLine($"  Last position:   {lastTimestampMs / 1000.0:F1}s");
        Console.WriteLine($"  Buffer progress: {source.BufferProgress:F0}%");
        Console.WriteLine($"  Status: {(endOfStream ? "✓ Completed" : error != null ? $"❌ {error}" : "⏹ Stopped")}");
        Console.WriteLine("════════════════════════════════════════════════════════════");
    }
    #endregion

    #region Helpers

    private static string? ExtractVideoId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();

        // Уже Video ID?
        if (System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-zA-Z0-9_-]{11}$"))
            return input;

        // youtube.com/watch?v=VIDEO_ID
        var match = System.Text.RegularExpressions.Regex.Match(input, @"[?&]v=([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        // youtu.be/VIDEO_ID
        match = System.Text.RegularExpressions.Regex.Match(input, @"youtu\.be/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        // youtube.com/embed/VIDEO_ID
        match = System.Text.RegularExpressions.Regex.Match(input, @"embed/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        // youtube.com/shorts/VIDEO_ID
        match = System.Text.RegularExpressions.Regex.Match(input, @"shorts/([a-zA-Z0-9_-]{11})");
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Добавляем User-Agent
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        return client;
    }

    private static async Task<long> GetContentLengthAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    #endregion
}