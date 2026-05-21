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

            var fixedManager = new FixedPlayerContextManager(cachedContext);
            var isolatedDecryptor = new NTokenDecryptor(fixedManager);

            try
            {
                await isolatedDecryptor.EnsureInitializedAsync(CancellationToken.None);

                if (!isolatedDecryptor.IsInitialized)
                {
                    Log.Error($"  ✗ Failed to initialize decryptor for version {version}");
                    failed++;
                    continue;
                }

                Log.Info($"  ✓ Decryptor initialized");

                var testToken = TestToken;
                var result = await isolatedDecryptor.DecryptAsync(testToken);

                if (string.IsNullOrEmpty(result) || result == testToken)
                {
                    Log.Error($"  ✗ Decryption failed: returned '{result ?? "null"}'");
                    failed++;
                    continue;
                }

                Log.Info($"  ✓ Decrypted: {testToken} → {result}");

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

    /// <summary>
    /// Интеграционный тест компиляции и вызова AST-солвера
    /// для реальных закэшированных версий плеера YouTube.
    /// Переведён с Jint на нативный движок QuickJS.
    /// </summary>
    [TestMethod(TestCategory.Unit, "AST: Solver Integration (cached players)",
        Group = TestGroups.Solver, Order = 65, RequiresNetwork = false, TimeoutSeconds = 60)]
    public static Task TestAstSolverOnCachedPlayerAsync()
    {
        var targetVersions = new[] { "e1bd44b2" }; // Пример версии
        PlayerContext? cachedContext = null;
        string? usedVersion = null;

        foreach (var version in targetVersions)
        {
            cachedContext = PlayerContext.LoadFromCacheNoExpiry(version);
            if (cachedContext is not null)
            {
                usedVersion = version;
                break;
            }
        }

        if (cachedContext is null)
        {
            Log.Warn("[Test] AST Solver Integration skipped: none of the target player versions " +
                     "(e1bd44b2) found in cache.");
            return Task.CompletedTask;
        }

        Log.Info($"[Test] Loaded player_{usedVersion}_basejs.txt ({cachedContext.BaseJs.Length / 1024} KB)");

        var sw = Stopwatch.StartNew();
        var preprocessedJs = YoutubeAstSolver.PreprocessPlayer(cachedContext.BaseJs);
        sw.Stop();

        Log.Info($"[Test] Preprocessed JS in {sw.ElapsedMilliseconds}ms (size: {preprocessedJs.Length / 1024} KB)");

        Assert(preprocessedJs.Contains("_result.n"), "Preprocessed script must assign _result.n");
        Assert(preprocessedJs.Contains("_result.sig"), "Preprocessed script must assign _result.sig");

        // Тестируем N-Token через QuickJS
        const string challenge = "SRGmkqJSCYsuk5i_"; 
        var decodedN = QuickJsDecryptor.Decrypt(preprocessedJs, "n", challenge);
        
        Assert(!string.IsNullOrEmpty(decodedN), "Decoded n-token is empty");
        Assert(decodedN != challenge, "Decoded n-token is unchanged");

        // Тестируем SigCipher через QuickJS
        const string sigChallenge = "AHEqNM4wRgIhAMe2mLCDjcBI5D7GvHp3XpDiVkWAC2IDigK-31PHnIcjAiEAsNZGAuKJz1WaThEs-uPkzZNg8npoYicx9NljtEXkH4k%3D";
        var decodedSig = QuickJsDecryptor.Decrypt(preprocessedJs, "sig", sigChallenge);
        
        Log.Info($"[Test] AST Solver verified Sig! Decoded sig: '{sigChallenge}' -> '{decodedSig}'");

        Assert(!string.IsNullOrEmpty(decodedSig), "Decoded signature is empty");
        Assert(decodedSig != sigChallenge, "Decoded signature is unchanged");

        Log.Info($"[Test] AST Solver verified successfully! Decoded challenge: '{challenge}' → '{decodedN}'");

        return Task.CompletedTask;
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

internal sealed class FixedPlayerContextManager : PlayerContextManager
{
    private readonly PlayerContext _fixedContext;

    public FixedPlayerContextManager(PlayerContext context)
        : base(null!)
    {
        _fixedContext = context;
    }

    public override Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_fixedContext);
    }
}