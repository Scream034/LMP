// Services/MemoryMonitor.cs
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.Services;

public class MemoryStats
{
    public long WorkingSetMb { get; set; }
    public long PrivateMemoryMb { get; set; }
    public long GcTotalMemoryMb { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

public class MemoryMonitor
{
    private readonly Timer _timer;
    private MemoryStats _lastStats = new();

    public event Action<MemoryStats>? OnStatsUpdated;
    public event Action<string>? OnMemoryWarning;

    public MemoryStats CurrentStats => _lastStats;

    // Пороги предупреждений
    public long WarningThresholdMb { get; set; } = 500;
    public long CriticalThresholdMb { get; set; } = 800;

    public MemoryMonitor(TimeSpan? interval = null)
    {
        _timer = new Timer(UpdateStats, null, TimeSpan.Zero, interval ?? TimeSpan.FromSeconds(5));
    }

    private void UpdateStats(object? state)
    {
        try
        {
            var process = Process.GetCurrentProcess();

            _lastStats = new MemoryStats
            {
                WorkingSetMb = process.WorkingSet64 / (1024 * 1024),
                PrivateMemoryMb = process.PrivateMemorySize64 / (1024 * 1024),
                GcTotalMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2)
            };

            OnStatsUpdated?.Invoke(_lastStats);

            // Проверяем пороги
            if (_lastStats.WorkingSetMb > CriticalThresholdMb)
            {
                OnMemoryWarning?.Invoke($"CRITICAL: Memory usage {_lastStats.WorkingSetMb}MB exceeds {CriticalThresholdMb}MB!");
                ForceGarbageCollection();
            }
            else if (_lastStats.WorkingSetMb > WarningThresholdMb)
            {
                OnMemoryWarning?.Invoke($"WARNING: Memory usage {_lastStats.WorkingSetMb}MB exceeds {WarningThresholdMb}MB");
            }
        }
        catch { }
    }

    public void ForceGarbageCollection()
    {
        Debug.WriteLine("[Memory] Forcing garbage collection...");
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Debug.WriteLine($"[Memory] After GC: {GC.GetTotalMemory(true) / (1024 * 1024)}MB");
    }

    public string GetFormattedStats()
    {
        return $"RAM: {_lastStats.WorkingSetMb}MB | GC: {_lastStats.GcTotalMemoryMb}MB | Gen0/1/2: {_lastStats.Gen0Collections}/{_lastStats.Gen1Collections}/{_lastStats.Gen2Collections}";
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}