using LMP.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace LMP.Core.Helpers;

/// <summary>
/// Централизованное управление памятью приложения.
///
/// <para><b>Три режима:</b></para>
/// <list type="bullet">
///   <item><b>Soft</b>  — GC Gen1, non-blocking (при активном воспроизведении)</item>
///   <item><b>Normal</b> — GC Gen2 Optimized, non-blocking</item>
///   <item><b>Aggressive</b> — GC Gen2 + LOH compaction + TrimWorkingSet + Skia purge</item>
/// </list>
///
/// <para><b>Авто-очистка:</b> запускается через <see cref="StartAutoCleanup"/>,
/// интервал берётся из <see cref="AppSettings.Memory"/>.
/// Останавливается через <see cref="StopAutoCleanup"/> или <see cref="Dispose"/>.</para>
/// </summary>
public static class MemoryCleanupHelper
{
    private static PeriodicTimer? _autoCleanupTimer;
    private static CancellationTokenSource? _autoCleanupCts;
    private static readonly Lock _timerLock = new();

    #region Public API

    /// <summary>
    /// Выполняет очистку памяти в фоновом потоке.
    /// Автоматически выбирает режим: soft при воспроизведении, normal/aggressive иначе.
    /// </summary>
    public static void PerformCleanup(bool aggressive = false)
    {
        _ = Task.Run(() => RunCleanupAsync(aggressive));
    }

    /// <summary>
    /// Запускает фоновый таймер авто-очистки.
    /// Вызывать после инициализации LibraryService.
    /// Повторный вызов сначала останавливает предыдущий таймер.
    /// </summary>
    public static void StartAutoCleanup()
    {
        StopAutoCleanup();

        LibraryService? library = null;
        try
        {
            library = Program.Services.GetRequiredService<LibraryService>();
        }
        catch
        {
            return;
        }

        var settings = library.Settings.Memory;
        if (!settings.AutoCleanupEnabled || settings.AutoCleanupIntervalMinutes <= 0)
        {
            Log.Info("[Memory] Auto-cleanup disabled by settings");
            return;
        }

        lock (_timerLock)
        {
            _autoCleanupCts = new CancellationTokenSource();
            var token = _autoCleanupCts.Token;
            var interval = TimeSpan.FromMinutes(settings.AutoCleanupIntervalMinutes);

            _ = Task.Run(() => RunAutoCleanupLoopAsync(interval, token), token);
            Log.Info($"[Memory] Auto-cleanup started: every {settings.AutoCleanupIntervalMinutes}min");
        }
    }

    /// <summary>
    /// Останавливает фоновый таймер авто-очистки.
    /// </summary>
    public static void StopAutoCleanup()
    {
        lock (_timerLock)
        {
            if (_autoCleanupCts is null) return;

            _autoCleanupCts.Cancel();
            _autoCleanupCts.Dispose();
            _autoCleanupCts = null;
            _autoCleanupTimer?.Dispose();
            _autoCleanupTimer = null;

            Log.Info("[Memory] Auto-cleanup stopped");
        }
    }

    /// <summary>
    /// Перезапускает авто-очистку с новыми настройками.
    /// Вызывать при изменении настроек в SettingsViewModel.
    /// </summary>
    public static void RestartAutoCleanup() => StartAutoCleanup();

    /// <summary>
    /// Финальная очистка при завершении приложения.
    /// </summary>
    public static void Dispose()
    {
        StopAutoCleanup();
    }

    #endregion

    #region Implementation

    private static async Task RunAutoCleanupLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        _autoCleanupTimer = new PeriodicTimer(interval);

