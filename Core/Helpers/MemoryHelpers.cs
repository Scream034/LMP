using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LMP.Core.Helpers;

public static partial class MemoryHelpers
{
    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool _SetProcessWorkingSetSize(IntPtr process, int minimumWorkingSetSize, int maximumWorkingSetSize);

    /// <summary>
    /// Принудительно сбрасывает физическую память (Working Set) в файл подкачки.
    /// Безопасно для аудио, так как активные буферы сразу вернутся в RAM при доступе.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            // Параметры -1, -1 говорят Windows: "Убери из RAM всё, что сейчас не используется активно".
            using var process = Process.GetCurrentProcess();
            _SetProcessWorkingSetSize(process.Handle, -1, -1);
            Log.Info($"[Memory] Working set trimmed. Current RAM: {process.WorkingSet64 / 1024 / 1024} MB");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Failed to trim working set: {ex.Message}");
        }
    }
}