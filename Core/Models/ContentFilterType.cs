using LMP.Core.Youtube.Search;

namespace LMP.Core.Models;

public enum ContentFilterType
{
    All,
    Music,
    Video
}

public static class ContentFilterTypeExtensions
{
    public static SearchFilter ToSearchFilter(this ContentFilterType filterType)
    {
        return filterType switch
        {
            ContentFilterType.All => SearchFilter.None,
            ContentFilterType.Music => SearchFilter.Music,
            ContentFilterType.Video => SearchFilter.Video,
            _ => SearchFilter.None,
        };
    }

    public static ContentFilterType FromSearchFilter(this SearchFilter filterType)
    {
        return filterType switch
        {
            SearchFilter.Music => ContentFilterType.Music,
            SearchFilter.Video => ContentFilterType.Video,
            _ => ContentFilterType.All,
        };

    }
}