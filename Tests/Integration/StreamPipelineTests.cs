using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Services;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// End-to-end тесты полного pipeline: видео → стрим → дешифровка → воспроизведение.
/// <para>
/// Проверяют что вся цепочка работает корректно:
/// <list type="bullet">
///   <item>PlayerResponse возвращает playable видео</item>
///   <item>Stream manifest содержит audio-стримы</item>
///   <item>SigCipher дешифрует подписи</item>
///   <item>NToken дешифрует throttle-токены</item>
///   <item>URL'ы валидны и возвращают аудио-данные</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Параметры тестов (videoId, itag и т.д.) берутся из <see cref="TestConfig"/>.
/// Редактируй test-config.json чтобы изменить их без перекомпиляции.
/// </remarks>
public static class StreamPipelineTests
{
    /// <summary>
    /// Список videoId для тестов из конфигурации.
    /// </summary>
    private static string[] TestVideoIds => TestConfig.Get().Pipeline.TestVideoIds;

    // ══════════════════════════════════════════════════════════════════
    // STREAM RESOLUTION
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Тестирует получение stream manifest и проверку лучшего audio-стрима.
    /// </summary>
    [TestMethod(TestCategory.Integration, "Pipeline: Stream Resolution",
        Order = 10, Group = TestGroups.Pipeline, RequiresNetwork = true, TimeoutSeconds = 60)]
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

