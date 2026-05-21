using LMP.Core.Youtube.Bridge.Common;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Юнит-тесты для верификации работы подсистемы кэширования DecryptorCache.
/// </summary>
public static class CacheTests
{
    private const int MaxMemorySize = 100;
    private const int MaxDiskSize = 50;
    private const string TestVersion = "test_player_v1";

    /// <summary>
    /// Тестирует базовые операции кэша в оперативной памяти (Set, TryGet, Overwrite, Clear).
    /// </summary>
    /// <returns>Асинхронная задача.</returns>
    [TestMethod(TestCategory.Unit, "Cache: Memory Cache Operations", Group = TestGroups.Cache, Order = 10)]
    public static Task TestMemoryCacheAsync()
    {
        using var tempFile = new TempFileCookie();
        var cache = new DecryptorCache(
            tempFile.Path,
            maxMemory: MaxMemorySize,
            maxDisk: MaxDiskSize
        );

        cache.Set("key1", "value1");
        Assert(cache.TryGet("key1", out var v1) && v1 == "value1", "Failed to retrieve key1 from memory cache");

        Assert(!cache.TryGet("nonexistent", out _), "Retrieved nonexistent key from cache");

        cache.Set("key1", "value2");
        Assert(cache.TryGet("key1", out var v2) && v2 == "value2", "Failed to overwrite key1 in memory cache");

        cache.Set("key2", "value2");
        cache.Set("key3", "value3");
        Assert(cache.Count == 3, $"Unexpected cache count. Expected: 3, Got: {cache.Count}");

        cache.Clear();
        Assert(cache.Count == 0, "Failed to clear cache");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Тестирует полный цикл сериализации и десериализации кэша на диске.
    /// </summary>
    /// <returns>Асинхронная задача.</returns>
    [TestMethod(TestCategory.Unit, "Cache: Disk Roundtrip", Group = TestGroups.Cache, Order = 20)]
    public static async Task TestDiskRoundtripAsync()
    {
        using var tempFile = new TempFileCookie();

        var writeCache = new DecryptorCache(tempFile.Path, MaxMemorySize, MaxDiskSize);
        const int testEntriesCount = 10;

        for (int i = 0; i < testEntriesCount; i++)
        {
            writeCache.Set($"key{i}", $"value{i}");
        }

        await writeCache.SaveAsync();

        var readCache = new DecryptorCache(tempFile.Path, MaxMemorySize, MaxDiskSize);
        await readCache.LoadAsync(TestVersion);

        Assert(readCache.Count == testEntriesCount, $"Disk roundtrip count mismatch. Expected: {testEntriesCount}, Got: {readCache.Count}");
        Log.Debug($"[Test] Cache roundtrip successful: wrote {testEntriesCount}, read {readCache.Count}");
    }

    /// <summary>
    /// Выполняет верификацию переданного логического условия и бросает исключение в случае провала.
    /// </summary>
    /// <param name="condition">Проверяемое логическое условие.</param>
    /// <param name="message">Сообщение об ошибке.</param>
    /// <exception cref="Exception">Генерируется в случае ложности условия.</exception>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    /// <summary>
    /// Реализует паттерн RAII для безопасного управления жизненным циклом 
    /// временных файлов в тестах, предотвращая загрязнение накопителя.
    /// </summary>
    private sealed class TempFileCookie : IDisposable
    {
        /// <summary>
        /// Путь к временному файлу на диске.
        /// </summary>
        public string Path { get; } = System.IO.Path.GetTempFileName();

        /// <summary>
        /// Выполняет деструкцию временного файла.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch
            {
                // Игнорируем ошибки удаления в тестах
            }
        }
    }
}