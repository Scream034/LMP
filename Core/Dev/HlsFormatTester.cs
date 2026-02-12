#if HLS_FORMAT_TEST

using System.Text.RegularExpressions;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Bridge;

namespace LMP.Core.Dev;

/// <summary>
/// Тестирование форматов HLS для разных типов YouTube контента.
/// Использует существующую инфраструктуру YoutubeClient.
/// </summary>
public static partial class HlsFormatTester
{
    public record FormatInfo(
        string Itag,
        string Codecs,
        int? Bandwidth,
        string? Resolution,
        bool IsAudioOnly
    );

    public record TestResult(
        string VideoId,
        string TestType,
        bool Success,
        List<FormatInfo> AudioFormats,
        List<FormatInfo> VideoFormats,
        string? HlsUrl,
        string? Error,
        string? RawManifest
    );

    /// <summary>
    /// Запустить все тесты.
    /// </summary>
    public static async Task RunAllTestsAsync(IServiceProvider? services = null)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           HLS FORMAT TESTER - Using Your Infrastructure      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        // Тестовые видео
        var tests = new (string Id, string Type)[]
        {
            // YouTube Music (официальный трек)
            ("dQw4w9WgXcQ", "YouTube Music / Official Audio"),
            
            // Обычное видео  
            ("jNQXAC9IVRw", "Regular YouTube Video (First YT video)"),
            
            // Made for Kids (Cocomelon)
            ("QSTFVxtPCN0", "Made for Kids"),
            
            // Ещё один Music
            ("9bZkp7q19f0", "YouTube Music (PSY - Gangnam Style)"),
            
            // Live stream (если есть HLS)
            // ("5qap5aO4i9A", "Live Stream"),
        };

