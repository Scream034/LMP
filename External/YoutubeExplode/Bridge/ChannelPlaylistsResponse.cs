using System.Text.Json;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal class ChannelPlaylistsResponse(JsonElement content)
{
    // Lazy evaluation to avoid traversing/allocating everything if only one batch is needed
    public IEnumerable<Playlist> Playlists => ParsePlaylists(content);

    public string? ContinuationToken =>
        // Fast path: Try standard traversal first before falling back or searching
        // Usually: onResponseReceivedActions -> appendContinuationItemsAction -> continuationItems -> continuationItemRenderer -> continuationEndpoint -> continuationCommand -> token
        content.GetPropertyOrNull("onResponseReceivedActions")?.EnumerateArrayOrNull()?.FirstOrNull()
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems")?.EnumerateArrayOrNull()?.LastOrDefault()
            .GetPropertyOrNull("continuationItemRenderer")
            ?.GetPropertyOrNull("continuationEndpoint")
            ?.GetPropertyOrNull("continuationCommand")
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull()
        ??
        // Fallback for initial load
        content.EnumerateDescendantProperties("continuationCommand")
            .FirstOrNull()
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull();

    private IEnumerable<Playlist> ParsePlaylists(JsonElement root)
    {
        // 1. Initial Load (Tabs -> Playlists Tab -> Content)
        var tabs = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs != null)
        {
            foreach (var tab in tabs.Value.EnumerateArrayOrEmpty())
            {
                // Find "Playlists" tab (logic usually relies on verifying the content structure)
                var tabContent = tab.GetPropertyOrNull("tabRenderer")?.GetPropertyOrNull("content");
                if (tabContent == null) continue;

                // Old Design: SectionList -> ItemSection -> GridPlaylist
                var sectionList = tabContent.Value.GetPropertyOrNull("sectionListRenderer");
                if (sectionList != null)
                {
                    foreach (var pl in ParseSectionList(sectionList.Value)) yield return pl;
                }

                // New Design: RichGrid -> RichItem -> LockupViewModel
                var richGrid = tabContent.Value.GetPropertyOrNull("richGridRenderer");
                if (richGrid != null)
                {
                    foreach (var pl in ParseRichGrid(richGrid.Value)) yield return pl;
                }
            }
        }

        // 2. Continuation (onResponseReceivedActions)
        var actions = root.GetPropertyOrNull("onResponseReceivedActions");
        if (actions != null)
        {
            foreach (var action in actions.Value.EnumerateArrayOrEmpty())
            {
                var items = action.GetPropertyOrNull("appendContinuationItemsAction")
                    ?.GetPropertyOrNull("continuationItems");

                if (items != null)
                {
                    foreach (var item in items.Value.EnumerateArrayOrEmpty())
                    {
                        var pl = ParseItem(item);
                        if (pl != null) yield return pl;
                    }
                }
            }
        }
    }

    private IEnumerable<Playlist> ParseSectionList(JsonElement sectionList)
    {
        var contents = sectionList.GetPropertyOrNull("contents");
        if (contents == null) yield break;

        foreach (var section in contents.Value.EnumerateArrayOrEmpty())
        {
            var itemSection = section.GetPropertyOrNull("itemSectionRenderer");
            var items = itemSection?.GetPropertyOrNull("contents");

            if (items != null)
            {
                foreach (var item in items.Value.EnumerateArrayOrEmpty())
                {
                    // Grid Renderer
                    var gridItems = item.GetPropertyOrNull("gridRenderer")?.GetPropertyOrNull("items");
                    if (gridItems != null)
                    {
                        foreach (var gridItem in gridItems.Value.EnumerateArrayOrEmpty())
                        {
                            var pl = ParseItem(gridItem);
                            if (pl != null) yield return pl;
                        }
                    }
                    else
                    {
                        var pl = ParseItem(item);
                        if (pl != null) yield return pl;
                    }
                }
            }
        }
    }

    private IEnumerable<Playlist> ParseRichGrid(JsonElement richGrid)
    {
        var contents = richGrid.GetPropertyOrNull("contents");
        if (contents == null) yield break;

        foreach (var item in contents.Value.EnumerateArrayOrEmpty())
        {
            var richItem = item.GetPropertyOrNull("richItemRenderer")?.GetPropertyOrNull("content");
            if (richItem != null)
            {
                var pl = ParseItem(richItem.Value);
                if (pl != null) yield return pl;
            }
        }
    }

    private Playlist? ParseItem(JsonElement item)
    {
        if (item.TryGetProperty("gridPlaylistRenderer", out var grid))
            return ParseGridPlaylist(grid);

        if (item.TryGetProperty("playlistRenderer", out var list))
            return ParseGridPlaylist(list); // Structure is compatible

        if (item.TryGetProperty("lockupViewModel", out var lockup))
            return ParseLockupViewModel(lockup);

        return null;
    }

    private Playlist? ParseGridPlaylist(JsonElement json)
    {
        var id = json.GetPropertyOrNull("playlistId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(id)) return null;

        var title = json.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?
            .EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull()
            ?? json.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

        var countText = json.GetPropertyOrNull("videoCountText")?.GetPropertyOrNull("runs")?
            .EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull();

        int? count = null;
        if (countText != null && int.TryParse(countText.Replace(",", "").Replace(" ", ""), out var c))
            count = c;

        var thumbnails = json.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?
            .EnumerateArrayOrNull()?.Select(x => new ThumbnailData(x))
            .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
            .ToArray() ?? [];

        return new Playlist(id, title ?? "", null, "", count, thumbnails);
    }

    private Playlist? ParseLockupViewModel(JsonElement json)
    {
        var id = json.GetPropertyOrNull("contentId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(id) || !id.StartsWith("PL")) return null;

        var metadata = json.GetPropertyOrNull("metadata")?.GetPropertyOrNull("lockupMetadataViewModel");
        var title = metadata?.GetPropertyOrNull("title")?.GetPropertyOrNull("content")?.GetStringOrNull() ?? "";

        var image = json.GetPropertyOrNull("contentImage")?.GetPropertyOrNull("collectionThumbnailViewModel")?
            .GetPropertyOrNull("primaryThumbnail")?.GetPropertyOrNull("thumbnailViewModel")?.GetPropertyOrNull("image");

        var thumbnails = image?.GetPropertyOrNull("sources")?
             .EnumerateArrayOrNull()?.Select(x => new ThumbnailData(x))
             .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
             .ToArray() ?? [];

        return new Playlist(id, title, null, "", null, thumbnails);
    }

    public static ChannelPlaylistsResponse Parse(string raw) => new(Json.Parse(raw));
}