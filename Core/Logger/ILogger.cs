namespace LMP.Core.Logger;

public interface ILogger
{
    void Trace(string message, string? category = null);
    void Debug(string message, string? category = null);
    void Info(string message, string? category = null);
    void Warn(string message, string? category = null);
    void Error(string message, Exception? ex = null, string? category = null);
    void Fatal(string message, Exception? ex = null, string? category = null);
}