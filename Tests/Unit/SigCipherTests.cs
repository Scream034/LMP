#if DEBUG

using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit-тесты для Sig Cipher системы.
/// Все тесты работают БЕЗ сети.
/// </summary>
public static class SigCipherTests
{
    // ══════════════════════════════════════════════════════════════════
    // MANIFEST TESTS
    // ══════════════════════════════════════════════════════════════════
    
    public static Task TestManifestSerializationAsync()
    {
        var ops = new List<SigCipherOperation>
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };
        
        var manifest = new SigCipherManifest("test_v1", ops, "test");
        
        // Serialize
        var serialized = manifest.Serialize();
        Assert(serialized.Contains("test_v1"), "Version not in serialized");
        Assert(serialized.Contains("|"), "Separator not found");
        
        // Deserialize
        var restored = SigCipherManifest.Deserialize(serialized);
        Assert(restored is not null, "Deserialization failed");
        Assert(restored!.PlayerVersion == "test_v1", "Version mismatch");
        Assert(restored.Operations.Count == 4, $"Op count: {restored.Operations.Count}");
        
        // Verify each operation
        for (int i = 0; i < ops.Count; i++)
        {
            Assert(restored.Operations[i].Type == ops[i].Type, 
                $"Op[{i}] type mismatch");
            Assert(restored.Operations[i].Parameter == ops[i].Parameter, 
                $"Op[{i}] param mismatch");
        }
        
