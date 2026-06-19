using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace LMP.Core.Diagnostics;

/// <summary>
/// Высокопроизводительный диагностический сторожевой таймер (Watchdog) для детекции зависаний UI-потока.
/// Работает на выделенном системном потоке с наивысшим приоритетом.
/// При фиксации зависания собирает метрики пула и генерирует диагностический дамп (.dmp).
/// </summary>
public sealed class UIHangWatchdog : IDisposable
{
    private readonly Thread _watchdogThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _dumpDirectory;
    private readonly int _hangThresholdMs;
    private volatile bool _disposed;

    #region Win32 P/Invoke for MiniDumpWriteDump

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("dbghelp.dll", SetLastError = true, PreserveSig = true, EntryPoint = "MiniDumpWriteDump")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    // MINIDUMP_TYPE: Normal = 0, дает компактный слепок только со стеками потоков и дескрипторами locks
    private const int MiniDumpNormal = 0x00000000;
    private const int MiniDumpWithThreadInfo = 0x00001000;
    private const int MiniDumpWithHandleData = 0x00000004;

    #endregion

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="UIHangWatchdog"/>.
    /// </summary>
    /// <param name="dumpDirectory">Директория сохранения дампов. По умолчанию — папка логов LMP.</param>
    /// <param name="hangThresholdMs">Время ожидания отклика UI в миллисекундах.</param>
    public UIHangWatchdog(string? dumpDirectory = null, int hangThresholdMs = 350)
    {
        _dumpDirectory = dumpDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LMP", "Logs");
        _hangThresholdMs = hangThresholdMs;
        
        Directory.CreateDirectory(_dumpDirectory);

        _watchdogThread = new Thread(WatchdogLoop)
        {
            Name = "LMP-UI-Hang-Watchdog",
            IsBackground = true,
            Priority = ThreadPriority.Highest // Максимальный приоритет вне ThreadPool
        };
    }

    /// <summary>
    /// Запускает поток мониторинга.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        _watchdogThread.Start();
        Log.Info($"[Watchdog] UI Hang Watchdog started. Threshold: {_hangThresholdMs}ms. Target dir: {_dumpDirectory}");
    }

    private void WatchdogLoop()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested && !_disposed)
        {
            try
            {
                // Интервал опроса UI
                Thread.Sleep(1000);

                var pingEvent = new ManualResetEventSlim(false);

                // Отправляем сигнал в UI-поток Avalonia
                Dispatcher.UIThread.Post(() =>
                {
                    pingEvent.Set();
                }, DispatcherPriority.Send);

                // Ожидаем подтверждения отклика
                if (!pingEvent.Wait(_hangThresholdMs, token))
                {
                    // UI-поток не обработал делегат за отведенное время
                    HandleUIHang();
                    
                    // Засыпаем на длительный период после детекции во избежание генерации дубликатов
                    Thread.Sleep(15000);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[Watchdog] Error in monitoring cycle: {ex.Message}");
            }
        }
    }

    private void HandleUIHang()
    {
        Log.Error($"[Watchdog] ⚠ DETECTED HANG IN AVALONIA UI THREAD! Freeze exceeded {_hangThresholdMs}ms.");

        // Метрики пула потоков для детекции ThreadPool Starvation
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
        int activeWorkers = maxWorker - availWorker;

        Log.Warn($"[Watchdog] ThreadPool Metrics — Active Workers: {activeWorkers}, Available: {availWorker}/{maxWorker}, Min Configuration: {minWorker}");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.Warn("[Watchdog] Programmatic minidump generation is supported only on Windows platforms.");
            return;
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var dumpPath = Path.Combine(_dumpDirectory, $"LMP_UI_Hang_{timestamp}.dmp");

            Log.Info($"[Watchdog] Initializing Windows MiniDumpWriteDump to: {dumpPath}");

            using (var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var hProcess = GetCurrentProcess();
                var processId = GetCurrentProcessId();
                var hFile = fs.SafeFileHandle.DangerousGetHandle();

                // Флаги дампа: собираем только стеки вызовов, метаданные потоков и дескрипторы блокировок (весит < 5 MB)
                int dumpFlags = MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithHandleData;

                bool success = MiniDumpWriteDump(
                    hProcess,
                    processId,
                    hFile,
                    dumpFlags,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (success)
                {
                    Log.Error($"[Watchdog] ✓ DIAGNOSTIC MINIDUMP CREATED SUCCESSFULLY: {dumpPath}");
                    Log.Error("[Watchdog] 💡 HOW TO ANALYZE: Open this .dmp file in Visual Studio, click 'Debug with Managed Only' in the right panel, and inspect the 'Parallel Stacks' (Debugging -> Windows -> Parallel Stacks) to see exactly what blocks the UI Thread.");
                }
                else
                {
                    var error = Marshal.GetLastWin32Error();
                    Log.Error($"[Watchdog] ✗ MiniDumpWriteDump failed. Win32 Error: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Watchdog] MiniDumpWriteDump failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        if (_watchdogThread.IsAlive)
        {
            _watchdogThread.Join(500);
        }
        _cts.Dispose();
    }
}