        // Создаём свой HttpClient с YoutubeHttpHandler
        using var handler = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 10
            }),
            authService: null, // Без авторизации для теста
            disposeClient: true
        );

        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var controller = new TestableVideoController(http);
        var results = new List<TestResult>();

        foreach (var (videoId, testType) in tests)
        {
            Console.WriteLine($"━━━ Testing: {testType} ━━━");
            Console.WriteLine($"    Video ID: {videoId}");

            var result = await TestVideoAsync(controller, http, videoId, testType);
            results.Add(result);

            PrintResult(result);
            Console.WriteLine();

            await Task.Delay(1500); // Rate limiting
        }

        PrintSummary(results);
    }

    /// <summary>
    /// Тест одного видео.
    /// </summary>
    private static async Task<TestResult> TestVideoAsync(
        TestableVideoController controller,
        HttpClient http,
        string videoId,
        string testType)
    {
        try
        {
            // 1. Получаем HLS URL через твой VideoController
            var hlsUrl = await controller.GetHlsManifestUrlAsync(videoId, default);

            if (string.IsNullOrEmpty(hlsUrl))
            {
                return new(videoId, testType, false, [], [], null, "No HLS manifest URL returned", null);
            }

            Console.WriteLine($"    HLS URL: {hlsUrl[..Math.Min(80, hlsUrl.Length)]}...");

            // 2. Скачиваем master manifest
            using var request = new HttpRequestMessage(HttpMethod.Get, hlsUrl);
            request.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);

            using var response = await http.SendAsync(request);
            var manifest = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new(videoId, testType, false, [], [], hlsUrl,
                    $"HTTP {(int)response.StatusCode}: {manifest[..Math.Min(200, manifest.Length)]}", null);
            }

            // 3. Парсим форматы
            var (audio, video) = ParseMasterPlaylist(manifest);

            return new(videoId, testType, true, audio, video, hlsUrl, null, manifest);
        }
        catch (Exception ex)
        {
            return new(videoId, testType, false, [], [], null, ex.Message, null);
        }
    }

    private static (List<FormatInfo> Audio, List<FormatInfo> Video) ParseMasterPlaylist(string manifest)
    {
        var audio = new List<FormatInfo>();
        var video = new List<FormatInfo>();

        var lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // #EXT-X-STREAM-INF для видео+аудио или только аудио
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                var info = ParseAttributes(line);
                var url = i + 1 < lines.Length ? lines[i + 1].Trim() : "";

                // Пропускаем если это не URL
                if (url.StartsWith("#")) continue;

                var codecs = info.GetValueOrDefault("CODECS", "unknown").Trim('"');
                var resolution = info.GetValueOrDefault("RESOLUTION", "");
                var bandwidth = int.TryParse(info.GetValueOrDefault("BANDWIDTH", "0"), out var bw) ? bw : 0;

                var format = new FormatInfo(
                    ExtractItag(url),
                    codecs,
                    bandwidth,
                    resolution,
                    string.IsNullOrEmpty(resolution) // Нет разрешения = только аудио
                );

                if (format.IsAudioOnly)
                    audio.Add(format);
                else
                    video.Add(format);
            }
            // #EXT-X-MEDIA для отдельных аудио треков
            else if (line.StartsWith("#EXT-X-MEDIA:") && line.Contains("TYPE=AUDIO"))
            {
                var info = ParseAttributes(line);
                var uri = info.GetValueOrDefault("URI", "").Trim('"');
                var name = info.GetValueOrDefault("NAME", "unknown").Trim('"');
                var groupId = info.GetValueOrDefault("GROUP-ID", "").Trim('"');

                if (!string.IsNullOrEmpty(uri))
                {
                    audio.Add(new FormatInfo(
                        ExtractItag(uri),
                        $"{name} ({groupId})",
                        null,
                        null,
                        true
                    ));
                }
            }
        }

        return (audio, video);
    }

    private static Dictionary<string, string> ParseAttributes(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Убираем префикс
        var attrPart = line.Contains(':') ? line[(line.IndexOf(':') + 1)..] : line;

        // Парсим KEY=VALUE или KEY="VALUE"
        var matches = AttributeRegex().Matches(attrPart);

        foreach (Match m in matches)
        {
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            result[key] = value;
        }

        return result;
    }

    private static string ExtractItag(string url)
    {
        var match = ItagRegex().Match(url);
        return match.Success ? match.Groups[1].Value : "?";
    }

    private static void PrintResult(TestResult result)
    {
        if (!result.Success)
        {
            Console.WriteLine($"    ❌ FAILED: {result.Error}");
            return;
        }

        Console.WriteLine($"    ✅ SUCCESS");

        // Аудио форматы
        Console.WriteLine($"\n    📻 Audio Formats ({result.AudioFormats.Count}):");

        if (result.AudioFormats.Count == 0)
        {
            Console.WriteLine($"        (none found in manifest)");
        }
        else
        {
            foreach (var fmt in result.AudioFormats.OrderByDescending(f => f.Bandwidth ?? 0))
            {
                var codec = ParseCodecName(fmt.Codecs);
                var bitrate = fmt.Bandwidth.HasValue ? $"{fmt.Bandwidth.Value / 1000}kbps" : "N/A";
                var isOpus = codec.Contains("OPUS", StringComparison.OrdinalIgnoreCase);
                var marker = isOpus ? "🟢" : "🟡";

                Console.WriteLine($"        {marker} itag={fmt.Itag,-4} | {codec,-15} | {bitrate,-10}");
            }
        }

        // Видео форматы (кратко)
        if (result.VideoFormats.Count > 0)
        {
            Console.WriteLine($"\n    🎬 Video+Audio Formats ({result.VideoFormats.Count}):");

            foreach (var fmt in result.VideoFormats
                .OrderByDescending(f => f.Bandwidth ?? 0)
                .Take(5))
            {
                var codec = ParseCodecName(fmt.Codecs);
                var bitrate = fmt.Bandwidth.HasValue ? $"{fmt.Bandwidth.Value / 1000}kbps" : "N/A";
                Console.WriteLine($"        itag={fmt.Itag,-4} | {fmt.Resolution,-10} | {codec,-20} | {bitrate}");
            }

            if (result.VideoFormats.Count > 5)
                Console.WriteLine($"        ... and {result.VideoFormats.Count - 5} more");
        }

        // Статистика кодеков
        var allCodecs = result.AudioFormats.Select(f => f.Codecs)
            .Concat(result.VideoFormats.Select(f => f.Codecs));

        var hasOpus = allCodecs.Any(c =>
            c.Contains("opus", StringComparison.OrdinalIgnoreCase));
        var hasAac = allCodecs.Any(c =>
            c.Contains("mp4a", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"\n    📊 Codec Summary: OPUS={hasOpus}, AAC={hasAac}");
    }

    private static string ParseCodecName(string codecs)
    {
        if (string.IsNullOrEmpty(codecs)) return "unknown";

        // Может быть несколько кодеков: "avc1.4d401f,mp4a.40.2"
        var parts = codecs.Split(',').Select(c => c.Trim()).ToList();
        var names = new List<string>();

        foreach (var part in parts)
        {
            var name = part switch
            {
                _ when part.Contains("opus", StringComparison.OrdinalIgnoreCase) => "OPUS",
                _ when part.StartsWith("mp4a.40.2") => "AAC-LC",
                _ when part.StartsWith("mp4a.40.5") => "AAC-HE",
                _ when part.StartsWith("mp4a.40.29") => "AAC-HEv2",
                _ when part.StartsWith("mp4a") => "AAC",
                _ when part.StartsWith("avc1") => "H.264",
                _ when part.StartsWith("vp9") || part.StartsWith("vp09") => "VP9",
                _ when part.StartsWith("av01") => "AV1",
                _ => part
            };
            names.Add(name);
        }

        return string.Join("+", names);
    }

    private static void PrintSummary(List<TestResult> results)
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                         SUMMARY                              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        Console.WriteLine($"    Total tests:  {results.Count}");
        Console.WriteLine($"    Successful:   {results.Count(r => r.Success)}");
        Console.WriteLine($"    Failed:       {results.Count(r => !r.Success)}");
        Console.WriteLine();

        // Анализ кодеков
        var successResults = results.Where(r => r.Success).ToList();

        if (successResults.Count == 0)
        {
            Console.WriteLine("    ⚠️  No successful tests to analyze.");
            return;
        }

        // Проверяем наличие OPUS
        var allHaveOpus = successResults.All(r =>
        {
            var allCodecs = r.AudioFormats.Select(f => f.Codecs)
                .Concat(r.VideoFormats.Select(f => f.Codecs));
            return allCodecs.Any(c => c.Contains("opus", StringComparison.OrdinalIgnoreCase));
        });

        var someHaveOnlyAac = successResults.Any(r =>
        {
            var allCodecs = r.AudioFormats.Select(f => f.Codecs)
                .Concat(r.VideoFormats.Select(f => f.Codecs));
            return !allCodecs.Any(c => c.Contains("opus", StringComparison.OrdinalIgnoreCase));
        });

        if (allHaveOpus)
        {
            Console.WriteLine("    🟢 EXCELLENT: OPUS is available in ALL tested content!");
            Console.WriteLine("       → You can safely skip AAC decoder.");
            Console.WriteLine("       → Use OPUS-only for smaller app size.");
        }
        else if (someHaveOnlyAac)
        {
            Console.WriteLine("    🟡 WARNING: Some content has AAC only!");
            Console.WriteLine("       Videos without OPUS:");

            foreach (var r in successResults)
            {
                var allCodecs = r.AudioFormats.Select(f => f.Codecs)
                    .Concat(r.VideoFormats.Select(f => f.Codecs));

                if (!allCodecs.Any(c => c.Contains("opus", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"         ❌ {r.VideoId} ({r.TestType})");
                }
            }
        }

        // Уникальные аудио кодеки
        Console.WriteLine("\n    Unique AUDIO codecs found across all tests:");

        var uniqueAudioCodecs = successResults
            .SelectMany(r => r.AudioFormats)
            .Select(f => ParseCodecName(f.Codecs))
            .Distinct()
            .OrderBy(c => c);

        foreach (var codec in uniqueAudioCodecs)
        {
            var marker = codec.Contains("OPUS") ? "🟢" : "🟡";
            Console.WriteLine($"        {marker} {codec}");
        }

        // Рекомендация
        Console.WriteLine("\n    ═══════════════════════════════════════════");
        Console.WriteLine("    RECOMMENDATION:");

        if (allHaveOpus)
        {
            Console.WriteLine("    ✅ Implement OPUS only (via Concentus)");
            Console.WriteLine("    ✅ Skip AAC - not needed for HLS/iOS");
            Console.WriteLine("    ✅ App size will be ~5MB smaller");
        }
        else
        {
            Console.WriteLine("    ⚠️  Consider adding AAC support as fallback");
            Console.WriteLine("    ⚠️  Or test more videos to confirm pattern");
        }
    }

    /// <summary>
    /// Расширенный тест: проверяем ВСЕ доступные стримы (не только HLS).
    /// </summary>
    public static async Task TestAllStreamsAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║       EXTENDED TEST - All Available Streams                   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var tests = new (string Id, string Type)[]
        {
        ("QSTFVxtPCN0", "Made for Kids - fluxxwave"),           // Детский контент
        ("dQw4w9WgXcQ", "Regular Music"),           // Обычная музыка
        ("jNQXAC9IVRw", "First YT Video"),          // Первое видео YT
        };

        using var handler = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 10
            }),
            authService: null,
            disposeClient: true
        );

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var controller = new TestableVideoController(http);

        foreach (var (videoId, testType) in tests)
        {
            Console.WriteLine($"━━━ {testType}: {videoId} ━━━\n");

            // Тестируем каждый клиент
            foreach (var clientName in new[] { "ANDROID_VR", "ANDROID_MUSIC", "IOS", "WEB" })
            {
                try
                {
                    Console.WriteLine($"    [{clientName}]");

                    var response = await controller.GetPlayerResponseWithClientAsync(
                        videoId, clientName, default);

                    // Статус
                    var status = response.IsPlayable ? "✅ Playable" : $"❌ {response.PlayabilityError}";
                    Console.WriteLine($"        Status: {status}");

                    // HLS URL
                    var hlsUrl = response.HlsManifestUrl;
                    Console.WriteLine($"        HLS: {(string.IsNullOrEmpty(hlsUrl) ? "❌ None" : "✅ Available")}");

                    // Стримы
                    var streams = response.Streams.ToList();
                    var audioOnly = streams.Where(s =>
                        !string.IsNullOrEmpty(s.AudioCodec) &&
                        string.IsNullOrEmpty(s.VideoCodec)).ToList();

                    Console.WriteLine($"        Total streams: {streams.Count}");
                    Console.WriteLine($"        Audio-only: {audioOnly.Count}");

                    if (audioOnly.Count > 0)
                    {
                        Console.WriteLine($"        Audio codecs:");
                        var grouped = audioOnly
                            .GroupBy(s => s.AudioCodec ?? "unknown")
                            .OrderByDescending(g => g.Count());

                        foreach (var g in grouped)
                        {
                            var codec = g.Key;
                            var isOpus = codec.Contains("opus", StringComparison.OrdinalIgnoreCase);
                            var marker = isOpus ? "🟢" : "🟡";
                            var bitrates = g.Select(s => s.Bitrate / 1000).OrderByDescending(b => b);
                            Console.WriteLine($"            {marker} {codec}: {string.Join(", ", bitrates)}kbps");
                        }
                    }

                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"        ❌ Error: {ex.Message}\n");
                }

                await Task.Delay(500); // Rate limit
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Тест Range-запросов для IOS стримов.
    /// Проверяем ограничение 1MB.
    /// </summary>
    public static async Task TestIosRangeLimitAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          IOS STREAM RANGE LIMIT TEST                         ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var videoId = "QSTFVxtPCN0"; // Rick Astley

        using var handler = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 10
            }),
            authService: null,
            disposeClient: true
        );

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var controller = new TestableVideoController(http);

        Console.WriteLine($"Testing video: {videoId}\n");

        // 1. Получаем IOS стримы
        var response = await controller.GetPlayerResponseWithClientAsync(videoId, "IOS", default);

        var opusStreams = response.Streams
            .Where(s => s.AudioCodec?.Contains("opus", StringComparison.OrdinalIgnoreCase) == true
                     && string.IsNullOrEmpty(s.VideoCodec))
            .ToList();

        if (opusStreams.Count == 0)
        {
            Console.WriteLine("❌ No OPUS streams found");
            return;
        }

        var stream = opusStreams.OrderByDescending(s => s.Bitrate).First();
        var url = stream.Url;
        var contentLength = stream.ContentLength ?? 0;

        Console.WriteLine($"Selected stream:");
        Console.WriteLine($"  Codec: {stream.AudioCodec}");
        Console.WriteLine($"  Bitrate: {stream.Bitrate / 1000}kbps");
        Console.WriteLine($"  Size: {contentLength / 1024 / 1024:F2}MB");
        Console.WriteLine($"  URL: {url?[..80]}...\n");

        if (string.IsNullOrEmpty(url))
        {
            Console.WriteLine("❌ No URL");
            return;
        }

        // 2. Тестируем разные Range размеры
        var testRanges = new[]
        {
        ("512KB", 512 * 1024L),
        ("1MB", 1024 * 1024L),
        ("2MB", 2 * 1024 * 1024L),
        ("5MB", 5 * 1024 * 1024L),
        ("10MB", 10 * 1024 * 1024L),
    };

        Console.WriteLine("Testing Range requests:\n");

        foreach (var (label, size) in testRanges)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, size - 1);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                sw.Stop();

                var statusCode = (int)resp.StatusCode;
                var actualLength = resp.Content.Headers.ContentLength ?? 0;

                if (resp.IsSuccessStatusCode)
                {
                    // Пробуем скачать
                    var downloaded = 0L;
                    await using var stream2 = await resp.Content.ReadAsStreamAsync();
                    var buffer = new byte[81920];
                    int read;

                    var downloadSw = System.Diagnostics.Stopwatch.StartNew();
                    while ((read = await stream2.ReadAsync(buffer)) > 0 && downloaded < size)
                    {
                        downloaded += read;
                    }
                    downloadSw.Stop();

                    var success = downloaded >= Math.Min(size, contentLength);
                    var marker = success ? "✅" : "⚠️";
                    var speed = downloaded / downloadSw.Elapsed.TotalSeconds / 1024 / 1024;

                    Console.WriteLine($"  {marker} {label,-8} | HTTP {statusCode} | Downloaded: {downloaded / 1024:N0}KB | Speed: {speed:F1}MB/s | Time: {downloadSw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Console.WriteLine($"  ❌ {label,-8} | HTTP {statusCode} | {resp.ReasonPhrase}");

                    // Пробуем прочитать тело ответа (может быть error message)
                    try
                    {
                        var errorBody = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(errorBody) && errorBody.Length < 500)
                        {
                            Console.WriteLine($"              Error: {errorBody}");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {label,-8} | Exception: {ex.Message}");
            }

            await Task.Delay(500); // Rate limit
        }

        // 3. Тест последовательных мелких запросов (как это делает HLS)
        Console.WriteLine("\n\nTesting sequential 1MB chunks (HLS-style):\n");

        var chunkSize = 1024 * 1024L; // 1MB
        var chunksToTest = 5;
        var allSuccess = true;

        for (int i = 0; i < chunksToTest; i++)
        {
            try
            {
                var start = i * chunkSize;
                var end = Math.Min(start + chunkSize - 1, contentLength - 1);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    var downloaded = 0L;
                    await using var stream2 = await resp.Content.ReadAsStreamAsync();
                    var buffer = new byte[81920];
                    int read;

                    while ((read = await stream2.ReadAsync(buffer)) > 0)
                    {
                        downloaded += read;
                    }

                    Console.WriteLine($"  ✅ Chunk {i + 1}/{chunksToTest} | Range: {start / 1024}KB-{end / 1024}KB | Downloaded: {downloaded / 1024}KB | {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Console.WriteLine($"  ❌ Chunk {i + 1}/{chunksToTest} | HTTP {(int)resp.StatusCode} | Range: {start / 1024}KB-{end / 1024}KB");
                    allSuccess = false;
                    break;
                }

                await Task.Delay(200); // Пауза между запросами
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ Chunk {i + 1}/{chunksToTest} | Exception: {ex.Message}");
                allSuccess = false;
                break;
            }
        }

        Console.WriteLine("\n" + new string('─', 64));
        Console.WriteLine("\nCONCLUSION:");

        if (allSuccess)
        {
            Console.WriteLine("✅ Sequential 1MB chunks work!");
            Console.WriteLine("   → IOS streams CAN be used with proper chunking");
            Console.WriteLine("   → No HLS needed, just segment downloads");
        }
        else
        {
            Console.WriteLine("❌ IOS streams are limited");
            Console.WriteLine("   → HLS is required for Made for Kids content");
            Console.WriteLine("   → Need AAC decoder for HLS segments");
        }
    }

    /// <summary>
    /// Детальный дамп PlayerResponse стримов.
    /// </summary>
    internal static async Task<IStreamData?> DumpPlayerResponseStreamsAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          PLAYER RESPONSE STREAMS DUMP                        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var testVideos = new[]
        {
        ("QSTFVxtPCN0", "Your Made for Kids"),
        ("dQw4w9WgXcQ", "Rick Astley"),
    };

        using var handler = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler()),
            authService: null,
            disposeClient: true
        );

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var controller = new TestableVideoController(http);

        foreach (var (videoId, label) in testVideos)
        {
            Console.WriteLine($"━━━ {label}: {videoId} ━━━\n");

            // Получаем IOS response
            var response = await controller.GetPlayerResponseWithClientAsync(videoId, "IOS", default);

            Console.WriteLine($"IsPlayable: {response.IsPlayable}");
            Console.WriteLine($"Total Streams: {response.Streams.Count()}\n");

            int i = 0;
            foreach (var stream in response.Streams.Take(20)) // Первые 20
            {
                Console.WriteLine($"Stream #{i++}:");
                Console.WriteLine($"  Itag:          {stream.Itag}");
                // Console.WriteLine($"  MimeType:      {stream.MimeType ?? "null"}");
                Console.WriteLine($"  Container:     {stream.Container ?? "null"}");
                // Console.WriteLine($"  Codecs:        {stream.Codecs ?? "null"}");
                Console.WriteLine($"  AudioCodec:    {stream.AudioCodec ?? "null"}");
                Console.WriteLine($"  VideoCodec:    {stream.VideoCodec ?? "null"}");
                Console.WriteLine($"  Bitrate:       {stream.Bitrate}");
                Console.WriteLine($"  ContentLength: {stream.ContentLength}");
                Console.WriteLine($"  URL:           {(stream.Url?.Length > 0 ? stream.Url[..60] + "..." : "null")}");
                Console.WriteLine($"  Signature:     {(stream.Signature?.Length > 0 ? stream.Signature[..20] + "..." : "null")}");
                Console.WriteLine();
            }

            Console.WriteLine(new string('─', 64) + "\n");
            return response.Streams.Where(s => s.AudioCodec == "opus").OrderByDescending(s => s.Bitrate).FirstOrDefault();
        }

        return null;
    }

    public static async Task TestSignatureRefreshAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          SIGNATURE REFRESH TEST FOR IOS STREAMS              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var videoId = "QSTFVxtPCN0";

        using var handler = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler()),
            authService: null,
            disposeClient: true
        );

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var controller = new TestableVideoController(http);

        Console.WriteLine("Test: Download 3MB using signature refresh every 900KB\n");

        long totalDownloaded = 0;
        const long targetSize = 3 * 1024 * 1024; // 3MB
        const long maxPerUrl = 900 * 1024; // 900KB per URL
        int urlRefreshCount = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (totalDownloaded < targetSize)
        {
            // Получаем НОВЫЙ PlayerResponse → НОВЫЙ URL
            Console.WriteLine($"[{urlRefreshCount + 1}] Getting fresh URL...");

            var response = await controller.GetPlayerResponseWithClientAsync(videoId, "IOS", default);

            var opusStream = response.Streams.Where(s => s.AudioCodec == "opus").OrderByDescending(s => s.Bitrate).FirstOrDefault();

            if (opusStream == null)
            {
                Console.WriteLine("   ❌ No OPUS stream");
                break;
            }

            var url = opusStream.Url;

            // Проверим что URL действительно разный
            if (urlRefreshCount == 0)
            {
                Console.WriteLine($"   URL: {url?[..80]}...");
            }
            else
            {
                // Сравниваем signature параметр
                var sig = System.Web.HttpUtility.ParseQueryString(new Uri(url!).Query)["sig"];
                Console.WriteLine($"   Signature: {sig?[..20]}...");
            }

            urlRefreshCount++;

            // Скачиваем через этот URL
            long downloadedFromThisUrl = 0;

            while (downloadedFromThisUrl < maxPerUrl && totalDownloaded < targetSize)
            {
                var chunkSize = Math.Min(maxPerUrl - downloadedFromThisUrl, targetSize - totalDownloaded);

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);

                    // Range от текущей позиции
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                        totalDownloaded,
                        totalDownloaded + chunkSize - 1
                    );

                    using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"   ❌ HTTP {(int)resp.StatusCode} at {totalDownloaded / 1024}KB");
                        goto failed;
                    }

                    // Скачиваем данные
                    var data = await resp.Content.ReadAsByteArrayAsync();
                    downloadedFromThisUrl += data.Length;
                    totalDownloaded += data.Length;

                    Console.WriteLine($"   ✅ Downloaded {data.Length / 1024}KB (total: {totalDownloaded / 1024}KB)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ❌ Error: {ex.Message}");
                    goto failed;
                }
            }

            // Пауза между запросами новых URL
            await Task.Delay(500);
        }

    failed:

        sw.Stop();

        Console.WriteLine(new string('─', 64));
        Console.WriteLine("\nRESULTS:\n");

        if (totalDownloaded >= targetSize)
        {
            var speed = totalDownloaded / sw.Elapsed.TotalSeconds / 1024 / 1024;

            Console.WriteLine($"✅ SUCCESS! Downloaded {totalDownloaded / 1024 / 1024:F1}MB");
            Console.WriteLine($"   URLs used: {urlRefreshCount}");
            Console.WriteLine($"   Average per URL: {totalDownloaded / urlRefreshCount / 1024}KB");
            Console.WriteLine($"   Speed: {speed:F1}MB/s");
            Console.WriteLine($"   Time: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine();
            Console.WriteLine("✅ SIGNATURE REFRESH WORKS!");
            Console.WriteLine("   → IOS streams CAN be used!");
            Console.WriteLine("   → No HLS needed!");
            Console.WriteLine("   → OPUS only is possible!");
        }
        else
        {
            Console.WriteLine($"❌ FAILED at {totalDownloaded / 1024}KB");
            Console.WriteLine($"   URLs tried: {urlRefreshCount}");
            Console.WriteLine();
            Console.WriteLine("❌ Signature refresh doesn't help");
            Console.WriteLine("   → Need HLS + AAC fallback");
        }
    }

