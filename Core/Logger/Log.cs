using System.Runtime.CompilerServices;

namespace LMP.Core.Logger;

/// <summary>
/// Глобальная статическая точка доступа к логгеру.
/// Thread-safe, поддерживает вызовы до Initialize (будут проигнорированы).
/// </summary>
public static class Log
{
    private static AsyncLogProcessor? _processor;
    private static volatile bool _isInitialized;

    /// <summary>
    /// Инициализация системы логгирования.
    /// Должна вызываться в начале Main, ПОСЛЕ создания папок (G.Folder.Create).
    /// </summary>
    /// <param name="logDirectory">
    /// Путь к папке логов. Если null — используется G.Folder.Logs.
    /// </param>
    public static void Initialize(string? logDirectory = null)
    {
        if (_isInitialized) return;
        
        // Определяем папку логов
        // Приоритет: явно переданная > G.Folder.Logs > fallback
        var logsPath = logDirectory ?? GetDefaultLogDirectory();
        
        _processor = new AsyncLogProcessor(logsPath);
        _isInitialized = true;

        Info($"=== LOGGER INITIALIZED === Path: {logsPath}");
    }

    /// <summary>
    /// Получает путь к папке логов по умолчанию.
    /// Использует G.Folder.Logs если класс G доступен.
    /// </summary>
    private static string GetDefaultLogDirectory()
    {
        try
        {
            // G.Folder.Logs = %APPDATA%/LMP/Logs
            return G.Folder.Logs;
        }
        catch
        {
            // Fallback если G ещё не инициализирован (не должно случиться)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LMP",
                "Logs"
            );
        }
    }

    /// <summary>
    /// Корректное завершение работы. Записывает все буферизованные сообщения.
    /// Должна вызываться в finally блока Main.
    /// </summary>
    public static void Shutdown()
    {
        if (!_isInitialized || _processor == null) return;
        
        Info("=== LOGGER SHUTDOWN ===");
        
        // Синхронный Dispose — гарантирует запись всех логов
        _processor.Dispose();
        _processor = null;
        _isInitialized = false;
    }

    /// <summary>
    /// Асинхронное завершение работы.
    /// </summary>
    public static async ValueTask ShutdownAsync()
    {
        if (!_isInitialized || _processor == null) return;
        
        Info("=== LOGGER SHUTDOWN ===");
        
        await _processor.DisposeAsync().ConfigureAwait(false);
        _processor = null;
        _isInitialized = false;
    }

    /// <summary>
    /// Внутренний метод отправки сообщения в очередь.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Enqueue(LogLevel level, string? message, Exception? ex = null)
    {
        // Быстрый выход если логгер не инициализирован
        if (!_isInitialized || _processor == null) return;

        var msg = message ?? "null";
        _processor.Enqueue(new LogMessage(level, msg, ex));
    }

    // === Public API ===

    /// <summary>Самый детальный уровень. Для трассировки потока выполнения.</summary>
    public static void Trace(string message) => Enqueue(LogLevel.Trace, message);
    
    /// <summary>Отладочная информация. Не показывается в Release.</summary>
    public static void Debug(string message) => Enqueue(LogLevel.Debug, message);

    /// <summary>Информационные сообщения. Основной уровень.</summary>
    public static void Info(string message) => Enqueue(LogLevel.Info, message);

    /// <summary>Предупреждения. Что-то не так, но работа продолжается.</summary>
    public static void Warn(string message) => Enqueue(LogLevel.Warning, message);

    /// <summary>Ошибки. Операция не выполнена, но приложение работает.</summary>
    public static void Error(string message, Exception? ex = null) => Enqueue(LogLevel.Error, message, ex);

    /// <summary>Фатальные ошибки. Приложение не может продолжать работу.</summary>
    public static void Fatal(string message, Exception? ex = null) => Enqueue(LogLevel.Fatal, message, ex);

    /// <summary>Перегрузка для объектов (как Console.WriteLine).</summary>
    public static void Info(object? obj) => Info(obj?.ToString() ?? "null");
    
    /// <summary>Перегрузка для объектов.</summary>
    public static void Debug(object? obj) => Debug(obj?.ToString() ?? "null");
}