using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Text;

namespace LMP.Core.Services;

/// <summary>
/// Объединённая система мониторинга и диагностики памяти.
/// Заменяет отдельный MemoryMonitor.
/// </summary>
public sealed class MemoryDiagnostics : IDisposable
{
    #region Singleton

    private static MemoryDiagnostics? _instance;
    public static MemoryDiagnostics Instance => _instance ??= new MemoryDiagnostics();

    #endregion

    #region Fields

    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, int> _instanceCounts = new();
    private readonly Timer _monitorTimer;

    private MemoryStats _lastStats = new();
    private bool _disposed;

    #endregion

    #region Events

    public event Action<MemoryStats>? OnStatsUpdated;
    public event Action<string>? OnMemoryWarning;

    #endregion

    #region Properties

    public MemoryStats CurrentStats => _lastStats;

    /// <summary>Порог предупреждения (MB)</summary>
    public long WarningThresholdMb { get; set; } = 330;

    /// <summary>Критический порог (MB)</summary>
    public long CriticalThresholdMb { get; set; } = 440;

    /// <summary>Автоматическая очистка при критическом пороге</summary>
    public bool AutoCleanupEnabled { get; set; } = true;

    #endregion

    #region Constructor

    private MemoryDiagnostics()
    {
        _monitorTimer = new Timer(UpdateStats, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Tracking Methods

    /// <summary>
    /// Изменяет частоту мониторинга.
    /// При сворачивании — реже (30с), при разворачивании — чаще (5с).
    /// </summary>
    public void SetMonitoringInterval(TimeSpan interval)
    {
        if (_disposed) return;
        try
        {
            _monitorTimer.Change(interval, interval);
            Log.Debug($"[MemoryDiag] Monitoring interval: {interval.TotalSeconds}s");
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Увеличивает счётчик байтов для категории.
    /// </summary>
    public static void TrackBytes(string category, long bytes)
    {
        Instance._counters.AddOrUpdate(category, bytes, (_, old) => old + bytes);
    }

    /// <summary>
    /// Уменьшает счётчик байтов для категории.
    /// </summary>
    public static void UntrackBytes(string category, long bytes)
    {
        Instance._counters.AddOrUpdate(category, 0, (_, old) => Math.Max(0, old - bytes));
    }

    /// <summary>
    /// Устанавливает точное значение для категории.
    /// </summary>
    public static void SetBytes(string category, long bytes)
    {
        Instance._counters[category] = bytes;
    }

    /// <summary>
    /// Увеличивает счётчик экземпляров.
    /// </summary>
    public static void TrackInstance(string category)
    {
        Instance._instanceCounts.AddOrUpdate(category, 1, (_, old) => old + 1);
    }

    /// <summary>
    /// Уменьшает счётчик экземпляров.
    /// </summary>
    public static void UntrackInstance(string category)
    {
        Instance._instanceCounts.AddOrUpdate(category, 0, (_, old) => Math.Max(0, old - 1));
    }

    /// <summary>
    /// Получает текущее значение счётчика.
    /// </summary>
    public static long GetBytes(string category)
    {
        return Instance._counters.GetValueOrDefault(category, 0);
    }

    /// <summary>
    /// Получает количество экземпляров.
    /// </summary>
    public static int GetInstanceCount(string category)
    {
        return Instance._instanceCounts.GetValueOrDefault(category, 0);
    }

    #endregion

    #region Monitoring

    private void UpdateStats(object? state)
    {
        if (_disposed) return;

        try
        {
            var process = Process.GetCurrentProcess();
            var gcInfo = GC.GetGCMemoryInfo();

            _lastStats = new MemoryStats
            {
                WorkingSetMb = process.WorkingSet64 / (1024 * 1024),
                PrivateMemoryMb = process.PrivateMemorySize64 / (1024 * 1024),
                GcTotalMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024),
                GcHeapSizeMb = gcInfo.HeapSizeBytes / (1024 * 1024),
                LohSizeMb = gcInfo.GenerationInfo.Length > 3
                    ? gcInfo.GenerationInfo[3].SizeAfterBytes / (1024 * 1024)
                    : 0,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                TrackedCategories = GetTrackedSummary()
            };

            OnStatsUpdated?.Invoke(_lastStats);

            // Проверяем пороги
            if (_lastStats.WorkingSetMb > CriticalThresholdMb)
            {
                var msg = $"CRITICAL: Memory {_lastStats.WorkingSetMb}MB > {CriticalThresholdMb}MB!";
                OnMemoryWarning?.Invoke(msg);
                Log.Warn(msg);

                if (AutoCleanupEnabled)
                {
                    ForceCleanup();
                }
            }
            else if (_lastStats.WorkingSetMb > WarningThresholdMb)
            {
                var msg = $"WARNING: Memory {_lastStats.WorkingSetMb}MB > {WarningThresholdMb}MB";
                OnMemoryWarning?.Invoke(msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MemoryDiagnostics] UpdateStats error: {ex.Message}");
        }
    }

    private Dictionary<string, long> GetTrackedSummary()
    {
        return _counters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    #endregion

    #region Reporting

    /// <summary>
    /// Генерирует полный отчёт о памяти.
    /// </summary>
    public string GetFullReport()
    {
        var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var sb = new StringBuilder();

        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║              MEMORY DIAGNOSTICS REPORT                   ║");
        sb.AppendLine("╠══════════════════════════════════════════════════════════╣");
        sb.AppendLine($"║ Working Set:      {process.WorkingSet64 / 1024 / 1024,6} MB                          ║");
        sb.AppendLine($"║ Private Memory:   {process.PrivateMemorySize64 / 1024 / 1024,6} MB                          ║");
        sb.AppendLine($"║ GC Total:         {GC.GetTotalMemory(false) / 1024 / 1024,6} MB                          ║");
        sb.AppendLine($"║ GC Heap Size:     {gcInfo.HeapSizeBytes / 1024 / 1024,6} MB                          ║");

        if (gcInfo.GenerationInfo.Length > 3)
        {
            sb.AppendLine($"║ LOH Size:         {gcInfo.GenerationInfo[3].SizeAfterBytes / 1024 / 1024,6} MB                          ║");
        }

        sb.AppendLine($"║ Memory Load:      {gcInfo.MemoryLoadBytes / 1024 / 1024,6} MB                          ║");
        sb.AppendLine($"║ High Threshold:   {gcInfo.HighMemoryLoadThresholdBytes / 1024 / 1024,6} MB                          ║");
        sb.AppendLine("╠══════════════════════════════════════════════════════════╣");
        sb.AppendLine($"║ GC Collections: Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2),-6}          ║");
        sb.AppendLine("╠══════════════════════════════════════════════════════════╣");
        sb.AppendLine("║                 TRACKED CATEGORIES                       ║");
        sb.AppendLine("╠──────────────────────────────────────────────────────────╣");

        var sortedCounters = _counters
            .Where(kvp => kvp.Value > 0)
            .OrderByDescending(kvp => kvp.Value)
            .ToList();

        foreach (var kvp in sortedCounters)
        {
            var valueMb = kvp.Value / 1024.0 / 1024.0;
            var valueKb = kvp.Value / 1024.0;

            string formatted = valueMb >= 1
                ? $"{valueMb:F1} MB"
                : $"{valueKb:F0} KB";

            sb.AppendLine($"║  {kvp.Key,-30} {formatted,12}          ║");
        }

        if (_instanceCounts.Any(kvp => kvp.Value > 0))
        {
            sb.AppendLine("╠──────────────────────────────────────────────────────────╣");
            sb.AppendLine("║                 INSTANCE COUNTS                          ║");
            sb.AppendLine("╠──────────────────────────────────────────────────────────╣");

            foreach (var kvp in _instanceCounts.Where(kvp => kvp.Value > 0).OrderByDescending(kvp => kvp.Value))
            {
                sb.AppendLine($"║  {kvp.Key,-30} {kvp.Value,12}          ║");
            }
        }

        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        return sb.ToString();
    }

    /// <summary>
    /// Краткая строка для UI.
    /// </summary>
    public string GetShortStatus()
    {
        return $"RAM: {_lastStats.WorkingSetMb}MB | GC: {_lastStats.GcTotalMemoryMb}MB | Gen0/1/2: {_lastStats.Gen0Collections}/{_lastStats.Gen1Collections}/{_lastStats.Gen2Collections}";
    }

    /// <summary>
    /// Логирует полный отчёт.
    /// </summary>
    public static void LogReport()
    {
        Log.Info(Instance.GetFullReport());
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Принудительная очистка памяти.
    /// </summary>
    public static void ForceCleanup()
    {
        Log.Info("[MemoryDiagnostics] Forcing memory cleanup...");

        var before = GC.GetTotalMemory(false);

        // Компактификация LOH
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        // Агрессивная сборка мусора
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        var after = GC.GetTotalMemory(true);
        var freed = (before - after) / 1024 / 1024;

        Log.Info($"[MemoryDiagnostics] Cleanup complete. Freed ~{freed}MB. Current: {after / 1024 / 1024}MB");
    }

    /// <summary>
    /// Мягкая очистка (Gen1).
    /// </summary>
    public static void SoftCleanup()
    {
        GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitorTimer.Dispose();
        _counters.Clear();
        _instanceCounts.Clear();
    }

    #endregion
}

/// <summary>
/// Статистика памяти.
/// </summary>
public class MemoryStats
{
    public long WorkingSetMb { get; set; }
    public long PrivateMemoryMb { get; set; }
    public long GcTotalMemoryMb { get; set; }
    public long GcHeapSizeMb { get; set; }
    public long LohSizeMb { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public Dictionary<string, long> TrackedCategories { get; set; } = [];
}