#if HLS_FORMAT_TEST

    public static async Task TestCookieBypassAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          TESTING COOKIE/SESSION BYPASS                       ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var videoId = "QSTFVxtPCN0";

        // Первый запрос - получаем URL
        using var handler1 = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler()),
            authService: null,
            disposeClient: true
        );
        using var http1 = new HttpClient(handler1);
        var controller1 = new TestableVideoController(http1);

        var response = await controller1.GetPlayerResponseWithClientAsync(videoId, "IOS", default);
        var opusStream = response.Streams
            .FirstOrDefault(s => s.AudioCodec == "opus" && s.Itag == 251);

        if (opusStream == null)
        {
            Console.WriteLine("❌ No OPUS stream");
            return;
        }

        var url = opusStream.Url!;

        Console.WriteLine("Testing same URL with different HTTP clients:\n");

        for (int i = 0; i < 3; i++)
        {
            Console.WriteLine($"Client #{i + 1}:");

            // НОВЫЙ HttpClient каждый раз (новая сессия)
            using var freshHandler = new SocketsHttpHandler
            {
                CookieContainer = new System.Net.CookieContainer(), // Чистые cookie
                UseCookies = true
            };
            using var freshHttp = new HttpClient(freshHandler);

            for (int mb = 1; mb <= 2; mb++)
            {
                try
                {
                    var size = mb * 1024 * 1024L;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, size - 1);

                    using var resp = await freshHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    if (resp.IsSuccessStatusCode)
                    {
                        var data = await resp.Content.ReadAsByteArrayAsync();
                        Console.WriteLine($"  ✅ {mb}MB: Downloaded {data.Length / 1024}KB");
                    }
                    else
                    {
                        Console.WriteLine($"  ❌ {mb}MB: HTTP {(int)resp.StatusCode}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ {mb}MB: {ex.Message}");
                    break;
                }

                await Task.Delay(200);
            }

            Console.WriteLine();
        }
    }

