namespace LMP.Logger;

public readonly struct LogMessage(LogLevel level, string message, Exception? exception = null)
{
    public DateTime Timestamp { get; } = DateTime.UtcNow; // Храним UTC, форматируем при записи
    public LogLevel Level { get; } = level;
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
    public int ThreadId { get; } = Environment.CurrentManagedThreadId;
}