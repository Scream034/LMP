using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

namespace LMP.Core.Youtube.Bridge.Common;

public sealed class DecryptorCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (TValue Value, long Ticks)> _memory = new();
    private readonly int _maxMemory;
    private readonly int _maxDisk;
    private readonly Lock _cleanupLock = new();
    private volatile bool _cleanupInProgress;
    private int _isDirty; // Атомарный флаг изменений (0 - чистый, 1 - грязный)
    private long _lastSaveTicks;
    private string? _playerVersion;

    private const int SaveIntervalMs = 60_000;

    /// <summary>Путь к файлу кэша на диске.</summary>
    public string DiskPath { get; }

    /// <summary>Папка, в которой лежит файл кэша.</summary>
    public string CacheFolder => Path.GetDirectoryName(DiskPath)!;

    public DecryptorCache(string diskPath, int maxMemory = 2000, int maxDisk = 500)
    {
        DiskPath = diskPath;
        _maxMemory = maxMemory;
        _maxDisk = maxDisk;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        if (_memory.TryGetValue(key, out var cached))
        {
            value = cached.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var ticks = Environment.TickCount64;
        _memory[key] = (value, ticks);
        Interlocked.Exchange(ref _isDirty, 1);

        if (_memory.Count > _maxMemory * 0.8 && !_cleanupInProgress)
            TriggerCleanup();

        var lastSave = Volatile.Read(ref _lastSaveTicks);
        if (ticks - lastSave > SaveIntervalMs &&
            Interlocked.CompareExchange(ref _lastSaveTicks, ticks, lastSave) == lastSave)
        {
            _ = Task.Run(SaveAsync);
        }
    }

    public async Task LoadAsync(string playerVersion)
    {
        _playerVersion = playerVersion;

        try
        {
            if (!File.Exists(DiskPath)) return;

            var json = await File.ReadAllTextAsync(DiskPath);
            var data = JsonSerializer.Deserialize<CacheData>(json);

            if (data is null || data.PlayerVersion != playerVersion)
            {
                File.Delete(DiskPath);
                return;
            }

            var ticks = Environment.TickCount64;
            foreach (var kvp in data.Entries)
            {
                var key = JsonSerializer.Deserialize<TKey>(kvp.Key);
                var value = JsonSerializer.Deserialize<TValue>(kvp.Value);
                if (key is not null && value is not null)
                    _memory[key] = (value, ticks);
            }

            Log.Debug($"[Cache] Loaded {_memory.Count} entries from {Path.GetFileName(DiskPath)}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Cache] Load failed: {ex.Message}");
        }
    }

    public async Task SaveAsync()
    {
        if (Interlocked.CompareExchange(ref _isDirty, 0, 1) == 0) return;

        _lastSaveTicks = Environment.TickCount64;

        try
        {
            Directory.CreateDirectory(CacheFolder);

            KeyValuePair<TKey, (TValue Value, long Ticks)>[] snapshot;
            try
            {
                snapshot = [.. _memory];
            }
            catch (ArgumentException)
            {
                snapshot = [.. _memory];
            }

            var entries = snapshot
                .OrderByDescending(static kvp => kvp.Value.Ticks)
                .Take(_maxDisk)
                .ToDictionary(
                    static kvp => JsonSerializer.Serialize(kvp.Key),
                    static kvp => JsonSerializer.Serialize(kvp.Value.Value));

            var data = new CacheData
            {
                PlayerVersion = _playerVersion ?? "unknown",
                Entries = entries
            };

            var json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(DiskPath, json);
        }
        catch (Exception ex)
        {
            Log.Debug($"[Cache] Save failed: {ex.Message}");
            Interlocked.Exchange(ref _isDirty, 1); // Возвращаем статус "грязного" кэша в случае ошибки записи
        }
    }

    public void Clear()
    {
        _memory.Clear();
        Interlocked.Exchange(ref _isDirty, 1);
    }

    public int Count => _memory.Count;

    public IEnumerable<TKey> Keys => _memory.Keys;

    private void TriggerCleanup()
    {
        if (!_cleanupLock.TryEnter()) return;
        try
        {
            if (_memory.Count <= _maxMemory * 0.8) return;
            _cleanupInProgress = true;

            var toRemove = _memory
                .ToArray()
                .OrderBy(static kvp => kvp.Value.Ticks)
                .Take(_maxMemory / 2)
                .Select(static kvp => kvp.Key);

            foreach (var key in toRemove)
                _memory.TryRemove(key, out _);
        }
        finally
        {
            _cleanupInProgress = false;
            _cleanupLock.Exit();
        }
    }

    private sealed class CacheData
    {
        public string PlayerVersion { get; set; } = "";
        public Dictionary<string, string> Entries { get; set; } = [];
    }
}