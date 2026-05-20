using System.Runtime.InteropServices;
using System.Text;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Обеспечивает выполнение обходных алгоритмов YouTube на сверхбыстром нативном движке QuickJS-NG.
/// Полностью исключает выделение объектов в управляемой куче .NET при дешифрации.
/// </summary>
public static unsafe class QuickJsDecryptor
{
    private const string LibName = "quickjs_bridge";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern byte* qjs_decrypt_token(byte* script, byte* functionName, byte* challenge);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void qjs_free_string(byte* str);

    /// <summary>
    /// Выполняет дешифрацию токена/подписи на нативном движке QuickJS-NG.
    /// Аллокация строк происходит в неуправляемой куче процесса во избежание StackOverflow.
    /// </summary>
    /// <param name="preprocessedJs">Полный оптимизированный код плеера.</param>
    /// <param name="functionName">Имя метода дешифратора ("n" или "sig").</param>
    /// <param name="challenge">Сырой токен, требующий расшифровки.</param>
    /// <returns>Расшифрованная строка или null в случае фатальной ошибки.</returns>
    public static string? Decrypt(string preprocessedJs, string functionName, string challenge)
    {
        if (string.IsNullOrEmpty(preprocessedJs) || string.IsNullOrEmpty(challenge))
            return null;

        byte* scriptPtr = null;
        byte* funcPtr = null;
        byte* challengePtr = null;
        byte* nativeResult = null;

        try
        {
            // Безопасное выделение UTF-8 строк в неуправляемой куче процесса (вместо стека)
            scriptPtr = (byte*)Marshal.StringToCoTaskMemUTF8(preprocessedJs);
            funcPtr = (byte*)Marshal.StringToCoTaskMemUTF8(functionName);
            challengePtr = (byte*)Marshal.StringToCoTaskMemUTF8(challenge);

            nativeResult = qjs_decrypt_token(scriptPtr, funcPtr, challengePtr);
            if (nativeResult == null)
                return null;

            // Вычисляем длину возвращенной UTF-8 строки
            int length = 0;
            while (nativeResult[length] != 0)
            {
                length++;
            }

            return Encoding.UTF8.GetString(nativeResult, length);
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJS] Native execution failed: {ex.Message}");
            return null;
        }
        finally
        {
            // Гарантированное освобождение памяти в куче
            if (scriptPtr != null) Marshal.FreeCoTaskMem((IntPtr)scriptPtr);
            if (funcPtr != null) Marshal.FreeCoTaskMem((IntPtr)funcPtr);
            if (challengePtr != null) Marshal.FreeCoTaskMem((IntPtr)challengePtr);
            if (nativeResult != null) qjs_free_string(nativeResult);
        }
    }
}