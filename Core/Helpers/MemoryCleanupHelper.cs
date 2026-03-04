using LMP.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Core.Helpers;

/// <summary>
/// Централизованная логика очистки памяти.
/// Используется из MainWindow, TrayManager и других компонентов.
/// 
/// <para><b>Три режима:</b></para>
/// <list type="bullet">
///   <item><b>Soft</b> — GC Gen1, не блокирует (при активном воспроизведении)</item>
///   <item><b>Normal</b> — GC Gen2 Optimized, не блокирует</item>
///   <item><b>Aggressive</b> — GC Gen2 Aggressive + LOH compaction + TrimWorkingSet</item>
/// </list>
/// </summary>
public static class MemoryCleanupHelper
{
    /// <summary>
    /// Выполняет очистку памяти в фоновом потоке.
    /// Автоматически выбирает режим: soft при воспроизведении, normal/aggressive иначе.
    /// </summary>
    /// <param name="aggressive">
    /// true — принудительная агрессивная очистка (LOH compaction, double GC).
    /// false — автоматический выбор на основе состояния плеера.
    /// </param>
    public static void PerformCleanup(bool aggressive = false)
    {
        _ = Task.Run(() =>
        {
            try
            {
                bool isPlaying = false;
                try
                {
                    var pc = Program.Services.GetRequiredService<PlayerControlService>();
                    isPlaying = pc.IsPlaying || pc.IsLoading;
                }
                catch { /* Сервис может быть недоступен при shutdown */ }

                // ═══ Очистка кэшей ═══
                CleanupCaches();

                // ═══ GC в зависимости от состояния ═══
                if (isPlaying && !aggressive)
                {
                    GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                    Log.Info("[Memory] Soft cleanup (playback active)");
                }
                else if (aggressive)
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    MemoryHelpers.TrimWorkingSet();
                    Log.Info($"[Memory] Aggressive cleanup: {GC.GetTotalMemory(false) / 1024 / 1024} MB");
                }
                else
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    Log.Info($"[Memory] Normal cleanup: {GC.GetTotalMemory(false) / 1024 / 1024} MB");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Memory] Cleanup error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Очищает все внутренние кэши приложения (изображения, TrackVM, TrackRegistry).
    /// </summary>
    private static void CleanupCaches()
    {
        try
        {
            Program.Services.GetRequiredService<ImageCacheService>().ClearMemoryCache();
            Program.Services.GetRequiredService<TrackViewModelFactory>().CleanupCache();
            Program.Services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Cache cleanup error: {ex.Message}");
        }
    }
}