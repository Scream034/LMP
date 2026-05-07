using System.Diagnostics;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Исчерпывающие тесты SigCipherSolver.
/// <para>
/// Покрывает:
/// <list type="bullet">
///   <item>Все известные паттерны YouTube</item>
///   <item>Диапазон swap-параметров 1-99</item>
///   <item>Все splice-значения 1-3</item>
///   <item>Различные длины подписей 85-115</item>
///   <item>Random-комбинации для стресс-теста</item>
///   <item>Edge cases: null, empty, invalid</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Параметры стресс-теста и benchmark'а берутся из <see cref="TestConfig.SolverConfig"/>.
/// </remarks>
public static class SigCipherSolverTests
{
    /// <summary>Стандартная YouTube-подпись: 105 символов.</summary>
    private static readonly string StandardSignature =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop" +
        "qrstu";

    /// <summary>Реалистичная подпись с символами YouTube.</summary>
    private static readonly string RealisticSignature =
        "Q=wwXNghQ_T04B8uJpMZ5sWyAJIXNs3cqJRjYS6AJrTK8CQIAA" +
        "8761Wv9lwNxVVHqF2m1E5dR3TkGpOcLbIfUz0aDSeYWhXiMnKJ";

    // ═══════════════════════════════════════════════════════════════
    // 1. КАЖДЫЙ ПАТТЕРН ОТДЕЛЬНО
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Тестирует каждый известный паттерн YouTube отдельно.</summary>
    [TestMethod(TestCategory.Unit, "Solver: All Known Patterns", Group = TestGroups.Solver, Order = 10)]
    public static Task TestAllKnownPatternsAsync()
    {
        var patterns = new (string Name, SigCipherOperation[] Ops)[]
        {
            ("swap→reverse→swap→splice(1)", [
                new(SigCipherOpType.Swap, 64),
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Swap, 56),
                new(SigCipherOpType.Splice, 1),
            ]),
            ("swap→swap→reverse→splice(1)", [
                new(SigCipherOpType.Swap, 51),
                new(SigCipherOpType.Swap, 44),
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Splice, 1),
            ]),
            ("reverse→swap→swap→splice(2)", [
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Swap, 48),
                new(SigCipherOpType.Swap, 67),
                new(SigCipherOpType.Splice, 2),
            ]),
            ("reverse→swap→splice(2)", [
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Swap, 48),
                new(SigCipherOpType.Splice, 2),
            ]),
            ("swap→reverse→splice(1)", [
                new(SigCipherOpType.Swap, 72),
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Splice, 1),
            ]),
            ("swap→splice(3)", [
                new(SigCipherOpType.Swap, 55),
                new(SigCipherOpType.Splice, 3),
            ]),
            ("reverse→splice(1)", [
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Splice, 1),
            ]),
            ("swap→swap→swap→splice(1)", [
                new(SigCipherOpType.Swap, 42),
                new(SigCipherOpType.Swap, 67),
                new(SigCipherOpType.Swap, 53),
                new(SigCipherOpType.Splice, 1),
            ]),
            ("swap→reverse→swap→swap→splice(2)", [
                new(SigCipherOpType.Swap, 60),
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Swap, 45),
                new(SigCipherOpType.Swap, 71),
                new(SigCipherOpType.Splice, 2),
            ]),
        };

        int passed = 0;
        int failed = 0;

        foreach (var (name, ops) in patterns)
        {
            var manifest = new SigCipherManifest("test", ops, "test");
            var encrypted = StandardSignature;
            var decrypted = manifest.Decipher(encrypted);

            var sw = Stopwatch.StartNew();
            var solved = SigCipherSolver.Solve(encrypted, decrypted);
            sw.Stop();

            if (solved is not null)
            {
                var check = new SigCipherManifest("v", solved, "c").Decipher(encrypted);
                if (check == decrypted)
                {
                    passed++;
                    Log.Info($"  ✓ {name} ({sw.ElapsedMilliseconds}ms)");
                    continue;
                }
            }

            failed++;
            Log.Error($"  ✗ {name} ({sw.ElapsedMilliseconds}ms)");
        }

        Assert(failed == 0, $"Known patterns: {failed}/{patterns.Length} FAILED");
        Log.Info($"[SolverTest] All {passed} known patterns passed");

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. ДИАПАЗОН SWAP-ПАРАМЕТРОВ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Проверяет что solver находит решение для ВСЕХ swap-значений 1-99.
    /// Это критический тест: YouTube может использовать любое значение.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Solver: All Swap Values (1-99)", Group = TestGroups.Solver, Order = 20)]
    public static Task TestAllSwapValuesAsync()
    {
        int passed = 0;
        int failed = 0;
        var failedValues = new List<int>();

        for (int swapVal = 1; swapVal <= 99; swapVal++)
        {
            var ops = new SigCipherOperation[]
            {
                new(SigCipherOpType.Swap, swapVal),
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Splice, 1),
            };

            var manifest = new SigCipherManifest("v", ops, "t");
            var decrypted = manifest.Decipher(StandardSignature);

            var solved = SigCipherSolver.Solve(StandardSignature, decrypted);
            if (solved is not null)
            {
                var check = new SigCipherManifest("v", solved, "c").Decipher(StandardSignature);
                if (check == decrypted) { passed++; continue; }
            }

            failed++;
            failedValues.Add(swapVal);
        }

        if (failedValues.Count > 0)
            Log.Error($"[SolverTest] Failed swap values: {string.Join(", ", failedValues)}");

        Assert(failed == 0, $"Swap values: {failed}/99 FAILED");
        Log.Info($"[SolverTest] All 99 swap values passed");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Тестирует двойные swap'ы с разными комбинациями параметров.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Solver: Double Swap Range", Group = TestGroups.Solver, Order = 30)]
    public static Task TestDoubleSwapRangeAsync()
    {
        int passed = 0;
        int failed = 0;
        int total = 0;

        for (int s1 = 1; s1 <= 99; s1 += 10)
        {
            for (int s2 = 1; s2 <= 99; s2 += 5)
            {
                total++;
                var ops = new SigCipherOperation[]
                {
                    new(SigCipherOpType.Swap, s1),
                    new(SigCipherOpType.Reverse, 0),
                    new(SigCipherOpType.Swap, s2),
                    new(SigCipherOpType.Splice, 1),
                };

                var manifest = new SigCipherManifest("v", ops, "t");
                var decrypted = manifest.Decipher(StandardSignature);
                var solved = SigCipherSolver.Solve(StandardSignature, decrypted);

                if (solved is not null)
                {
                    var check = new SigCipherManifest("v", solved, "c")
                        .Decipher(StandardSignature);
                    if (check == decrypted) { passed++; continue; }
                }

                failed++;
            }
        }

        Assert(failed == 0, $"Double swap: {failed}/{total} FAILED");
        Log.Info($"[SolverTest] Double swap: {passed}/{total} passed");

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. РАЗЛИЧНЫЕ ДЛИНЫ ПОДПИСИ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// YouTube подписи бывают 90-110 символов. Проверяем весь диапазон.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Solver: Signature Lengths (85-115)", Group = TestGroups.Solver, Order = 40)]
    public static Task TestVariousSignatureLengthsAsync()
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_=+/";

        var rng = new Random(12345);
        int passed = 0;
        int failed = 0;

        var ops = new SigCipherOperation[]
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };

        for (int len = 85; len <= 115; len++)
        {
            var input = new string(Enumerable.Range(0, len)
                .Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());

            var manifest = new SigCipherManifest("v", ops, "t");
            var decrypted = manifest.Decipher(input);
            var solved = SigCipherSolver.Solve(input, decrypted);

            if (solved is not null)
            {
                var check = new SigCipherManifest("v", solved, "c").Decipher(input);
                if (check == decrypted) { passed++; continue; }
            }

            failed++;
            Log.Warn($"  Length {len}: FAILED");
        }

        Assert(failed == 0, $"Variable lengths: {failed}/31 FAILED");
        Log.Info($"[SolverTest] All {passed} signature lengths passed");

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. ВСЕ SPLICE-ЗНАЧЕНИЯ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Проверяет все реальные splice-значения: 1, 2, 3.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Solver: All Splice Values (1-3)", Group = TestGroups.Solver, Order = 50)]
    public static Task TestAllSpliceValuesAsync()
    {
        int passed = 0;

        for (int splice = 1; splice <= 3; splice++)
        {
            var ops = new SigCipherOperation[]
            {
                new(SigCipherOpType.Swap, 52),
                new(SigCipherOpType.Reverse, 0),
                new(SigCipherOpType.Swap, 68),
                new(SigCipherOpType.Splice, splice),
            };

            var manifest = new SigCipherManifest("v", ops, "t");
            var decrypted = manifest.Decipher(StandardSignature);
            var solved = SigCipherSolver.Solve(StandardSignature, decrypted);

            Assert(solved is not null, $"Splice {splice}: not solved");
            var check = new SigCipherManifest("v", solved!, "c").Decipher(StandardSignature);
            Assert(check == decrypted, $"Splice {splice}: verification failed");
            passed++;
        }

        Log.Info($"[SolverTest] All {passed} splice values passed");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. РЕАЛИСТИЧНЫЕ ПОДПИСИ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Тестирует солвер на реалистичной подписи с символами YouTube.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Solver: Realistic Signatures", Group = TestGroups.Solver, Order = 60)]
    public static Task TestRealisticSignaturesAsync()
    {
        var ops = new SigCipherOperation[]
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };

        var manifest = new SigCipherManifest("v", ops, "t");
        var decrypted = manifest.Decipher(RealisticSignature);
        var solved = SigCipherSolver.Solve(RealisticSignature, decrypted);

        Assert(solved is not null, "Realistic signature: not solved");
        var check = new SigCipherManifest("v", solved!, "c").Decipher(RealisticSignature);
        Assert(check == decrypted, "Realistic signature: verification failed");

        Log.Info("[SolverTest] Realistic signature passed");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. СТРЕСС-ТЕСТ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// N случайных комбинаций паттернов и параметров.
    /// Допускается не более 2% фейлов (из-за коллизий символов).
    /// </summary>
    /// <remarks>
    /// Количество комбинаций берётся из <see cref="TestConfig.SolverConfig.RandomCombinationsCount"/>.
    /// </remarks>
    [TestMethod(TestCategory.Unit, "Solver: Random Combinations", Group = TestGroups.Solver, Order = 70,
        TimeoutSeconds = 60)]
    public static Task TestRandomCombinationsAsync()
    {
        var rng = new Random(42);
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_=";

        var patternTemplates = new SigCipherOpType[][]
        {
            [SigCipherOpType.Swap, SigCipherOpType.Reverse, SigCipherOpType.Swap, SigCipherOpType.Splice],
            [SigCipherOpType.Swap, SigCipherOpType.Swap, SigCipherOpType.Reverse, SigCipherOpType.Splice],
            [SigCipherOpType.Reverse, SigCipherOpType.Swap, SigCipherOpType.Splice],
            [SigCipherOpType.Swap, SigCipherOpType.Reverse, SigCipherOpType.Splice],
        };

        int passed = 0;
        int failed = 0;
        var totalTests = TestConfig.Get().Solver.RandomCombinationsCount;

        var sw = Stopwatch.StartNew();

        for (int t = 0; t < totalTests; t++)
        {
            var template = patternTemplates[rng.Next(patternTemplates.Length)];
            var ops = template.Select(type => type switch
            {
                SigCipherOpType.Swap => new SigCipherOperation(type, rng.Next(1, 100)),
                SigCipherOpType.Splice => new SigCipherOperation(type, rng.Next(1, 4)),
                _ => new SigCipherOperation(type, 0),
            }).ToList();

            int len = rng.Next(90, 110);
            var input = new string(Enumerable.Range(0, len)
                .Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());

            var manifest = new SigCipherManifest("v", ops, "t");
            var decrypted = manifest.Decipher(input);
            var solved = SigCipherSolver.Solve(input, decrypted);

            if (solved is not null)
            {
                var check = new SigCipherManifest("v", solved, "c").Decipher(input);
                if (check == decrypted) { passed++; continue; }
            }

            failed++;
        }

        sw.Stop();

        int maxAllowedFailures = totalTests * 2 / 100;
        Assert(failed <= maxAllowedFailures,
            $"Random: {failed}/{totalTests} failed (max allowed: {maxAllowedFailures})");

        Log.Info($"[SolverTest] Random: {passed}/{totalTests} passed in {sw.ElapsedMilliseconds}ms " +
                 $"({(failed > 0 ? $"{failed} failures" : "100%")})");

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 7. EDGE CASES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Проверяет граничные случаи: null, empty, invalid inputs.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Solver: Edge Cases", Group = TestGroups.Solver, Order = 80)]
    public static Task TestEdgeCasesAsync()
    {
        // Null inputs
        Assert(SigCipherSolver.Solve(null!, "abc") is null, "Null input should return null");
        Assert(SigCipherSolver.Solve("abc", null!) is null, "Null expected should return null");
        Assert(SigCipherSolver.Solve("", "") is null, "Empty should return null");

        // Паттерн без splice (не поддерживается)
        var opsNoSplice = new SigCipherOperation[]
        {
            new(SigCipherOpType.Swap, 50),
            new(SigCipherOpType.Reverse, 0),
        };
        var manifestNoSplice = new SigCipherManifest("v", opsNoSplice, "t");
        var decryptedNoSplice = manifestNoSplice.Decipher(StandardSignature);

        // Should NOT crash
        var resultNoSplice = SigCipherSolver.Solve(StandardSignature, decryptedNoSplice);
        Log.Info($"[SolverTest] No-splice: {(resultNoSplice is not null ? "found" : "null (expected)")}");

        // Негативный splice (output длиннее input) — invalid
        Assert(SigCipherSolver.Solve("abc", "abcde") is null,
            "Negative splice should return null");

        // Splice > 3 — unsupported
        Assert(SigCipherSolver.Solve("abcdefghij", "abcdef") is null,
            "Splice > 3 should return null");

        Log.Info("[SolverTest] Edge cases passed");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 8. PARALLEL SOLVER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Тестирует параллельную версию солвера.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Solver: Parallel", Group = TestGroups.Solver, Order = 90)]
    public static Task TestParallelSolverAsync()
    {
        var ops = new SigCipherOperation[]
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };

        var manifest = new SigCipherManifest("v", ops, "t");
        var decrypted = manifest.Decipher(StandardSignature);

        var sw = Stopwatch.StartNew();
        var solved = SigCipherSolver.SolveParallel(StandardSignature, decrypted);
        sw.Stop();

        Assert(solved is not null, "Parallel solver failed");
        var check = new SigCipherManifest("v", solved!, "c").Decipher(StandardSignature);
        Assert(check == decrypted, "Parallel solver verification failed");

        Log.Info($"[SolverTest] Parallel solver: {sw.ElapsedMilliseconds}ms");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // 9. BENCHMARK
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Benchmark: измеряет среднее время решения.
    /// </summary>
    /// <remarks>
    /// Количество итераций берётся из <see cref="TestConfig.SolverConfig.BenchmarkIterations"/>.
    /// </remarks>
    [TestMethod(TestCategory.Benchmark, "Solver: Performance Benchmark", Group = TestGroups.Solver, Order = 100)]
    public static Task BenchmarkSolverAsync()
    {
        var ops = new SigCipherOperation[]
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };

        var manifest = new SigCipherManifest("v", ops, "t");
        var decrypted = manifest.Decipher(StandardSignature);

        // Warm-up
        SigCipherSolver.Solve(StandardSignature, decrypted);

        var iterations = TestConfig.Get().Solver.BenchmarkIterations;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
            SigCipherSolver.Solve(StandardSignature, decrypted);

        sw.Stop();
        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;

        Assert(avgMs < 100, $"Solver too slow: {avgMs:F2}ms avg (max 100ms)");
        Log.Info($"[SolverBench] Average: {avgMs:F2}ms per solve ({iterations} iterations)");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Запускает все тесты солвера (legacy entry point).
    /// </summary>
    public static async Task RunAllAsync()
    {
        var runner = new TestRunner(Program.Services);
        var solverTests = TestDiscovery.GetAllOrdered()
            .Where(t => t.ClassName == nameof(SigCipherSolverTests))
            .ToList();

        await runner.RunBatchAsync(solverTests);
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}