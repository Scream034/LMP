using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit-тесты для Sig Cipher системы.
/// </summary>
public static class SigCipherTests
{
    // ══════════════════════════════════════════════════════════════════
    // MANIFEST TESTS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Проверяет сериализацию и десериализацию манифеста операций.
    /// </summary>
    [TestMethod(TestCategory.Unit, "SigCipher: Manifest Serialization",
        Group = TestGroups.SigCipher, Order = 10)]
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

        var serialized = manifest.Serialize();
        Assert(serialized.Contains("test_v1"), "Version not in serialized");
        Assert(serialized.Contains('|'), "Separator not found");

        var restored = SigCipherManifest.Deserialize(serialized);
        Assert(restored is not null, "Deserialization failed");
        Assert(restored!.PlayerVersion == "test_v1", "Version mismatch");
        Assert(restored.Operations.Count == 4, $"Op count: {restored.Operations.Count}");

        for (int i = 0; i < ops.Count; i++)
        {
            Assert(restored.Operations[i].Type == ops[i].Type,
                $"Op[{i}] type mismatch");
            Assert(restored.Operations[i].Parameter == ops[i].Parameter,
                $"Op[{i}] param mismatch");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Проверяет корректность каждой операции дешифрации.
    /// </summary>
    [TestMethod(TestCategory.Unit, "SigCipher: Manifest Decipher", Group = TestGroups.SigCipher, Order = 20)]
    public static Task TestManifestDecipherAsync()
    {
        var swapOps = new List<SigCipherOperation> { new(SigCipherOpType.Swap, 3) };
        var swapManifest = new SigCipherManifest("v1", swapOps, "test");
        var swapResult = swapManifest.Decipher("ABCDEFG");
        Assert(swapResult == "DBCAEFG", $"Swap failed: {swapResult}");

        var reverseOps = new List<SigCipherOperation> { new(SigCipherOpType.Reverse, 0) };
        var reverseManifest = new SigCipherManifest("v1", reverseOps, "test");
        var reverseResult = reverseManifest.Decipher("ABCDE");
        Assert(reverseResult == "EDCBA", $"Reverse failed: {reverseResult}");

        var spliceOps = new List<SigCipherOperation> { new(SigCipherOpType.Splice, 2) };
        var spliceManifest = new SigCipherManifest("v1", spliceOps, "test");
        var spliceResult = spliceManifest.Decipher("ABCDEFG");
        Assert(spliceResult == "CDEFG", $"Splice failed: {spliceResult}");

        var comboOps = new List<SigCipherOperation>
        {
            new(SigCipherOpType.Swap, 64),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 56),
            new(SigCipherOpType.Splice, 1),
        };

        var input = new string(Enumerable.Range(0, 105)
            .Select(i => (char)('A' + (i % 26))).ToArray());

        var comboManifest = new SigCipherManifest("v1", comboOps, "test");
        var result = comboManifest.Decipher(input);

        Assert(result.Length == 104, $"Length: {result.Length}");
        Assert(result != input, "Output equals input");

        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    // LIVE TESTS (require network)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Тестирует извлечение манифеста из текущей версии base.js.
    /// </summary>
    [TestMethod(TestCategory.Integration, "SigCipher: Live Extraction",
        Order = 10, Group = TestGroups.SigCipher, RequiresNetwork = true, TimeoutSeconds = 60)]
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

    /// <summary>
    /// Тестирует live-дешифрацию подписи.
    /// </summary>
    [TestMethod(TestCategory.Integration, "SigCipher: Live Decryption",
        Order = 20, Group = TestGroups.SigCipher, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveDecryptionAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<SigCipherDecryptor>();
        var testSig = TestConfig.Get().SigCipher.TestSignature;

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecipherAsync(testSig);
        sw.Stop();

        Assert(!string.IsNullOrEmpty(result), "Decryption returned empty");
        Assert(result != testSig, "Decryption returned unchanged signature");
        Assert(result.Length > 50, $"Result too short: {result.Length}");

        var inputChars = testSig.ToHashSet();
        foreach (char c in result)
        {
            Assert(inputChars.Contains(c),
                $"Result contains '{c}' which is not in input — cipher is broken");
        }

        int removed = testSig.Length - result.Length;
        Assert(removed is >= 0 and <= 10,
            $"Length delta {removed} is suspicious (input={testSig.Length}, result={result.Length})");

        Log.Info($"[Test] Decrypted in {sw.ElapsedMilliseconds}ms " +
                 $"(removed {removed} chars): {result[..20]}...");

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