#endif

#if HLS_FORMAT_TEST

    public static async Task TestParallelConnectionsAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          TESTING PARALLEL CONNECTIONS                        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var videoId = "QSTFVxtPCN0";

        using var handler = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler()),
            authService: null,
            disposeClient: true
        );
        using var http = new HttpClient(handler);
        var controller = new TestableVideoController(http);

        var response = await controller.GetPlayerResponseWithClientAsync(videoId, "IOS", default);
        var opusStream = response.Streams
            .FirstOrDefault(s => s.AudioCodec == "opus" && s.Itag == 251);

        if (opusStream == null) return;

        var url = opusStream.Url!;
        var totalSize = opusStream.ContentLength ?? 0;

        Console.WriteLine($"Testing 4 parallel connections, 500KB each:\n");

        // Запускаем 4 параллельных загрузки разных частей
        var tasks = new List<Task<bool>>();

        for (int i = 0; i < 4; i++)
        {
            int connId = i;
            var start = i * 500 * 1024L;
            var end = start + 500 * 1024 - 1;

            if (end > totalSize) end = totalSize - 1;

            var task = Task.Run(async () =>
            {
                // Каждый со своим HttpClient
                using var h = new HttpClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                try
                {
                    using var resp = await h.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        var data = await resp.Content.ReadAsByteArrayAsync();
                        Console.WriteLine($"  ✅ Connection {connId + 1}: {start / 1024}KB-{end / 1024}KB = {data.Length / 1024}KB");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"  ❌ Connection {connId + 1}: HTTP {(int)resp.StatusCode}");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ Connection {connId + 1}: {ex.Message}");
                    return false;
                }
            });

            tasks.Add(task);
        }

        var results = await Task.WhenAll(tasks);

        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"\nRESULT: {results.Count(r => r)}/4 succeeded\n");

        if (results.All(r => r))
        {
            Console.WriteLine("✅ PARALLEL CONNECTIONS WORK!");
            Console.WriteLine("   → Can download 500KB × N connections");
            Console.WriteLine("   → Total 2MB+ possible!");
        }
        else
        {
            Console.WriteLine("❌ Parallel connections limited");
        }
    }

