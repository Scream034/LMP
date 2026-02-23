using System.Collections.Concurrent;
using System.Text.Json;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Generic кэш для расшифрованных значений (signatures/tokens).
/// Thread-safe, с автоматической очисткой и персистентностью.
/// </summary>
public sealed class DecryptorCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (TValue Value, long Ticks)> _memory = new();
    private readonly string _diskPath;
    private readonly int _maxMemory;
    private readonly int _maxDisk;
    private readonly Lock _cleanupLock = new();
    private volatile bool _cleanupInProgress;
    private volatile bool _isDirty;
    private long _lastSaveTicks;
    
    private const int SaveIntervalMs = 60_000;
    
    public DecryptorCache(string diskPath, int maxMemory = 2000, int maxDisk = 500)
    {
        _diskPath = diskPath;
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
        
        // Auto-cleanup
        if (_memory.Count > _maxMemory * 0.8 && !_cleanupInProgress)
            TriggerCleanup();
        
        // Auto-save
        if (ticks - _lastSaveTicks > SaveIntervalMs)
            _ = Task.Run(SaveAsync);
    }
    
    public async Task LoadAsync(string playerVersion)
    {
        try
        {
            if (!File.Exists(_diskPath)) return;
            
            var json = await File.ReadAllTextAsync(_diskPath);
            var data = JsonSerializer.Deserialize<CacheData>(json);
            
            if (data is null || data.PlayerVersion != playerVersion)
            {
                // Version mismatch — delete
                File.Delete(_diskPath);
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
            
            Log.Debug($"[Cache] Loaded {_memory.Count} entries from {Path.GetFileName(_diskPath)}");
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
            Directory.CreateDirectory(Path.GetDirectoryName(_diskPath)!);
            
            var entries = _memory
                .OrderByDescending(kvp => kvp.Value.Ticks)
                .Take(_maxDisk)
                .ToDictionary(
                    kvp => JsonSerializer.Serialize(kvp.Key),
                    kvp => JsonSerializer.Serialize(kvp.Value.Value)
                );
            
            var data = new CacheData
            {
                PlayerVersion = "unknown", // будет переопределено в наследнике
                Entries = entries
            };
            
            var json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(_diskPath, json);
            
            Log.Debug($"[Cache] Saved {entries.Count} entries to {Path.GetFileName(_diskPath)}");
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
            
            Log.Debug($"[Cache] Cleanup: {toRemove.Length} removed");
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