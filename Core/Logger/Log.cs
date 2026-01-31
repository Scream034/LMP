using System.Runtime.CompilerServices;

namespace LMP.Logger;

/// <summary>
/// Глобальная статическая точка доступа к логгеру.
/// Замена для Debug.WriteLine.
/// </summary>
public static class Log
{
    private static AsyncLogProcessor? _processor;
    private static bool _isInitialized;

    /// <summary>
    /// Инициализация системы логгирования (вызывать в начале Main).
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized) return;
        _processor = new AsyncLogProcessor();
        _isInitialized = true;

        Info("=== LOGGER INITIALIZED ===");
    }

    /// <summary>
    /// Корректное завершение работы (вызывать в finally блока Main).
    /// </summary>
    public static void Shutdown()
    {
        if (_processor != null)
        {
            Info("=== LOGGER SHUTDOWN ===");
            _processor.Dispose(); // Сброс буферов на диск
            _processor = null;
        }
    }

    // Вспомогательный метод отправки
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Enqueue(LogLevel level, string? message, Exception? ex = null, [CallerMemberName] string memberName = "")
    {
        if (!_isInitialized || _processor == null) return;

        // Если сообщение null, ничего не пишем (или пишем "null")
        string msg = message ?? "null";

        // Используем имя вызывающего метода как категорию, если не передана явно
        // Это очень удобно: Log.Info("Test") внутри PlayerService автоматически пометит лог как [PlayerService] или [MethodName]
        // Но для красоты лучше передавать категорию явно, или оставить "Global".
        // В данной реализации: Категория = Имя Метода (CallerMemberName) для быстрой отладки.

        _processor.Enqueue(new LogMessage(level, memberName, msg, ex));
    }

    // --- API, совместимое с Debug.WriteLine ---

    public static void Trace(string message) => Enqueue(LogLevel.Trace, message);
    public static void Debug(string message) => Enqueue(LogLevel.Debug, message);

    // Info - основной метод
    public static void Info(string message) => Enqueue(LogLevel.Info, message);
    public static void Info(string message, string category) => _processor?.Enqueue(new LogMessage(LogLevel.Info, category, message));

    public static void Warn(string message) => Enqueue(LogLevel.Warning, message);

    public static void Error(string message, Exception? ex = null) => Enqueue(LogLevel.Error, message, ex);
    public static void Fatal(string message, Exception? ex = null) => Enqueue(LogLevel.Fatal, message, ex);

    // Перегрузки для объектов (как в Console.WriteLine)
    public static void Info(object? obj) => Info(obj?.ToString() ?? "null");
}