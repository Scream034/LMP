using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit и Integration тесты для верификации работы подсистемы SigCipher (дешифрации сигнатур).
/// </summary>
public static class SigCipherTests
{
    private const string MockPlayerScript = "var sts = 20650; signatureTimestamp: 20650;";
    private const string ExpectedSts = "20650";

    /// <summary>
    /// Тестирует парсинг и извлечение временной метки подписи (STS) из кода плеера.
    /// </summary>
    [TestMethod(TestCategory.Unit, "SigCipher: STS Extraction", Group = TestGroups.SigCipher, Order = 10)]
    public static Task TestStsExtractionAsync()
    {
        var sts = YoutubeAstSolver.ExtractSts(MockPlayerScript);
        Assert(sts == ExpectedSts, $"STS extraction failed. Expected: {ExpectedSts}, Got: {sts}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Тестирует дешифрацию реальной подписи через SigCipherDecryptor (требуется сеть).
    /// </summary>
    [TestMethod(TestCategory.Integration, "SigCipher: Live Decryption", Group = TestGroups.SigCipher, Order = 20, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveDecryptionAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<SigCipherDecryptor>();
        var testSig = TestConfig.Get().SigCipher.TestSignature;

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecipherAsync(testSig);
        sw.Stop();

        Assert(!string.IsNullOrEmpty(result), "Decryption returned empty string");
        Assert(result != testSig, "Decryption returned unmodified signature");
        Assert(result.Length > 50, $"Resulting signature length is suspicious: {result.Length}");

        var inputChars = testSig.ToHashSet();
        foreach (char c in result)
        {
            Assert(inputChars.Contains(c), $"Decrypted signature contains invalid character '{c}'");
        }

        int removedCount = testSig.Length - result.Length;
        Assert(removedCount is >= 0 and <= 10, $"Signature length difference is out of bounds: {removedCount}");

        Log.Info($"[Test] Decrypted signature in {sw.ElapsedMilliseconds}ms (removed {removedCount} chars)");

        sw.Restart();
        var cachedResult = await decryptor.DecipherAsync(testSig);
        sw.Stop();

        Assert(cachedResult == result, "Cache returned inconsistent decryption result");
        Assert(sw.ElapsedMilliseconds < 5, $"Cache hit took too long: {sw.ElapsedMilliseconds}ms");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}