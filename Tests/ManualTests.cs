#if DEBUG

using System.Diagnostics;
using LMP.Tests.Framework;

namespace LMP.Tests;

/// <summary>
/// Точка входа для запуска тестов из кода (F10 в Debug).
/// <para>
/// Теперь делегирует в <see cref="TestRunner"/> с автоматическим discovery.
/// Сохраняет обратную совместимость: legacy-методы всё ещё работают.
/// </para>
/// </summary>
public static class ManualTests
{
    /// <summary>
    /// Запускает ВСЕ обнаруженные тесты: Unit → Integration → Benchmark.
    /// Заменяет старую ручную регистрацию.
    /// </summary>
    public static async Task RunAllAsync()
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine("\n" + new string('═', 70));
        Console.WriteLine("  LMP TEST SUITE (Auto-Discovery)");
        Console.WriteLine(new string('═', 70) + "\n");

        var runner = new TestRunner(Program.Services);
        int passed = 0, failed = 0, skipped = 0;

        runner.TestCompleted += (descriptor, result) =>
        {
            var icon = result.State switch
            {
                TestRunState.Passed => "✓",
                TestRunState.Failed => "✗",
                TestRunState.Skipped => "⊘",
                _ => "?",
            };

            Console.WriteLine($"  {icon} {descriptor.DisplayName} ({result.DurationFormatted})" +
                (result.ErrorMessage is not null ? $" — {result.ErrorMessage}" : ""));

            switch (result.State)
            {
                case TestRunState.Passed: Interlocked.Increment(ref passed); break;
                case TestRunState.Failed: Interlocked.Increment(ref failed); break;
                case TestRunState.Skipped: Interlocked.Increment(ref skipped); break;
            }
        };

        // Группируем вывод по категории
        var grouped = TestDiscovery.GetGrouped();

        foreach (var (category, tests) in grouped)
        {
            var label = category switch
            {
                TestCategory.Unit => "▶ UNIT TESTS (no network)",
                TestCategory.Integration => "▶ INTEGRATION TESTS (network required)",
                TestCategory.Benchmark => "▶ BENCHMARKS",
                _ => $"▶ {category}",
            };
            Console.WriteLine($"\n{label}\n");

            await runner.RunBatchAsync(tests);
        }

        sw.Stop();

        Console.WriteLine("\n" + new string('═', 70));
        Console.WriteLine($"  RESULTS: {passed} passed, {failed} failed, {skipped} skipped " +
                         $"({sw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine(new string('═', 70) + "\n");
    }

    // ══════════════════════════════════════════════════════════════════
    // LEGACY QUICK TESTS (для обратной совместимости)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Быстрый тест N-Token (самый важный).</summary>
    public static Task TestNTokenQuickAsync() =>
        Unit.NTokenTests.TestLiveDecryptionAsync(Program.Services);

    /// <summary>Быстрый тест Sig Cipher.</summary>
    public static Task TestSigCipherQuickAsync() =>
        Unit.SigCipherTests.TestLiveDecryptionAsync(Program.Services);

    /// <summary>Полный pipeline тест.</summary>
    public static Task TestSigCipherFullAsync(string videoId = "dQw4w9WgXcQ") =>
        Integration.StreamPipelineTests.TestFullPipelineInternalAsync(Program.Services, videoId);

    /// <summary>Полный тест солвера.</summary>
    public static Task TestSolverFullAsync() =>
        Unit.SigCipherSolverTests.RunAllAsync();

    /// <summary>Benchmark N-Token.</summary>
    public static Task BenchmarkNTokenAsync() =>
        Unit.NTokenTests.BenchmarkAsync(Program.Services);
}

#endif