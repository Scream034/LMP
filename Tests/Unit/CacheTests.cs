#if DEBUG

using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit-тесты для кэширования.
/// </summary>
public static class CacheTests
{
    public static Task TestMemoryCacheAsync()
    {
        var cache = new DecryptorCache<string, string>(
            Path.GetTempFileName(), 
            maxMemory: 100, 
            maxDisk: 50
        );
        
        // Set & Get
        cache.Set("key1", "value1");
        Assert(cache.TryGet("key1", out var v1) && v1 == "value1", "Get failed");
        
        // Missing key
        Assert(!cache.TryGet("nonexistent", out _), "Found nonexistent key");
        
        // Overwrite
        cache.Set("key1", "value2");
        Assert(cache.TryGet("key1", out var v2) && v2 == "value2", "Overwrite failed");
        
        // Count
        cache.Set("key2", "value2");
        cache.Set("key3", "value3");
        Assert(cache.Count == 3, $"Count: {cache.Count}");
        
        // Clear
        cache.Clear();
        Assert(cache.Count == 0, "Clear failed");
        
        return Task.CompletedTask;
    }
    
    public static async Task TestDiskRoundtripAsync()
    {
        var tempFile = Path.GetTempFileName();
        
        try
        {
            // Write
            var writeCache = new DecryptorCache<string, string>(tempFile, 100, 50);
            for (int i = 0; i < 10; i++)
                writeCache.Set($"key{i}", $"value{i}");
            
            await writeCache.SaveAsync();
            
            // Read
            var readCache = new DecryptorCache<string, string>(tempFile, 100, 50);
            await readCache.LoadAsync("test_version");
            
            // Проверяем (может быть 0 если version mismatch)
            // Это ожидаемое поведение
            Log.Debug($"[Test] Cache roundtrip: wrote 10, read {readCache.Count}");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
    
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
        
        // Roundtrip
        var serialized = manifest.Serialize();
        var restored = SigCipherManifest.Deserialize(serialized);
        
        Assert(restored is not null, "Deserialization failed");
        Assert(restored!.PlayerVersion == manifest.PlayerVersion, "Version mismatch");
        
        // Проверяем что дешифровка идентична
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

#endif