        return Task.CompletedTask;
    }
    
    public static Task TestManifestDecipherAsync()
    {
        // Тест каждой операции отдельно
        
        // 1. Swap
        var swapOps = new List<SigCipherOperation> { new(SigCipherOpType.Swap, 3) };
        var swapManifest = new SigCipherManifest("v1", swapOps, "test");
        var swapResult = swapManifest.Decipher("ABCDEFG");
        Assert(swapResult == "DBCAEFG", $"Swap failed: {swapResult}");
        
        // 2. Reverse
        var reverseOps = new List<SigCipherOperation> { new(SigCipherOpType.Reverse, 0) };
        var reverseManifest = new SigCipherManifest("v1", reverseOps, "test");
        var reverseResult = reverseManifest.Decipher("ABCDE");
        Assert(reverseResult == "EDCBA", $"Reverse failed: {reverseResult}");
        
        // 3. Splice
        var spliceOps = new List<SigCipherOperation> { new(SigCipherOpType.Splice, 2) };
        var spliceManifest = new SigCipherManifest("v1", spliceOps, "test");
        var spliceResult = spliceManifest.Decipher("ABCDEFG");
        Assert(spliceResult == "CDEFG", $"Splice failed: {spliceResult}");
        
        // 4. Комбинация (как у YouTube)
        var comboOps = new List<SigCipherOperation>
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };
        
        // Длинная строка (как подпись YouTube)
        var input = new string(Enumerable.Range(0, 105)
            .Select(i => (char)('A' + (i % 26))).ToArray());
        
        var comboManifest = new SigCipherManifest("v1", comboOps, "test");
        var result = comboManifest.Decipher(input);
        
        Assert(result.Length == 104, $"Length: {result.Length}"); // splice removes 1
        Assert(result != input, "Output equals input");
        
        return Task.CompletedTask;
    }
    
    // ══════════════════════════════════════════════════════════════════
    // SOLVER TESTS
    // ══════════════════════════════════════════════════════════════════
    
    public static Task TestSolverKnownPatternsAsync()
    {
        // Тестируем известные паттерны YouTube
        var patterns = new[]
        {
            new[] { new SigCipherOperation(SigCipherOpType.Swap, 64),
                    new SigCipherOperation(SigCipherOpType.Reverse, 0),
                    new SigCipherOperation(SigCipherOpType.Swap, 56),
                    new SigCipherOperation(SigCipherOpType.Splice, 1) },
            
            new[] { new SigCipherOperation(SigCipherOpType.Swap, 51),
                    new SigCipherOperation(SigCipherOpType.Swap, 44),
                    new SigCipherOperation(SigCipherOpType.Reverse, 0),
                    new SigCipherOperation(SigCipherOpType.Splice, 1) },
            
            new[] { new SigCipherOperation(SigCipherOpType.Reverse, 0),
                    new SigCipherOperation(SigCipherOpType.Swap, 48),
                    new SigCipherOperation(SigCipherOpType.Splice, 2) },
        };
        
        const string testInput = 
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop";
        
        foreach (var ops in patterns)
        {
            var manifest = new SigCipherManifest("v1", ops.ToList(), "test");
            var expected = manifest.Decipher(testInput);
            
            var sw = Stopwatch.StartNew();
            var solved = SigCipherSolver.Solve(testInput, expected);
            sw.Stop();
            
            Assert(solved is not null, $"Solver failed for pattern: {string.Join(" → ", ops)}");
            
            // Verify solution
            var solvedManifest = new SigCipherManifest("v1", solved!, "solved");
            var actual = solvedManifest.Decipher(testInput);
            
            Assert(actual == expected, 
                $"Solver verification failed:\n  Expected: {expected[..30]}...\n  Got: {actual[..30]}...");
        }
        
        return Task.CompletedTask;
    }
    
    public static Task TestSolverRandomInputsAsync()
    {
        var ops = new List<SigCipherOperation>
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };
        
        var manifest = new SigCipherManifest("v1", ops, "test");
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_=";
        
        int passed = 0;
        for (int test = 0; test < 20; test++)
        {
            int len = 90 + rng.Next(20); // 90-109 символов
            var input = new string(Enumerable.Range(0, len)
                .Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            
            var expected = manifest.Decipher(input);
            var solved = SigCipherSolver.Solve(input, expected);
            
            if (solved is not null)
            {
                var check = new SigCipherManifest("v1", solved, "check").Decipher(input);
                if (check == expected) passed++;
            }
        }
        
        Assert(passed >= 15, $"Only {passed}/20 random tests passed");
        
        return Task.CompletedTask;
    }
    
    // ══════════════════════════════════════════════════════════════════
    // EXTRACTOR TESTS
    // ══════════════════════════════════════════════════════════════════
    
    public static Task TestParseDictArrayAsync()
    {
        // Формат 1: bracket array
        const string bracketJs = """
            var someCode = 1;
            var A=["reverse","splice","length","swap","join","split"];
            var moreCode = 2;
            """;
        
        // Тестируем через reflection (метод private)
        var extractorType = typeof(SigCipherExtractor);
        var method = extractorType.GetMethod("ParseDictArrayDirect", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var bracketResult = (string[]?)method?.Invoke(null, [bracketJs, "A"]);
        Assert(bracketResult is not null, "Bracket parse returned null");
        Assert(bracketResult!.Length == 6, $"Bracket: expected 6, got {bracketResult.Length}");
        Assert(bracketResult[0] == "reverse", $"Element 0: {bracketResult[0]}");
        Assert(bracketResult[1] == "splice", $"Element 1: {bracketResult[1]}");
        
        // Формат 2: split format
        const string splitJs = """
            var other = 0;
            var B='reverse;splice;length;swap;join;split;test1;test2;test3;test4;test5;test6;test7;test8;test9;test10;test11'.split(";");
            var code = 1;
            """;
        
        var splitResult = (string[]?)method?.Invoke(null, [splitJs, "B"]);
        Assert(splitResult is not null, "Split parse returned null");
        Assert(splitResult!.Length == 17, $"Split: expected 17, got {splitResult.Length}");
        Assert(splitResult[0] == "reverse", $"Split element 0: {splitResult[0]}");
        
        return Task.CompletedTask;
    }
    
    public static Task TestDetectMethodsAsync()
    {
        // Симуляция cipher object кода
        const string cipherObjCode = """
            {
                Pi: function(k, U) { k.splice(0, U) },
                Zm: function(k, U) { var n=k[0]; k[0]=k[U%k.length]; k[U%k.length]=n },
                EF: function(k) { k.reverse() }
            }
            """;
        
        // Через reflection тестируем ParseCipherMethods
        var extractorType = typeof(SigCipherExtractor);
        var method = extractorType.GetMethod("ParseCipherMethods",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var result = (Dictionary<string, SigCipherOpType>?)method?.Invoke(null, [cipherObjCode, null]);
        
        Assert(result is not null, "ParseCipherMethods returned null");
        Assert(result!.Count == 3, $"Expected 3 methods, got {result.Count}");
        Assert(result.ContainsKey("Pi") && result["Pi"] == SigCipherOpType.Splice, "Pi should be Splice");
        Assert(result.ContainsKey("Zm") && result["Zm"] == SigCipherOpType.Swap, "Zm should be Swap");
        Assert(result.ContainsKey("EF") && result["EF"] == SigCipherOpType.Reverse, "EF should be Reverse");
        
        return Task.CompletedTask;
    }
    
    // ══════════════════════════════════════════════════════════════════
    // LIVE TESTS (require network)
    // ══════════════════════════════════════════════════════════════════
    
    public static async Task TestLiveExtractionAsync(IServiceProvider services)
    {
        var playerManager = services.GetRequiredService<PlayerContextManager>();
        var context = await playerManager.GetOrLoadAsync();
        
        Assert(!string.IsNullOrEmpty(context.Version), "Version is empty");
        Assert(context.BaseJs.Length > 1_000_000, $"BaseJs too small: {context.BaseJs.Length}");
        
        var manifest = SigCipherExtractor.ExtractManifest(context.BaseJs, context.Version);
        Assert(manifest is not null, "Manifest extraction failed");
        Assert(manifest!.Operations.Count >= 3, $"Only {manifest.Operations.Count} operations");
        
        Log.Info($"[Test] Extracted: {manifest}");
    }
    
    public static async Task TestLiveDecryptionAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<ISigCipherDecryptor>();
        
        // Тестовая подпись (реальный формат YouTube)
        const string testSig = 
            "ZEjG4qhkMg80tqLJ275cGl__kUaafdy-yB0G8dDIXtWlAEiAANEgtBgM7ydrIxvh" +
            "=bo70X1fcmOZkWRVLnGjZiq8UuPAhIgRwE0jiEJAm";
        
        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecipherAsync(testSig);
        sw.Stop();
        
        Assert(!string.IsNullOrEmpty(result), "Decryption returned empty");
        Assert(result != testSig, "Decryption returned unchanged signature");
        Assert(result.Length > 50, $"Result too short: {result.Length}");
        
        Log.Info($"[Test] Decrypted in {sw.ElapsedMilliseconds}ms: {result[..30]}...");
        
        // Второй вызов должен быть из кэша
        sw.Restart();
        var cached = await decryptor.DecipherAsync(testSig);
        sw.Stop();
        
        Assert(cached == result, "Cache returned different result");
        Assert(sw.ElapsedMilliseconds < 5, $"Cache too slow: {sw.ElapsedMilliseconds}ms");
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