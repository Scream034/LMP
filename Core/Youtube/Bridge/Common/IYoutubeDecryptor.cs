namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Базовый интерфейс для дешифраторов YouTube.
/// </summary>
public interface IYoutubeDecryptor
{
    /// <summary>Инвалидирует кэш (при смене версии плеера).</summary>
    void InvalidateCache();

    /// <summary>Асинхронно сбрасывает кэш на диск.</summary>
    Task FlushCacheAsync();
}