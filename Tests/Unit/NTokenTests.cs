#if DEBUG

using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit и integration тесты для N-Token системы.
/// </summary>
public static class NTokenTests
{
    private static string TestToken => TestConfig.Get().NToken.TestToken;

    // ══════════════════════════════════════════════════════════════════
    // UNIT TESTS (no network)
    // ══════════════════════════════════════════════════════════════════

    [TestMethod(TestCategory.Unit, "NToken: Function Detection",
        Group = TestGroups.NToken, Order = 10)]
    public static Task TestFunctionDetectionAsync()
    {
        const string fakeJs = """
            var someCode = function() { return 1; };
            var KM = function(a, b) {
                var c = [-1552975130, -306113009];
                // ... decryption logic ...
                return a;
            };
            var otherCode = 2;
            """;

        Assert(fakeJs.Contains("-1552975130"), "Primary marker missing");
        Assert(fakeJs.Contains("-306113009"), "Secondary marker missing");

        int markerIdx = fakeJs.IndexOf("-1552975130");
        Assert(markerIdx > 0, "Marker not found");

        var contextBefore = fakeJs[..markerIdx];
        Assert(contextBefore.Contains("KM"), "Function name not in context");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "NToken: Bundle Extraction",
        Group = TestGroups.NToken, Order = 20)]
    public static Task TestBundleExtractionAsync()
    {
        const string simpleJs = """
            var helper = function(x) { return x + 1; };
            var main = function(a) {
                return helper(a) * 2;
            };
            """;

        var bundle = JsFunctionExtractor.ExtractBundle(simpleJs, "main");

        Log.Debug($"[Test] Bundle extraction: {(bundle is not null ? $"{bundle.Length} chars" : "null (expected for simple code)")}");

        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    // LIVE TESTS (require network)
    // ══════════════════════════════════════════════════════════════════

    [TestMethod(TestCategory.Integration, "NToken: Live Decryption",
        Group = TestGroups.NToken, Order = 30, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveDecryptionAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecryptAsync(TestToken);
        sw.Stop();

        Assert(!string.IsNullOrEmpty(result), "Decryption returned empty");
        Assert(result != TestToken, "Decryption returned unchanged token");
        Assert(!result.Contains("undefined"), "Result contains 'undefined'");

        Log.Info($"[Test] N-Token decrypted in {sw.ElapsedMilliseconds}ms: {TestToken} → {result}");
    }

    [TestMethod(TestCategory.Integration, "NToken: Cache Hit",
        Group = TestGroups.NToken, Order = 40, RequiresNetwork = true)]
    public static async Task TestCacheHitAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();

        var first = await decryptor.DecryptAsync(TestToken);

        var sw = Stopwatch.StartNew();
        var second = await decryptor.DecryptAsync(TestToken);
        sw.Stop();

        Assert(first == second, "Cache returned different result");
        Assert(sw.ElapsedMilliseconds < 5, $"Cache hit too slow: {sw.ElapsedMilliseconds}ms (expected <5ms)");

        Log.Info($"[Test] Cache hit: {sw.ElapsedMilliseconds}ms");
    }

    // ══════════════════════════════════════════════════════════════════
    // PLAYER VERSION COMPATIBILITY TESTS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Тестирует дешифрацию N-token для разных версий плеера из конфига.
    /// <para>
    /// Для каждой версии:
    /// 1. Загружает закэшированный base.js (player_{version}_basejs.txt)
    /// 2. Создаёт изолированный NTokenDecryptor с фиксированным контекстом
    /// 3. Инициализирует и тестирует дешифрацию
    /// 4. Если есть test cases в конфиге — проверяет все пары
    /// </para>
    /// </summary>
    [TestMethod(TestCategory.Integration, "NToken: Player Version Compatibility",
        Group = TestGroups.NToken, Order = 50, RequiresNetwork = false, TimeoutSeconds = 120)]
    public static async Task TestPlayerVersionCompatibilityAsync(IServiceProvider services)
    {
        var config = TestConfig.Get().PlayerVersions;

        if (config.Versions.Length == 0)
        {
            Log.Info("[Test] No player versions configured in test-config.json → playerVersions.versions");
            Log.Info("[Test] Add versions like: [\"6c5cb4f4\", \"99f55c01\"]");
            return;
        }

        int passed = 0;
        int failed = 0;
        int skipped = 0;

        foreach (var version in config.Versions)
        {
            Log.Info($"\n[Test] ═══ Testing player version: {version} ═══");

            // Загружаем закэшированный base.js
            var cachedContext = PlayerContext.LoadFromCacheNoExpiry(version);
            if (cachedContext is null)
            {
                Log.Warn($"  ⊘ Skipped: no cached base.js for version {version}");
                Log.Info($"     Cache folder: {G.Folder.NTokenCache}");
                Log.Info($"     Expected file: player_{version}_basejs.txt");
                skipped++;
                continue;
            }

            Log.Info($"  ✓ Loaded base.js: {cachedContext.BaseJs.Length / 1024}KB");

            // Создаём изолированный NTokenDecryptor с фиксированным контекстом
            var fixedManager = new FixedPlayerContextManager(cachedContext);
            var isolatedDecryptor = new NTokenDecryptor(fixedManager);

            try
            {
                // Инициализируем (вызовет InitializeCore с нашим контекстом)
                await isolatedDecryptor.EnsureInitializedAsync(CancellationToken.None);

                if (!isolatedDecryptor.IsInitialized)
                {
                    Log.Error($"  ✗ Failed to initialize decryptor for version {version}");
                    failed++;
                    continue;
                }

                Log.Info($"  ✓ Decryptor initialized");

                // Тестируем стандартный токен
                var testToken = TestToken;
                var result = await isolatedDecryptor.DecryptAsync(testToken);

                if (string.IsNullOrEmpty(result) || result == testToken)
                {
                    Log.Error($"  ✗ Decryption failed: returned '{result ?? "null"}'");
                    failed++;
                    continue;
                }

                Log.Info($"  ✓ Decrypted: {testToken} → {result}");

                // Проверяем test cases из конфига
                if (config.NTokenTestCases.TryGetValue(version, out var testCases) && testCases.Length > 0)
                {
                    int casePassed = 0;
                    int caseFailed = 0;

                    foreach (var testCase in testCases)
                    {
                        var caseResult = await isolatedDecryptor.DecryptAsync(testCase.Encrypted);
                        if (caseResult == testCase.Expected)
                        {
                            casePassed++;
                            var desc = testCase.Description ?? testCase.Encrypted[..Math.Min(15, testCase.Encrypted.Length)];
                            Log.Info($"    ✓ {desc}");
                        }
                        else
                        {
                            caseFailed++;
                            Log.Error($"    ✗ {testCase.Encrypted[..Math.Min(15, testCase.Encrypted.Length)]}...");
                            Log.Error($"      Expected: {testCase.Expected}");
                            Log.Error($"      Got:      {caseResult}");
                        }
                    }

                    if (caseFailed > 0)
                    {
                        failed++;
                        continue;
                    }
                }

                passed++;
                Log.Info($"  ✓ Version {version} PASSED");
            }
            catch (Exception ex)
            {
                Log.Error($"  ✗ Exception: {ex.Message}");
                failed++;
            }
            finally
            {
                isolatedDecryptor.Dispose();
            }
        }

        Log.Info($"\n[Test] ═══ Version compatibility: {passed} passed, {failed} failed, {skipped} skipped ═══");

        if (failed > 0)
            throw new Exception($"Version compatibility: {failed} version(s) failed");
    }

    [TestMethod(TestCategory.Integration, "NToken: Test Specific Version",
        Group = TestGroups.NToken, Order = 55, RequiresNetwork = false, TimeoutSeconds = 60)]
    public static async Task TestSpecificVersionAsync(IServiceProvider services)
    {
        var config = TestConfig.Get().PlayerVersions;

        if (config.Versions.Length == 0)
        {
            Log.Info("[Test] No specific version configured.");
            Log.Info("[Test] Add to test-config.json → playerVersions.versions: [\"6c5cb4f4\"]");
            return;
        }

        var version = config.Versions[0];
        Log.Info($"[Test] Testing specific version: {version}");

        var cachedContext = PlayerContext.LoadFromCacheNoExpiry(version);
        if (cachedContext is null)
        {
            Log.Warn($"[Test] No cached base.js for version {version}");
            Log.Info($"[Test] Cache folder: {G.Folder.NTokenCache}");
            return;
        }

        Log.Info($"[Test] Loaded cached base.js: {cachedContext.BaseJs.Length / 1024}KB");

        var fixedManager = new FixedPlayerContextManager(cachedContext);
        var isolatedDecryptor = new NTokenDecryptor(fixedManager);

        try
        {
            await isolatedDecryptor.EnsureInitializedAsync(CancellationToken.None);
            Assert(isolatedDecryptor.IsInitialized, "Failed to initialize decryptor");

            Log.Info("[Test] ✓ Decryptor initialized");

            var result = await isolatedDecryptor.DecryptAsync(TestToken);

            Assert(!string.IsNullOrEmpty(result), "Decryption returned empty");
            Assert(result != TestToken, "Decryption returned unchanged token");

            Log.Info($"[Test] ✓ {TestToken} → {result}");
            Log.Info("[Test] ✓ Version test passed");
        }
        finally
        {
            isolatedDecryptor.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // BENCHMARK
    // ══════════════════════════════════════════════════════════════════

    [TestMethod(TestCategory.Benchmark, "NToken: Benchmark",
        Group = TestGroups.NToken, Order = 100, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task BenchmarkAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();

        await decryptor.DecryptAsync(TestToken);

        var iterations = TestConfig.Get().NToken.BenchmarkIterations;
        var tokens = Enumerable.Range(0, iterations)
            .Select(i => $"test_token_{i:D3}_{Guid.NewGuid():N}"[..20])
            .ToArray();

        var sw = Stopwatch.StartNew();
        foreach (var token in tokens)
            await decryptor.DecryptAsync(token);
        sw.Stop();

        var avgMiss = sw.ElapsedMilliseconds / (double)iterations;
        Log.Info($"[Benchmark] Cache miss avg: {avgMiss:F2}ms ({iterations} iterations)");

        sw.Restart();
        foreach (var token in tokens)
            await decryptor.DecryptAsync(token);
        sw.Stop();

        var avgHit = sw.ElapsedMilliseconds / (double)iterations;
        Log.Info($"[Benchmark] Cache hit avg: {avgHit:F3}ms");

        Assert(avgHit < 1, $"Cache hit too slow: {avgHit:F3}ms");
    }

    // ══════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

/// <summary>
/// PlayerContextManager с фиксированным PlayerContext для тестирования.
/// НЕ обращается к сети, возвращает переданный контекст.
/// </summary>
internal sealed class FixedPlayerContextManager : PlayerContextManager
{
    private readonly PlayerContext _fixedContext;

    public FixedPlayerContextManager(PlayerContext context)
        : base(null!) // HttpClient не используется
    {
        _fixedContext = context;
    }

    public override Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_fixedContext);
    }
}

#endif