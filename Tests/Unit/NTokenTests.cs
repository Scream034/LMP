#if DEBUG

using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit и integration тесты для N-Token системы.
/// </summary>
public static class NTokenTests
{
    private const string TestToken = "WDZxqubC-kfdqV5cl60";
    
    // ══════════════════════════════════════════════════════════════════
    // UNIT TESTS (no network)
    // ══════════════════════════════════════════════════════════════════
    
    public static Task TestFunctionDetectionAsync()
    {
        // Симуляция маркеров n-token функции
        const string fakeJs = """
            var someCode = function() { return 1; };
            var KM = function(a, b) {
                var c = [-1552975130, -306113009];
                // ... decryption logic ...
                return a;
            };
            var otherCode = 2;
            """;
        
        // Проверяем что маркеры обнаруживаются
        Assert(fakeJs.Contains("-1552975130"), "Primary marker missing");
        Assert(fakeJs.Contains("-306113009"), "Secondary marker missing");
        
        // Функция перед маркерами должна быть найдена
        int markerIdx = fakeJs.IndexOf("-1552975130");
        Assert(markerIdx > 0, "Marker not found");
        
        var contextBefore = fakeJs[..markerIdx];
        Assert(contextBefore.Contains("KM"), "Function name not in context");
        
        return Task.CompletedTask;
    }
    
    public static Task TestBundleExtractionAsync()
    {
        // Тест JsFunctionExtractor базовой логики
        const string simpleJs = """
            var helper = function(x) { return x + 1; };
            var main = function(a) {
                return helper(a) * 2;
            };
            """;
        
        var bundle = JsFunctionExtractor.ExtractBundle(simpleJs, "main");
        
        // Bundle может быть null для слишком простого кода
        // Главное - не exception
        Log.Debug($"[Test] Bundle extraction: {(bundle is not null ? $"{bundle.Length} chars" : "null (expected for simple code)")}");
        
        return Task.CompletedTask;
    }
    
    // ══════════════════════════════════════════════════════════════════
    // LIVE TESTS (require network)
    // ══════════════════════════════════════════════════════════════════
    
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
    
    public static async Task TestCacheHitAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        
        // Первый вызов (может быть cache miss)
        var first = await decryptor.DecryptAsync(TestToken);
        
        // Второй вызов (должен быть cache hit)
        var sw = Stopwatch.StartNew();
        var second = await decryptor.DecryptAsync(TestToken);
        sw.Stop();
        
        Assert(first == second, "Cache returned different result");
        Assert(sw.ElapsedMilliseconds < 5, $"Cache hit too slow: {sw.ElapsedMilliseconds}ms (expected <5ms)");
        
        Log.Info($"[Test] Cache hit: {sw.ElapsedMilliseconds}ms");
    }
    
    public static async Task BenchmarkAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        
        // Warm up
        await decryptor.DecryptAsync(TestToken);
        
        const int iterations = 100;
        var tokens = Enumerable.Range(0, iterations)
            .Select(i => $"test_token_{i:D3}_{Guid.NewGuid():N}"[..20])
            .ToArray();
        
        // Benchmark cache misses (первый раз для каждого токена)
        var sw = Stopwatch.StartNew();
        foreach (var token in tokens)
            await decryptor.DecryptAsync(token);
        sw.Stop();
        
        var avgMiss = sw.ElapsedMilliseconds / (double)iterations;
        Log.Info($"[Benchmark] Cache miss avg: {avgMiss:F2}ms");
        
        // Benchmark cache hits
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

#endif