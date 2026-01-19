using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;

namespace MyLiteMusicPlayer.UI;

public static class ImGuiClipboardBridge
{
    private static IntPtr _lastAllocation = IntPtr.Zero;

    // Храним ссылки на делегаты, чтобы GC их не удалил
    private static readonly GetClipboardTextHandler GetDelegate = GetClipboard;
    private static readonly SetClipboardTextHandler SetDelegate = SetClipboard;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetClipboardTextHandler(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetClipboardTextHandler(IntPtr userData, IntPtr textPtr);

    #region Windows Native API
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;  // Unicode (UTF-16)
    private const uint GMEM_MOVEABLE = 0x0002;

    #endregion

    /// <summary>
    /// Устанавливает кастомные функции буфера обмена для ImGui
    /// </summary>
    public static void Install()
    {
        var io = ImGui.GetIO();
        io.GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(GetDelegate);
        io.SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(SetDelegate);
    }

    /// <summary>
    /// Вызывается ImGui при ВСТАВКЕ (Ctrl+V).
    /// Возвращает указатель на UTF-8 строку.
    /// </summary>
    private static IntPtr GetClipboard(IntPtr userData)
    {
        string? text = GetClipboardTextNative();
        
        if (string.IsNullOrEmpty(text)) 
            return IntPtr.Zero;

        // Освобождаем предыдущую аллокацию
        if (_lastAllocation != IntPtr.Zero) 
        {
            Marshal.FreeHGlobal(_lastAllocation);
            _lastAllocation = IntPtr.Zero;
        }

        // ImGui ожидает UTF-8 null-terminated строку
        int byteCount = Encoding.UTF8.GetByteCount(text);
        byte[] bytes = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        bytes[byteCount] = 0; // null-terminator

        _lastAllocation = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, _lastAllocation, bytes.Length);

        return _lastAllocation;
    }

    /// <summary>
    /// Вызывается ImGui при КОПИРОВАНИИ (Ctrl+C).
    /// Получает указатель на UTF-8 строку от ImGui.
    /// </summary>
    private static void SetClipboard(IntPtr userData, IntPtr textPtr)
    {
        if (textPtr == IntPtr.Zero)
            return;

        // ImGui передаёт UTF-8 строку
        string? text = Marshal.PtrToStringUTF8(textPtr);
        
        if (!string.IsNullOrEmpty(text))
        {
            SetClipboardTextNative(text);
        }
    }

    /// <summary>
    /// Читает текст из системного буфера обмена через WinAPI
    /// </summary>
    private static string? GetClipboardTextNative()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
                return null;

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                // CF_UNICODETEXT = UTF-16 строка
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Записывает текст в системный буфер обмена через WinAPI
    /// </summary>
    private static void SetClipboardTextNative(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return;

        IntPtr hGlobal = IntPtr.Zero;
        
        try
        {
            EmptyClipboard();
            
            // UTF-16: каждый символ = 2 байта + null-terminator (2 байта)
            int byteCount = (text.Length + 1) * sizeof(char);
            
            hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
            if (hGlobal == IntPtr.Zero)
                return;

            IntPtr target = GlobalLock(hGlobal);
            if (target == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return;
            }

            try
            {
                // Копируем UTF-16 строку в память
                byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            // После SetClipboardData система владеет памятью, не освобождаем hGlobal!
            if (SetClipboardData(CF_UNICODETEXT, hGlobal) != IntPtr.Zero)
            {
                hGlobal = IntPtr.Zero; // Система забрала владение
            }
        }
        finally
        {
            if (hGlobal != IntPtr.Zero)
                GlobalFree(hGlobal);
                
            CloseClipboard();
        }
    }
}