        using var request = new HttpRequestMessage(HttpMethod.Head, best.Url);
        using var response = await SharedHttpClient.Instance.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert(response.IsSuccessStatusCode,
            $"Stream URL failed: HTTP {(int)response.StatusCode}");
    }

    /// <summary>
    /// Тестирует параллельное получение manifest для нескольких видео.
    /// </summary>
    [TestMethod(TestCategory.Integration, "Pipeline: Multi-Video",
        Order = 20, Group = TestGroups.Pipeline, RequiresNetwork = true, TimeoutSeconds = 90)]
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

    /// <summary>
    /// Тестирует скачивание первых 64KB audio-данных и проверку формата.
    /// </summary>
    [TestMethod(TestCategory.Integration, "Pipeline: Audio Download",
        Order = 30, Group = TestGroups.Pipeline, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestAudioDownloadAsync(IServiceProvider services)
    {
        var youtube = services.GetRequiredService<YoutubeProvider>().GetClient();
        var videoId = TestVideoIds[0];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            VideoId.Parse(videoId), cts.Token);

        var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        request.Headers.Range = new RangeHeaderValue(0, 65535);

        using var response = await SharedHttpClient.Instance.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent,
            $"Download failed: HTTP {(int)response.StatusCode}");

        var buffer = await response.Content.ReadAsByteArrayAsync(cts.Token);

        bool isWebM = buffer.Length >= 4 &&
            buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3;
        bool isMp4 = buffer.Length >= 8 &&
            buffer[4] == 'f' && buffer[5] == 't' && buffer[6] == 'y' && buffer[7] == 'p';

        Assert(isWebM || isMp4,
            $"Invalid format. Magic: {BitConverter.ToString(buffer, 0, Math.Min(8, buffer.Length))}");

        Log.Info($"[Test] Downloaded {buffer.Length} bytes, format: {(isWebM ? "WebM/Opus" : "MP4/AAC")}");
    }

    // ══════════════════════════════════════════════════════════════════
    // FULL PIPELINE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Полный end-to-end тест: manifest + sig decryption + n-token + URL validation.
    /// </summary>
    [TestMethod(TestCategory.Integration, "Pipeline: Full Pipeline (sig + n-token)",
        Order = 40, Group = TestGroups.Pipeline, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestFullPipelineAsync(IServiceProvider services)
    {
        await TestFullPipelineInternalAsync(services, "dQw4w9WgXcQ");
    }

    /// <summary>
    /// Внутренний метод для запуска full pipeline с произвольным videoId.
    /// Вынесен из атрибутного метода чтобы поддержать legacy вызовы с параметром.
    /// </summary>
    /// <param name="services">DI-контейнер.</param>
    /// <param name="videoId">ID видео для тестирования.</param>
    public static async Task TestFullPipelineInternalAsync(
        IServiceProvider services, string videoId = "dQw4w9WgXcQ")
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

    /// <summary>
    /// Детальная диагностика stream URL для конкретного видео и itag.
    /// Показывает ВСЕ параметры URL и делает тестовый запрос.
    /// </summary>
    /// <remarks>
    /// Параметры берутся из <see cref="TestConfig.PipelineConfig"/>:
    /// <list type="bullet">
    ///   <item><c>debugVideoId</c> — ID видео для диагностики</item>
    ///   <item><c>debugTargetItag</c> — целевой itag (null = любой)</item>
    ///   <item><c>debugTestDownload</c> — скачивать ли тестовый chunk</item>
    /// </list>
    /// </remarks>
    [TestMethod(TestCategory.Integration, "Pipeline: Debug Specific Stream",
        Order = 50, Group = TestGroups.Pipeline, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task DebugSpecificStreamAsync(IServiceProvider services)
    {
        // ═══════════════════════════════════════════════════════════════
        // КОНФИГУРАЦИЯ ИЗ test-config.json
        // ═══════════════════════════════════════════════════════════════

        var config = TestConfig.Get().Pipeline;
        var videoId = config.DebugVideoId;
        var targetItag = config.DebugTargetItag;
        var testDownload = config.DebugTestDownload;

        // ═══════════════════════════════════════════════════════════════

        var youtube = services.GetRequiredService<YoutubeProvider>().GetClient();

        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info($"  DEBUG STREAM: {videoId}");
        Log.Info($"  Target itag: {targetItag?.ToString() ?? "any"}");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // ═══ STEP 1: Get manifest ═══
        Log.Info("[STEP 1] Fetching stream manifest...");
        var sw = Stopwatch.StartNew();

        StreamManifest manifest;
        try
        {
            manifest = await youtube.Videos.Streams.GetManifestAsync(
                VideoId.Parse(videoId), cts.Token);
            sw.Stop();
            Log.Info($"  ✓ Manifest in {sw.ElapsedMilliseconds}ms\n");
        }
        catch (Exception ex)
        {
            Log.Error($"  ✗ Manifest failed: {ex.Message}");
            throw;
        }

        var audioStreams = manifest.GetAudioOnlyStreams().ToList();

        Log.Info($"[STEP 2] Available streams ({audioStreams.Count}):");
        foreach (var s in audioStreams)
        {
            var marker = (targetItag == s.Itag) ? " ◄── TARGET" : "";
            Log.Info($"  itag={s.Itag}: {s.Container}/{s.AudioCodec}, " +
                     $"{s.Bitrate.KiloBitsPerSecond:F0}kbps, " +
                     $"{s.Size.MegaBytes:F1}MB{marker}");
        }

        // ═══ STEP 3: Select stream ═══
        var stream = (targetItag is not null
            ? audioStreams.FirstOrDefault(s => s.Itag == targetItag)
            : audioStreams.FirstOrDefault()) ?? throw new Exception($"Stream itag={targetItag} not found");

        Log.Info($"\n[STEP 3] Selected: itag={stream.Itag}");
        Log.Info($"  Container: {stream.Container}");
        Log.Info($"  Codec: {stream.AudioCodec}");
        Log.Info($"  Bitrate: {stream.Bitrate.KiloBitsPerSecond:F0} kbps");
        Log.Info($"  Size: {stream.Size.MegaBytes:F2} MB");

        // ═══ STEP 4: Analyze URL ═══
        Log.Info("\n[STEP 4] URL Analysis:");
        AnalyzeStreamUrl(stream.Url);

        // ═══ STEP 5: Test download ═══
        if (testDownload)
        {
            Log.Info("\n[STEP 5] Testing download (first 1KB)...");
            await TestStreamDownloadAsync(stream.Url, cts.Token);
        }

        Log.Info("\n═══════════════════════════════════════════════════════════════");
        Log.Info("  DEBUG COMPLETE");
        Log.Info("═══════════════════════════════════════════════════════════════");
    }

    /// <summary>
    /// Кросс-тест: сравнивает RAW URL из PlayerResponse с обработанным URL.
    /// Помогает диагностировать проблемы с n-token и sig decryption.
    /// </summary>
    [TestMethod(TestCategory.Integration, "Pipeline: Phase 3 - Raw URL from PlayerResponse",
    Order = 62, Group = TestGroups.Pipeline, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task CrossTestPhase3Async(IServiceProvider services)
    {
        var youtube = services.GetRequiredService<YoutubeProvider>().GetClient();

        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        Log.Info("═══ PHASE 3: ТЕСТИРУЕМ RAW URL ДО И ПОСЛЕ ОБРАБОТКИ ═══\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // ═══ Получаем PlayerResponse напрямую ═══
        Log.Info("[1] Getting PlayerResponse...");
        var playerResponse = await youtube.Videos.GetPlayerResponseAsync(
            VideoId.Parse(videoId), cts.Token);

        if (!playerResponse.IsPlayable)
        {
            Log.Error($"  Not playable: {playerResponse.PlayabilityError}");
            throw new Exception("Video not playable");
        }

        Log.Info("  ✓ PlayerResponse is playable");

        // ═══ Извлекаем RAW URL из JSON (до любой обработки) ═══
        var streams = playerResponse.Streams.ToList();
        Log.Info($"  Found {streams.Count} streams");

        var targetStream = streams.FirstOrDefault(s => s.Itag == 251)
                        ?? streams.FirstOrDefault(s =>
                            s.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);

        if (targetStream == null)
        {
            Log.Error("  No audio stream found!");
            throw new Exception("No audio stream");
        }

        var rawUrl = targetStream.Url;
        var hasCipher = !string.IsNullOrEmpty(targetStream.Signature);

        Log.Info($"\n  itag={targetStream.Itag}");
        Log.Info($"  Has cipher: {hasCipher}");
        Log.Info($"  Signature param: {targetStream.SignatureParameter ?? "null"}");

        if (hasCipher)
        {
            Log.Info($"  ⚠ URL requires sig decryption (signatureCipher)");
            Log.Info($"  Signature (first 40): {targetStream.Signature![..Math.Min(40, targetStream.Signature!.Length)]}...");
        }

        // ═══ RAW URL Analysis ═══
        Log.Info($"\n[2] RAW URL from PlayerResponse:");
        Log.Info($"  Length: {rawUrl?.Length ?? 0}");
        if (rawUrl != null && rawUrl.Length > 0)
        {
            Log.Info($"  First 200: {rawUrl[..Math.Min(200, rawUrl.Length)]}");
            Log.Info($"  Last 200: {rawUrl[^Math.Min(200, rawUrl.Length)..]}");
        }

        if (string.IsNullOrEmpty(rawUrl))
        {
            Log.Error("  RAW URL is empty!");
            throw new Exception("Empty URL");
        }

        // ═══ Тест RAW URL (без обработки n-token) ═══
        Log.Info("\n[3] Testing RAW URL (no n-token decryption)...");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("Origin", "https://music.youtube.com");
        client.DefaultRequestHeaders.Add("Referer", "https://music.youtube.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        // Тест A: RAW URL + range (без N-token decryption)
        var testUrlA = Core.Youtube.Utils.UrlEx.SetQueryParameter(rawUrl, "range", "0-1023");
        testUrlA = Core.Youtube.Utils.UrlEx.SetQueryParameter(testUrlA, "rn", "1");
        await RunTestWithDetails(client, "A. RAW URL (encrypted n)", testUrlA);

        // Тест B: RAW URL без n (вообще удалим)
        var testUrlB = Core.Youtube.Utils.UrlEx.RemoveQueryParameter(testUrlA, "n");
        await RunTestWithDetails(client, "B. RAW URL (n removed)", testUrlB);

        // ═══ Теперь расшифровываем n-token ═══
        Log.Info("\n[4] Decrypting n-token...");
        var nToken = Core.Youtube.Utils.UrlEx.TryGetQueryParameterValue(rawUrl, "n");
        Log.Info($"  Encrypted n: {nToken}");

        if (!string.IsNullOrEmpty(nToken))
        {
            var decryptor = services.GetRequiredService<Core.Youtube.Bridge.NToken.NTokenDecryptor>();

            try
            {
                var decryptedN = await decryptor.DecryptAsync(nToken, cts.Token);
                Log.Info($"  Decrypted n: {decryptedN}");

                // Тест C: с расшифрованным n
                var testUrlC = Core.Youtube.Utils.UrlEx.SetQueryParameter(testUrlA, "n", decryptedN);
                await RunTestWithDetails(client, "C. RAW URL (n decrypted)", testUrlC);

                // Тест D: с расшифрованным n + убираем мусорные параметры
                var testUrlD = testUrlC;
                testUrlD = Core.Youtube.Utils.UrlEx.RemoveQueryParameter(testUrlD, "ump");
                testUrlD = Core.Youtube.Utils.UrlEx.RemoveQueryParameter(testUrlD, "alr");
                testUrlD = Core.Youtube.Utils.UrlEx.RemoveQueryParameter(testUrlD, "srfvp");
                testUrlD = Core.Youtube.Utils.UrlEx.RemoveQueryParameter(testUrlD, "pot");
                await RunTestWithDetails(client, "D. Processed URL (как StreamClient)", testUrlD);
            }
            catch (Exception ex)
            {
                Log.Error($"  N-token decryption failed: {ex.Message}");
            }
        }

        // ═══ Тест E: получаем URL через нормальный StreamClient pipeline ═══
        Log.Info("\n[5] Full StreamClient pipeline...");
        try
        {
            var manifest = await youtube.Videos.Streams.GetManifestAsync(
                VideoId.Parse(videoId), cts.Token);

            var stream = manifest.GetAudioOnlyStreams()
                .FirstOrDefault(s => s.Itag == 251);

            if (stream != null)
            {
                var pipelineUrl = Core.Youtube.Utils.UrlEx.SetQueryParameter(stream.Url, "range", "0-1023");
                pipelineUrl = Core.Youtube.Utils.UrlEx.SetQueryParameter(pipelineUrl, "rn", "1");
                await RunTestWithDetails(client, "E. StreamClient pipeline URL", pipelineUrl);

                // Сравниваем что изменилось
                Log.Info("\n[6] Diff: RAW vs Pipeline URL:");
                CompareUrls(rawUrl, stream.Url);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"  Pipeline failed: {ex.Message}");
        }

        Log.Info("\n═══ PHASE 3 ЗАВЕРШЕНА ═══");
        Log.Info("A=403, C=200 → N-token был проблемой");
        Log.Info("A=403, C=403 → Проблема в самом PlayerResponse");
        Log.Info("A=200 → N-token throttle (скорость, не доступ)");
    }

    // ══════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════

    private static async Task RunTestWithDetails(HttpClient client, string name, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await client.SendAsync(req);

            var code = (int)res.StatusCode;
            string status = (code is 200 or 206) ? "✅ OK" : $"❌ {code}";

            Log.Info($"  {name,-45} -> {status}");

            if (code == 403)
            {
                var body = await res.Content.ReadAsStringAsync();
                if (body.Length > 0)
                    Log.Info($"    Response: {body[..Math.Min(100, body.Length)]}");
            }
            else if (code is 200 or 206)
            {
                var bytes = await res.Content.ReadAsByteArrayAsync();
                Log.Info($"    Got {bytes.Length} bytes, magic: {BitConverter.ToString(bytes.Take(8).ToArray())}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"  {name,-45} -> ERROR: {ex.Message}");
        }
    }

    private static void CompareUrls(string rawUrl, string processedUrl)
    {
        var rawParams = Core.Youtube.Utils.UrlEx.GetQueryParameters(rawUrl);
        var procParams = Core.Youtube.Utils.UrlEx.GetQueryParameters(processedUrl);

        var allKeys = rawParams.Keys.Union(procParams.Keys).Order();
        int diffCount = 0;
        foreach (var key in allKeys)
        {
            var raw = rawParams.GetValueOrDefault(key, "<MISSING>");
            var proc = procParams.GetValueOrDefault(key, "<MISSING>");
            if (raw == proc) continue;

            diffCount++;
            var rawShort = raw.Length > 30 ? $"{raw[..15]}...{raw[^10..]}" : raw;
            var procShort = proc.Length > 30 ? $"{proc[..15]}...{proc[^10..]}" : proc;

            Log.Info($"    {key,-15} RAW={rawShort}");
            Log.Info($"    {"",15} PRC={procShort}");
        }

        if (diffCount == 0) Log.Info("    No differences (only n-token should differ)");
    }

    /// <summary>
    /// Детальный анализ URL стрима.
    /// </summary>
    private static void AnalyzeStreamUrl(string url)
    {
        var uri = new Uri(url);
        Log.Info($"  Host: {uri.Host}");
        Log.Info($"  Path: {uri.AbsolutePath}");
        Log.Info($"  URL length: {url.Length} chars");

        var queryParams = Core.Youtube.Utils.UrlEx.GetQueryParameters(url);

        Log.Info($"\n  Query parameters ({queryParams.Count}):");

        // Critical params
        var critical = new[] { "n", "sig", "lsig", "c", "expire", "itag", "ip" };

        foreach (var param in queryParams.OrderBy(p => p.Key))
        {
            var isCritical = critical.Contains(param.Key);
            var prefix = isCritical ? "  ★ " : "    ";

            var value = param.Value.Length > 50
                ? $"{param.Value[..25]}...{param.Value[^15..]} ({param.Value.Length}ch)"
                : param.Value;

            Log.Info($"{prefix}{param.Key} = {value}");

            // Detailed analysis for signatures
            if (param.Key is "sig" or "lsig")
            {
                AnalyzeSignatureParam(param.Key, param.Value);
            }

            // Detailed analysis for n-token
            if (param.Key == "n")
            {
                AnalyzeNTokenParam(param.Value);
            }
        }

        // Check missing params
        var missing = critical.Where(p => !queryParams.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            Log.Warn($"\n  ⚠ Missing params: {string.Join(", ", missing)}");
        }
    }

    private static void AnalyzeSignatureParam(string name, string value)
    {
        var decoded = Uri.UnescapeDataString(value);
        var lengthMod4 = decoded.Length % 4;
        var endsWithPadding = decoded.EndsWith("==") || decoded.EndsWith("=");

        Log.Info($"      └─ {name} details:");
        Log.Info($"         Raw length: {value.Length}");
        Log.Info($"         Decoded length: {decoded.Length}");
        Log.Info($"         Length % 4: {lengthMod4}");
        Log.Info($"         Has padding: {endsWithPadding}");
        Log.Info($"         Ends: ...{decoded[^Math.Min(15, decoded.Length)..]}");

        if (lengthMod4 != 0 && !endsWithPadding)
        {
            Log.Warn($"         ⚠ INVALID! Missing {4 - lengthMod4}x '=' padding");
        }
    }

    private static void AnalyzeNTokenParam(string value)
    {
        var looksDecrypted = value.Length is >= 11 and <= 17;
        var looksEncrypted = value.Length is >= 19 and <= 22;

        string status = looksDecrypted ? "✓ Decrypted" :
                        looksEncrypted ? "⚠ ENCRYPTED!" :
                        "? Unknown";

        Log.Info($"      └─ n-token: len={value.Length}, status={status}");
        Log.Info($"         Value: {value}");
    }

    /// <summary>
    /// Тестирует скачивание первого KB стрима.
    /// </summary>
    private static async Task TestStreamDownloadAsync(string url, CancellationToken ct)
    {
        var testUrl = Core.Youtube.Utils.UrlEx.SetQueryParameter(url, "range", "0-1023");
        testUrl = Core.Youtube.Utils.UrlEx.SetQueryParameter(testUrl, "rn", "1");

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, testUrl);

        // Browser-like headers
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.Add("Origin", "https://music.youtube.com");
        request.Headers.Add("Referer", "https://music.youtube.com/");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
        }
        catch (Exception ex)
        {
            Log.Error($"  ✗ Request failed: {ex.Message}");
            return;
        }

        Log.Info($"  Status: {(int)response.StatusCode} {response.StatusCode}");
        Log.Info($"  Time: {sw.ElapsedMilliseconds}ms");

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent)
        {
            var content = await response.Content.ReadAsByteArrayAsync(ct);
            Log.Info($"  ✓ Downloaded {content.Length} bytes");
            Log.Info($"  ✓ Magic bytes: {BitConverter.ToString(content.Take(16).ToArray())}");

            // Check format
            bool isWebM = content.Length >= 4 &&
                content[0] == 0x1A && content[1] == 0x45 &&
                content[2] == 0xDF && content[3] == 0xA3;
            bool isMp4 = content.Length >= 8 &&
                content[4] == 'f' && content[5] == 't' &&
                content[6] == 'y' && content[7] == 'p';

            Log.Info($"  Format: {(isWebM ? "WebM/Opus" : isMp4 ? "MP4/AAC" : "Unknown")}");
        }
        else
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            Log.Error($"  ✗ Error: {errorBody[..Math.Min(300, errorBody.Length)]}");

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                Log.Error("\n  ═══ 403 FORBIDDEN ANALYSIS ═══");
                Log.Error("  Possible causes:");
                Log.Error("    1. n-token NOT decrypted (still encrypted)");
                Log.Error("    2. sig/lsig missing Base64 padding (== or =)");
                Log.Error("    3. URL expired (check 'expire' timestamp)");
                Log.Error("    4. Wrong 'c' client parameter");
                Log.Error("    5. IP mismatch or geo-blocking");
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}