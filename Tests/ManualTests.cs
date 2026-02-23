#if DEBUG

using System.Diagnostics;
using LMP.Tests.Unit;
using LMP.Tests.Integration;

namespace LMP.Tests;

/// <summary>
/// Точка входа для всех тестов.
/// Запуск: F10 в Debug режиме.
/// </summary>
public static class ManualTests
{
    public static async Task RunAllAsync()
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine("\n" + new string('═', 70));
        Console.WriteLine("  LMP TEST SUITE");
        Console.WriteLine(new string('═', 70) + "\n");

        var results = new TestResults();

        // ══════════════════════════════════════════════════════════════
        // UNIT TESTS (no network)
        // ══════════════════════════════════════════════════════════════

        Console.WriteLine("▶ UNIT TESTS (no network)\n");

        await results.RunAsync("SigCipher.Manifest.Serialize",
            SigCipherTests.TestManifestSerializationAsync);

        await results.RunAsync("SigCipher.Manifest.Decipher",
            SigCipherTests.TestManifestDecipherAsync);

        await results.RunAsync("SigCipher.Solver.KnownPatterns",
            SigCipherTests.TestSolverKnownPatternsAsync);

        await results.RunAsync("SigCipher.Solver.RandomInputs",
            SigCipherTests.TestSolverRandomInputsAsync);

        await results.RunAsync("SigCipher.Extractor.ParseDictArray",
            SigCipherTests.TestParseDictArrayAsync);

        await results.RunAsync("SigCipher.Extractor.DetectMethods",
            SigCipherTests.TestDetectMethodsAsync);

        await results.RunAsync("NToken.FunctionDetection",
            NTokenTests.TestFunctionDetectionAsync);

        await results.RunAsync("NToken.BundleExtraction",
            NTokenTests.TestBundleExtractionAsync);

        await results.RunAsync("Cache.MemoryCache",
            CacheTests.TestMemoryCacheAsync);

        await results.RunAsync("Cache.DiskRoundtrip",
            CacheTests.TestDiskRoundtripAsync);

        await results.RunAsync("SigSolver.AllKnownPatterns",
SigCipherSolverTests.TestAllKnownPatternsAsync);

        await results.RunAsync("SigSolver.AllSwapValues",
            SigCipherSolverTests.TestAllSwapValuesAsync);

        await results.RunAsync("SigSolver.DoubleSwapRange",
            SigCipherSolverTests.TestDoubleSwapRangeAsync);

        await results.RunAsync("SigSolver.SignatureLengths",
            SigCipherSolverTests.TestVariousSignatureLengthsAsync);

        await results.RunAsync("SigSolver.SpliceValues",
            SigCipherSolverTests.TestAllSpliceValuesAsync);

        await results.RunAsync("SigSolver.RealisticSigs",
            SigCipherSolverTests.TestRealisticSignaturesAsync);

        await results.RunAsync("SigSolver.RandomCombos",
            SigCipherSolverTests.TestRandomCombinationsAsync);

        await results.RunAsync("SigSolver.EdgeCases",
            SigCipherSolverTests.TestEdgeCasesAsync);

        await results.RunAsync("SigSolver.Parallel",
            SigCipherSolverTests.TestParallelSolverAsync);

        await results.RunAsync("SigSolver.Benchmark",
            SigCipherSolverTests.BenchmarkSolverAsync);

        // ══════════════════════════════════════════════════════════════
        // INTEGRATION TESTS (network required)
        // ══════════════════════════════════════════════════════════════

        Console.WriteLine("\n▶ INTEGRATION TESTS (network required)\n");

        await results.RunAsync("NToken.LiveDecryption",
            () => NTokenTests.TestLiveDecryptionAsync(Program.Services));

        await results.RunAsync("NToken.CacheHit",
            () => NTokenTests.TestCacheHitAsync(Program.Services));

        await results.RunAsync("SigCipher.LiveExtraction",
            () => SigCipherTests.TestLiveExtractionAsync(Program.Services));

        await results.RunAsync("SigCipher.LiveDecryption",
            () => SigCipherTests.TestLiveDecryptionAsync(Program.Services));

        await results.RunAsync("Pipeline.StreamResolution",
            () => StreamPipelineTests.TestStreamResolutionAsync(Program.Services));

        await results.RunAsync("Pipeline.MultiVideo",
            () => StreamPipelineTests.TestMultiVideoAsync(Program.Services));

        await results.RunAsync("Pipeline.AudioDownload",
            () => StreamPipelineTests.TestAudioDownloadAsync(Program.Services));

        // ══════════════════════════════════════════════════════════════
        // RESULTS
        // ══════════════════════════════════════════════════════════════

        sw.Stop();
        Console.WriteLine("\n" + new string('═', 70));
        Console.WriteLine($"  RESULTS: {results.Passed} passed, {results.Failed} failed " +
                         $"({sw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine(new string('═', 70) + "\n");

        if (results.Failed > 0)
        {
            Console.WriteLine("❌ FAILED TESTS:");
            foreach (var failure in results.Failures)
                Console.WriteLine($"   • {failure}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // QUICK TESTS (для отдельного запуска)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Быстрый тест N-Token (самый важный).</summary>
    public static Task TestNTokenQuickAsync() =>
        NTokenTests.TestLiveDecryptionAsync(Program.Services);

    /// <summary>Быстрый тест Sig Cipher.</summary>
    public static Task TestSigCipherQuickAsync() =>
        SigCipherTests.TestLiveDecryptionAsync(Program.Services);

    /// <summary>Полный pipeline тест.</summary>
    public static Task TestSigCipherFullAsync(string videoId = "dQw4w9WgXcQ") =>
        StreamPipelineTests.TestFullPipelineAsync(Program.Services, videoId);

    /// <summary>Полный тест солвера.</summary>
    public static Task TestSolverFullAsync() =>
        SigCipherSolverTests.RunAllAsync();

    /// <summary>Benchmark N-Token.</summary>
    public static Task BenchmarkNTokenAsync() =>
        NTokenTests.BenchmarkAsync(Program.Services);
}

/// <summary>Аккумулятор результатов тестов.</summary>
file sealed class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public List<string> Failures { get; } = [];

    public async Task RunAsync(string name, Func<Task> test)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await test();
            sw.Stop();
            Console.WriteLine($"  ✓ {name} ({sw.ElapsedMilliseconds}ms)");
            Passed++;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ✗ {name}: {ex.Message}");
            Failures.Add($"{name}: {ex.Message}");
            Failed++;
        }
    }
}

#endif