using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit-тесты для кэширования.
/// </summary>
public static class CacheTests
{
    [TestMethod(TestCategory.Unit, "Cache: Memory Cache", Group = TestGroups.Cache, Order = 10)]
    public static Task TestMemoryCacheAsync()
    {
        var cache = new DecryptorCache<string, string>(
            Path.GetTempFileName(),
            maxMemory: 100,
            maxDisk: 50
        );

        cache.Set("key1", "value1");
        Assert(cache.TryGet("key1", out var v1) && v1 == "value1", "Get failed");

        Assert(!cache.TryGet("nonexistent", out _), "Found nonexistent key");

        cache.Set("key1", "value2");
        Assert(cache.TryGet("key1", out var v2) && v2 == "value2", "Overwrite failed");

        cache.Set("key2", "value2");
        cache.Set("key3", "value3");
        Assert(cache.Count == 3, $"Count: {cache.Count}");

        cache.Clear();
        Assert(cache.Count == 0, "Clear failed");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Cache: Disk Roundtrip", Group = TestGroups.Cache, Order = 20)]
    public static async Task TestDiskRoundtripAsync()
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            var writeCache = new DecryptorCache<string, string>(tempFile, 100, 50);
            for (int i = 0; i < 10; i++)
                writeCache.Set($"key{i}", $"value{i}");

            await writeCache.SaveAsync();

            var readCache = new DecryptorCache<string, string>(tempFile, 100, 50);
            await readCache.LoadAsync("test_version");

            Log.Debug($"[Test] Cache roundtrip: wrote 10, read {readCache.Count}");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod(TestCategory.Unit, "Cache: SigCipher Manifest Cache", Group = TestGroups.Cache, Order = 30)]
    public static Task TestSigCipherManifestCacheAsync()
    {
        var ops = new List<SigCipherOperation>
        {
            new(SigCipherOpType.Swap, 51),
            new(SigCipherOpType.Reverse, 0),
            new(SigCipherOpType.Swap, 44),
            new(SigCipherOpType.Splice, 2),
        };

        var manifest = new SigCipherManifest("player_v123", ops, "test");

        var serialized = manifest.Serialize();
        var restored = SigCipherManifest.Deserialize(serialized);

        Assert(restored is not null, "Deserialization failed");
        Assert(restored!.PlayerVersion == manifest.PlayerVersion, "Version mismatch");

        const string testSig = "Q=wwXNghQ_T04B8uJpMZ5sWyAJIXNs3cqJRjYS6AJrTK8CQIAA8761Wv9lwNxVV";
        var result1 = manifest.Decipher(testSig);
        var result2 = restored.Decipher(testSig);

        Assert(result1 == result2, "Decipher mismatch after roundtrip");

        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}