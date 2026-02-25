using System.Collections.Concurrent;
using System.Text.Json;

namespace LMP.Core.Youtube.Bridge.Common;

public sealed class DecryptorCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (TValue Value, long Ticks)> _memory = new();
    private readonly int _maxMemory;
    private readonly int _maxDisk;
    private readonly Lock _cleanupLock = new();
    private volatile bool _cleanupInProgress;
    private volatile bool _isDirty;
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
        _isDirty = true;

        if (_memory.Count > _maxMemory * 0.8 && !_cleanupInProgress)
            TriggerCleanup();

        if (ticks - _lastSaveTicks > SaveIntervalMs)
            _ = Task.Run(SaveAsync);
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
        if (!_isDirty) return;

        _lastSaveTicks = Environment.TickCount64;
        _isDirty = false;

        try
        {
            Directory.CreateDirectory(CacheFolder);

            var entries = _memory
                .OrderByDescending(kvp => kvp.Value.Ticks)
                .Take(_maxDisk)
                .ToDictionary(
                    kvp => JsonSerializer.Serialize(kvp.Key),
                    kvp => JsonSerializer.Serialize(kvp.Value.Value));

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
        }
    }

    public void Clear()
    {
        _memory.Clear();
        _isDirty = true;
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
                .OrderBy(kvp => kvp.Value.Ticks)
                .Take(_maxMemory / 2)
                .Select(kvp => kvp.Key)
                .ToArray();

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