        try
        {
            // Первый прогон — сразу при старте (очищаем splash-остатки)
            await RunCleanupAsync(aggressive: false);

            while (await _autoCleanupTimer.WaitForNextTickAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                // Проверяем актуальные настройки (пользователь мог изменить)
                bool enabled = false;
                try
                {
                    var lib = Program.Services.GetRequiredService<LibraryService>();
                    enabled = lib.Settings.Memory.AutoCleanupEnabled;
                }
                catch { }

                if (enabled)
                    await RunCleanupAsync(aggressive: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Auto-cleanup loop error: {ex.Message}");
        }
    }

    private static async Task RunCleanupAsync(bool aggressive)
    {
        try
        {
            bool isPlaying = false;
            int pressureThreshold = 0;

            try
            {
                var lib = Program.Services.GetRequiredService<LibraryService>();
                pressureThreshold = lib.Settings.Memory.PressureThresholdMb;

                var pc = Program.Services.GetRequiredService<PlayerControlService>();
                isPlaying = pc.IsPlaying || pc.IsLoading;
            }
            catch { }

            // Проверка давления памяти
            if (!aggressive && pressureThreshold > 0)
            {
                var workingSetMb = GC.GetTotalMemory(false) / 1024 / 1024;
                if (workingSetMb > pressureThreshold)
                {
                    Log.Info($"[Memory] Pressure detected ({workingSetMb}MB > {pressureThreshold}MB), forcing aggressive");
                    aggressive = true;
                }
            }

            // Очистка кэшей приложения
            CleanupAppCaches();

            // GC в зависимости от режима
            if (isPlaying && !aggressive)
            {
                // Soft: воспроизведение идёт, не блокируем
                GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                Log.Info("[Memory] Soft cleanup (playback active)");
            }
            else if (aggressive)
            {
                // Aggressive: полная очистка с компактификацией LOH
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                MemoryHelpers.TrimWorkingSet();

                var afterMb = GC.GetTotalMemory(false) / 1024 / 1024;
                Log.Info($"[Memory] Aggressive cleanup done: {afterMb}MB remaining");
            }
            else
            {
                // Normal: фоновая Gen2
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                Log.Info("[Memory] Normal cleanup done");
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error($"[Memory] Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Очищает все внутренние кэши приложения + Skia non-GPU кэши.
    /// При aggressive=true также пытается очистить GPU texture cache
    /// через Avalonia Compositor (только из UI потока).
    /// </summary>
    private static void CleanupAppCaches(bool aggressive = false)
    {
        // ImageCache RAM
        try
        {
            Program.Services.GetRequiredService<ImageCacheService>().ClearMemoryCache();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] ImageCache cleanup error: {ex.Message}");
        }

        // TrackViewModel factory
        try
        {
            Program.Services.GetRequiredService<TrackViewModelFactory>().CleanupCache();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] TrackVmFactory cleanup error: {ex.Message}");
        }

        // TrackRegistry dead references
        try
        {
            Program.Services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] TrackRegistry cleanup error: {ex.Message}");
        }

        // Skia non-GPU кэши (шрифты, path effects, изображения)
        try
        {
            SKGraphics.PurgeAllCaches();
            Log.Debug("[Memory] Skia non-GPU caches purged");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Skia purge error: {ex.Message}");
        }

        // GPU texture cache — только при aggressive и только из UI потока.
        // Compositor хранит текстуры обложек, ColorPicker градиентов, глифов —
        // после навигации со страницы с большим количеством UI (Settings)
        // эти текстуры остаются resident. PurgeResourceCaches освобождает
        // неиспользуемые текстуры из GPU VRAM обратно в системную память.
        if (aggressive)
        {
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime
                            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                                ? desktop.MainWindow : null;

                        if (mainWindow != null)
                        {
                            // В Avalonia 12 Compositor извлекается через ElementComposition
                            var visual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(mainWindow);

                            if (visual?.Compositor is Avalonia.Rendering.Composition.Compositor compositor)
                            {
                                // Метод RequestCompositionUpdate полностью доступен
                                compositor.RequestCompositionUpdate(() =>
                                {
                                    // Очистка кэшей SkiaSharp внутри потока рендеринга
                                    SKGraphics.PurgeAllCaches();
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Memory] GPU cache purge error: {ex.Message}");
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Memory] GPU purge dispatch error: {ex.Message}");
            }
        }
    }

    #endregion
}