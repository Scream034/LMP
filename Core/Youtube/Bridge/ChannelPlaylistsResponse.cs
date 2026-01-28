using System.Text.Json;
using LMP.Core.Models;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal class ChannelPlaylistsResponse(JsonElement content)
{
    public IEnumerable<Playlist> Playlists => ParsePlaylists(content);

    public string? ContinuationToken =>
        content.GetPropertyOrNull("onResponseReceivedActions")?.EnumerateArrayOrNull()?.FirstOrNull()
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems")?.EnumerateArrayOrNull()?.LastOrDefault()
            .GetPropertyOrNull("continuationItemRenderer")
            ?.GetPropertyOrNull("continuationEndpoint")
            ?.GetPropertyOrNull("continuationCommand")
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull()
        ??
        content.EnumerateDescendantProperties("continuationCommand")
            .FirstOrNull()
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull();

    private IEnumerable<Playlist> ParsePlaylists(JsonElement root)
    {
        var tabs = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs != null)
        {
            foreach (var tab in tabs.Value.EnumerateArrayOrEmpty())
            {
                var tabContent = tab.GetPropertyOrNull("tabRenderer")?.GetPropertyOrNull("content");
                if (tabContent == null) continue;

                var sectionList = tabContent.Value.GetPropertyOrNull("sectionListRenderer");
                if (sectionList != null)
                {
                    foreach (var pl in ParseSectionList(sectionList.Value)) yield return pl;
                }

                var richGrid = tabContent.Value.GetPropertyOrNull("richGridRenderer");
                if (richGrid != null)
                {
                    foreach (var pl in ParseRichGrid(richGrid.Value)) yield return pl;
                }
            }
        }

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
            return ParseGridPlaylist(list); 

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

        var thumb = json.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?
            .EnumerateArrayOrNull()?.LastOrDefault().GetPropertyOrNull("url")?.GetStringOrNull();

        // Используем инициализатор объекта для LMP.Core.Models.Playlist
        return new Playlist
        {
            Id = id,
            YoutubeId = id,
            StoredName = title ?? "Unknown",
            // RemoteCount = count,
            ThumbnailUrl = thumb,
            SyncMode = PlaylistSyncMode.CloudPublic
        };
    }

    private static Playlist? ParseLockupViewModel(JsonElement json)
    {
        var id = json.GetPropertyOrNull("contentId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(id) || !id.StartsWith("PL")) return null;

        var metadata = json.GetPropertyOrNull("metadata")?.GetPropertyOrNull("lockupMetadataViewModel");
        var title = metadata?.GetPropertyOrNull("title")?.GetPropertyOrNull("content")?.GetStringOrNull() ?? "";

        var image = json.GetPropertyOrNull("contentImage")?.GetPropertyOrNull("collectionThumbnailViewModel")?
            .GetPropertyOrNull("primaryThumbnail")?.GetPropertyOrNull("thumbnailViewModel")?.GetPropertyOrNull("image");

        var thumb = image?.GetPropertyOrNull("sources")?
             .EnumerateArrayOrNull()?.LastOrDefault().GetPropertyOrNull("url")?.GetStringOrNull();

        return new Playlist
        {
            Id = id,
            YoutubeId = id,
            StoredName = title,
            ThumbnailUrl = thumb,
            SyncMode = PlaylistSyncMode.CloudPublic
        };
    }

    public static ChannelPlaylistsResponse Parse(string raw) => new(Json.Parse(raw));
}