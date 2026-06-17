namespace LMP.Core.Youtube.Search;

/// <summary>
/// Общий интерфейс для результатов поиска (Видео, Плейлисты, Каналы)
/// </summary>
public interface ISearchResult : IBatchItem
{
    string Url { get; }
    string Title { get; }
}