#endif

    public static async Task TestIosOpusRangeLimitAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        IOS OPUS STREAM - RANGE REQUEST TEST                  ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        using var handler = new YoutubeHttpHandler(
            new HttpClient(new SocketsHttpHandler()),
            authService: null,
            disposeClient: true
        );

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var controller = new TestableVideoController(http);

        // string[] videoIds = ["dQw4w9WgXcQ", "jNQXAC9IVRw", "QSTFVxtPCN0"];
        IStreamData? opusStream = await DumpPlayerResponseStreamsAsync();
        // string foundId = "";

        // foreach (var id in videoIds)
        // {
        //     var response = await controller.GetPlayerResponseWithClientAsync(id, "IOS", default);
        //     opusStream = response.Streams.FirstOrDefault(s => s.AudioCodec == "opus");
        //     if (opusStream != null)
        //     {
        //         foundId = id;
        //         break;
        //     }
        // }

        if (opusStream == null)
        {
            Console.WriteLine("❌ TOTAL FAILURE: No OPUS found in ANY video via iOS");
            return;
        }

        var url = opusStream.Url;
        var contentLength = opusStream.ContentLength ?? 0;

        Console.WriteLine($"OPUS Stream (itag=251):");
        Console.WriteLine($"  Container: {opusStream.Container}");
        Console.WriteLine($"  Codec:     {opusStream.AudioCodec}");
        Console.WriteLine($"  Bitrate:   {opusStream.Bitrate / 1000}kbps");
        Console.WriteLine($"  Size:      {contentLength / 1024:N0}KB ({contentLength / 1024 / 1024:F2}MB)");
        Console.WriteLine($"  URL:       {url?[..80]}...\n");

        if (string.IsNullOrEmpty(url))
        {
            Console.WriteLine("❌ No URL");
            return;
        }

        // Тестируем Range запросы
        var testRanges = new[]
        {
        ("100KB",  100 * 1024L),
        ("512KB",  512 * 1024L),
        ("1MB",    1024 * 1024L),
        ("2MB",    2 * 1024 * 1024L),
        ("5MB",    5 * 1024 * 1024L),
        ("10MB",   10 * 1024 * 1024L),
        ("FULL",   contentLength),
    };

        Console.WriteLine("Testing Range requests:\n");

        foreach (var (label, size) in testRanges)
        {
            var actualSize = Math.Min(size, contentLength);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, actualSize - 1);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (resp.IsSuccessStatusCode)
                {
                    var downloaded = 0L;
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    var buffer = new byte[81920];
                    int read;

                    while ((read = await stream.ReadAsync(buffer)) > 0 && downloaded < actualSize)
                    {
                        downloaded += read;
                    }

                    sw.Stop();

                    var success = downloaded >= actualSize * 0.95; // 95% считаем успехом
                    var marker = success ? "✅" : "⚠️";
                    var speed = downloaded / sw.Elapsed.TotalSeconds / 1024 / 1024;

                    Console.WriteLine($"  {marker} {label,-8} | {downloaded / 1024:N0}KB / {actualSize / 1024:N0}KB | {speed:F1}MB/s | {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Console.WriteLine($"  ❌ {label,-8} | HTTP {(int)resp.StatusCode} - {resp.ReasonPhrase}");

                    // Показать первые 200 байт ответа
                    try
                    {
                        var errorBody = await resp.Content.ReadAsStringAsync();
                        if (errorBody.Length > 0 && errorBody.Length < 500)
                        {
                            Console.WriteLine($"              {errorBody}");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {label,-8} | Exception: {ex.Message}");
            }

            await Task.Delay(300); // Rate limit
        }

        // Sequential chunks тест (как делает твой MemoryFirstCachingStream)
        Console.WriteLine("\n\nTesting sequential 512KB chunks:\n");

        var chunkSize = 512 * 1024L;
        var chunksToTest = Math.Min(5, (int)Math.Ceiling((double)contentLength / chunkSize));
        var allSuccess = true;
        var totalDownloaded = 0L;
        var totalTime = 0L;

        for (int i = 0; i < chunksToTest; i++)
        {
            try
            {
                var start = i * chunkSize;
                var end = Math.Min(start + chunkSize - 1, contentLength - 1);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", YoutubeClientUtils.UaIos);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (resp.IsSuccessStatusCode)
                {
                    var downloaded = 0L;
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    var buffer = new byte[81920];
                    int read;

                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        downloaded += read;
                    }

                    sw.Stop();
                    totalDownloaded += downloaded;
                    totalTime += sw.ElapsedMilliseconds;

                    Console.WriteLine($"  ✅ Chunk {i + 1}/{chunksToTest} | {start / 1024}KB-{end / 1024}KB | {downloaded / 1024}KB | {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Console.WriteLine($"  ❌ Chunk {i + 1}/{chunksToTest} | HTTP {(int)resp.StatusCode}");
                    allSuccess = false;
                    break;
                }

                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ Chunk {i + 1}/{chunksToTest} | {ex.Message}");
                allSuccess = false;
                break;
            }
        }

        Console.WriteLine("\n" + new string('─', 64));
        Console.WriteLine("\nCONCLUSION:\n");

        if (allSuccess)
        {
            var avgSpeed = totalDownloaded / (totalTime / 1000.0) / 1024 / 1024;
            Console.WriteLine($"✅ IOS OPUS streams work with chunked downloads!");
            Console.WriteLine($"   Average speed: {avgSpeed:F1}MB/s");
            Console.WriteLine($"   Total downloaded: {totalDownloaded / 1024:N0}KB in {totalTime}ms");
            Console.WriteLine();
            Console.WriteLine($"✅ YOU CAN USE OPUS ONLY (no AAC needed!)");
            Console.WriteLine($"   → IOS client provides OPUS for Made for Kids");
            Console.WriteLine($"   → Your MemoryFirstCachingStream will work");
            Console.WriteLine($"   → App size: miniaudio + Concentus (~5MB total)");
        }
        else
        {
            Console.WriteLine($"❌ IOS OPUS streams have limitations");
            Console.WriteLine($"   → Need HLS fallback (AAC decoder required)");
        }
    }

    public static async Task TestHeadersBypassAsync()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          TESTING HEADERS BYPASS FOR 1MB LIMIT               ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var videoId = "QSTFVxtPCN0";

        using var baseHandler = new SocketsHttpHandler();
        using var http = new HttpClient(baseHandler);

        var controller = new TestableVideoController(http);
        var response = await controller.GetPlayerResponseWithClientAsync(videoId, "IOS", default);

        var opusStream = response.Streams
            .FirstOrDefault(s => s.AudioCodec == "opus" && s.Itag == 251);

        if (opusStream == null)
        {
            Console.WriteLine("❌ No OPUS stream");
            return;
        }

        var url = opusStream.Url!;

        // Тест 1: Обычные заголовки (базовый - уже провалился)
        Console.WriteLine("Test 1: Standard headers");
        await TestRangeWithHeaders(url, new Dictionary<string, string>
        {
            ["User-Agent"] = YoutubeClientUtils.UaIos
        });

        // Тест 2: Без User-Agent
        Console.WriteLine("\nTest 2: No User-Agent");
        await TestRangeWithHeaders(url, new Dictionary<string, string>());

        // Тест 3: Chrome браузер
        Console.WriteLine("\nTest 3: Chrome User-Agent");
        await TestRangeWithHeaders(url, new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        });

        // Тест 4: Android приложение
        Console.WriteLine("\nTest 4: Android app headers");
        await TestRangeWithHeaders(url, new Dictionary<string, string>
        {
            ["User-Agent"] = "com.google.android.youtube/19.01.35",
            ["X-YouTube-Client-Name"] = "3",
            ["X-YouTube-Client-Version"] = "19.01.35"
        });

        // Тест 5: Без Range (последовательное чтение)
        Console.WriteLine("\nTest 5: Sequential read without Range header");
        await TestSequentialRead(url);
    }

    private static async Task TestRangeWithHeaders(string url, Dictionary<string, string> headers)
    {
        using var http = new HttpClient();

        for (int mb = 1; mb <= 3; mb++)
        {
            try
            {
                var size = mb * 1024 * 1024L;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);

                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, size - 1);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                var marker = response.IsSuccessStatusCode ? "✅" : "❌";
                Console.WriteLine($"  {marker} {mb}MB: HTTP {(int)response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                    break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {mb}MB: {ex.Message}");
                break;
            }

            await Task.Delay(200);
        }
    }

    private static async Task TestSequentialRead(string url)
    {
        // Идея: читать без Range, просто подряд
        // YouTube может не отслеживать total downloaded если нет Range

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaIos);

        // БЕЗ Range!

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  ❌ Initial request failed: {(int)response.StatusCode}");
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while ((read = await stream.ReadAsync(buffer)) > 0 && totalRead < 5 * 1024 * 1024)
        {
            totalRead += read;

            if (totalRead % (1024 * 1024) == 0) // Каждый MB
            {
                Console.WriteLine($"  → {totalRead / 1024 / 1024}MB downloaded ({sw.ElapsedMilliseconds}ms)");
            }
        }

        var success = totalRead >= 2 * 1024 * 1024;
        var marker = success ? "✅" : "❌";
        Console.WriteLine($"  {marker} Total: {totalRead / 1024 / 1024:F1}MB in {sw.ElapsedMilliseconds}ms");
    }

    [GeneratedRegex(@"(\w+[-\w]*)=(?:""([^""]*)""|([^,\s]+))")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"itag[/=](\d+)")]
    private static partial Regex ItagRegex();
}

/// <summary>
/// Обёртка над VideoController для доступа к GetHlsManifestUrlAsync.
/// </summary>
internal class TestableVideoController : VideoController
{
    public TestableVideoController(HttpClient http) : base(http) { }

    // GetHlsManifestUrlAsync уже public в VideoController
}

#endif