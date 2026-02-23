namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Базовый интерфейс для всех дешифраторов YouTube.
/// </summary>
public interface IYoutubeDecryptor
{
    /// <summary>Инвалидирует весь кэш (при смене версии плеера).</summary>
    void InvalidateCache();
    
    /// <summary>Синхронно сбрасывает кэш на диск.</summary>
    void FlushCache();
}