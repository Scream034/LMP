using System.Globalization;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlaylistBrowseResponse(JsonElement content) : IPlaylistData
{
    private JsonElement? Sidebar =>
        content
            .GetPropertyOrNull("sidebar")
            ?.GetPropertyOrNull("playlistSidebarRenderer")
            ?.GetPropertyOrNull("items");

    private JsonElement? SidebarPrimary =>
        Sidebar
            ?.EnumerateArrayOrNull()
            ?.ElementAtOrNull(0)
            ?.GetPropertyOrNull("playlistSidebarPrimaryInfoRenderer");

    private JsonElement? SidebarSecondary =>
        Sidebar
            ?.EnumerateArrayOrNull()
            ?.ElementAtOrNull(1)
            ?.GetPropertyOrNull("playlistSidebarSecondaryInfoRenderer");

    // Новый путь для контента плейлиста (основной)
    private JsonElement? PlaylistContents =>
        content
            .GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("tabRenderer")
            ?.GetPropertyOrNull("content")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("itemSectionRenderer")
            ?.GetPropertyOrNull("contents")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("playlistVideoListRenderer");

    // Альтернативный путь (для некоторых плейлистов)
    private JsonElement? PlaylistContentsAlt =>
        content
            .GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("tabRenderer")
            ?.GetPropertyOrNull("content")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("playlistVideoListRenderer");

    private JsonElement? EffectivePlaylistContents => PlaylistContents ?? PlaylistContentsAlt;

    public bool IsAvailable => Sidebar is not null || EffectivePlaylistContents is not null;

    public string? Title =>
        SidebarPrimary
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("simpleText")
            ?.GetStringOrNull()
        ?? SidebarPrimary
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetPropertyOrNull("text")?.GetStringOrNull())
            .WhereNotNull()
            .Pipe(string.Concat)
        ?? SidebarPrimary
            ?.GetPropertyOrNull("titleForm")
            ?.GetPropertyOrNull("inlineFormRenderer")
            ?.GetPropertyOrNull("formField")
            ?.GetPropertyOrNull("textInputFormFieldRenderer")
            ?.GetPropertyOrNull("value")
            ?.GetStringOrNull()
        // Fallback: из header
        ?? content
            .GetPropertyOrNull("header")
            ?.GetPropertyOrNull("playlistHeaderRenderer")
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("simpleText")
            ?.GetStringOrNull();

    private JsonElement? AuthorDetails =>
        SidebarSecondary?.GetPropertyOrNull("videoOwner")?.GetPropertyOrNull("videoOwnerRenderer");

    public string? Author =>
        AuthorDetails
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("simpleText")
            ?.GetStringOrNull()
        ?? AuthorDetails
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetPropertyOrNull("text")?.GetStringOrNull())
            .WhereNotNull()
            .Pipe(string.Concat)
        // Fallback: из header
        ?? content
            .GetPropertyOrNull("header")
            ?.GetPropertyOrNull("playlistHeaderRenderer")
            ?.GetPropertyOrNull("ownerText")
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull();

    public string? ChannelId =>
        AuthorDetails
            ?.GetPropertyOrNull("navigationEndpoint")
            ?.GetPropertyOrNull("browseEndpoint")
            ?.GetPropertyOrNull("browseId")
            ?.GetStringOrNull();

    public string? Description =>
        SidebarPrimary
            ?.GetPropertyOrNull("description")
            ?.GetPropertyOrNull("simpleText")
            ?.GetStringOrNull()
        ?? SidebarPrimary
            ?.GetPropertyOrNull("description")
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetPropertyOrNull("text")?.GetStringOrNull())
            .WhereNotNull()
            .Pipe(string.Concat)
        ?? SidebarPrimary
            ?.GetPropertyOrNull("descriptionForm")
            ?.GetPropertyOrNull("inlineFormRenderer")
            ?.GetPropertyOrNull("formField")
            ?.GetPropertyOrNull("textInputFormFieldRenderer")
            ?.GetPropertyOrNull("value")
            ?.GetStringOrNull();

    public int? Count =>
        SidebarPrimary
            ?.GetPropertyOrNull("stats")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull()
            ?.Pipe(static s =>
                int.TryParse(s.Replace(",", "").Replace(" ", ""), CultureInfo.InvariantCulture, out var result) ? result : (int?)null
            )
        ?? SidebarPrimary
            ?.GetPropertyOrNull("stats")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("simpleText")
            ?.GetStringOrNull()
            ?.Split(' ')
            ?.FirstOrDefault()
            ?.Replace(",", "")
            ?.Pipe(static s =>
                int.TryParse(s, CultureInfo.InvariantCulture, out var result) ? result : (int?)null
            );

    public IReadOnlyList<ThumbnailData> Thumbnails =>
        SidebarPrimary
            ?.GetPropertyOrNull("thumbnailRenderer")
            ?.GetPropertyOrNull("playlistVideoThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => new ThumbnailData(j))
            .ToArray()
        ?? SidebarPrimary
            ?.GetPropertyOrNull("thumbnailRenderer")
            ?.GetPropertyOrNull("playlistCustomThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => new ThumbnailData(j))
            .ToArray()
        ?? [];

    // ===== НОВОЕ: Получение видео из browse response =====
    public IReadOnlyList<PlaylistVideoData> Videos =>
        EffectivePlaylistContents
            ?.GetPropertyOrNull("contents")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetPropertyOrNull("playlistVideoRenderer"))
            .WhereNotNull()
            .Select(static j => new PlaylistVideoData(j))
            .ToArray() ?? [];

    // ===== НОВОЕ: Токен продолжения для пагинации =====
    public string? ContinuationToken =>
        EffectivePlaylistContents
            ?.GetPropertyOrNull("continuations")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("nextContinuationData")
            ?.GetPropertyOrNull("continuation")
            ?.GetStringOrNull()
        ?? content
            .GetPropertyOrNull("onResponseReceivedActions")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems")
            ?.EnumerateArrayOrNull()
            ?.LastOrDefault()
            .GetPropertyOrNull("continuationItemRenderer")
            ?.GetPropertyOrNull("continuationEndpoint")
            ?.GetPropertyOrNull("continuationCommand")
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull();

    public string? VisitorData =>
        content
            .GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();
}

internal partial class PlaylistBrowseResponse
{
    public static PlaylistBrowseResponse Parse(string raw) => new(Json.Parse(raw));
}