namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Фатальная ошибка проверки native quickjs_bridge.dll.
/// Возникает, когда приложение загрузило устаревшую/неверную DLL
/// или native browser environment не соответствует ожидаемому ABI/features.
/// </summary>
public sealed class QuickJsBridgeVerificationException : Exception
{
    public QuickJsBridgeVerificationException(string message)
        : base(message)
    {
    }

    public QuickJsBridgeVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}