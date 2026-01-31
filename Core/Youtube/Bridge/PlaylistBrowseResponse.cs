using System.Globalization;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет ответ API YouTube на запрос страницы просмотра плейлиста (Browse).
/// </summary>
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

    // Основной путь к содержимому плейлиста
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

    // Альтернативный путь (иногда встречается в старых или специфических плейлистах)
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

    /// <inheritdoc />
    public bool IsAvailable => Sidebar is not null || EffectivePlaylistContents is not null;

    /// <inheritdoc />
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
        // Fallback: заголовок из хедера
        ?? content
            .GetPropertyOrNull("header")
            ?.GetPropertyOrNull("playlistHeaderRenderer")
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("simpleText")
            ?.GetStringOrNull();

    private JsonElement? AuthorDetails =>
        SidebarSecondary?.GetPropertyOrNull("videoOwner")?.GetPropertyOrNull("videoOwnerRenderer");

    /// <inheritdoc />
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
        // Fallback: автор из хедера
        ?? content
            .GetPropertyOrNull("header")
            ?.GetPropertyOrNull("playlistHeaderRenderer")
            ?.GetPropertyOrNull("ownerText")
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull();

    /// <inheritdoc />
    public string? ChannelId =>
        AuthorDetails
            ?.GetPropertyOrNull("navigationEndpoint")
            ?.GetPropertyOrNull("browseEndpoint")
            ?.GetPropertyOrNull("browseId")
            ?.GetStringOrNull();

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <summary>
    /// Список видео в текущей партии плейлиста.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos =>
        EffectivePlaylistContents
            ?.GetPropertyOrNull("contents")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetPropertyOrNull("playlistVideoRenderer"))
            .WhereNotNull()
            .Select(static j => new PlaylistVideoData(j))
            .ToArray() ?? [];

    /// <summary>
    /// Токен для получения следующей страницы видео.
    /// </summary>
    public string? ContinuationToken
    {
        get
        {
            // 1. Проверяем поле "continuations" внутри рендерера списка видео (стандартный случай)
            var videoListRenderer = EffectivePlaylistContents;
            if (videoListRenderer != null)
            {
                // Проверка явного поля continuations
                var continuations = videoListRenderer.Value.GetPropertyOrNull("continuations");
                if (continuations != null)
                {
                    var token = continuations.Value.EnumerateArrayOrNull()?.FirstOrNull()
                        ?.GetPropertyOrNull("nextContinuationData")
                        ?.GetPropertyOrNull("continuation")
                        ?.GetStringOrNull();
                    if (token != null) return token;
                }

                // Проверка последнего элемента в списке contents (для Liked Videos и сложных списков)
                var contents = videoListRenderer.Value.GetPropertyOrNull("contents");
                if (contents != null)
                {
                    var token = BridgeUtils.FindTokenInContents(contents.Value);
                    if (token != null) return token;
                }
            }

            // 2. Проверяем действия получения ответа (onResponseReceivedActions) - редко для browse, но возможно
            var actions = content.GetPropertyOrNull("onResponseReceivedActions");
            if (actions != null)
            {
                var continuationItems = actions.Value.EnumerateArrayOrNull()?.FirstOrNull()
                    ?.GetPropertyOrNull("appendContinuationItemsAction")
                    ?.GetPropertyOrNull("continuationItems");

                if (continuationItems != null)
                {
                    var token = BridgeUtils.FindTokenInContents(continuationItems.Value);
                    if (token != null) return token;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Данные о сессии пользователя для отслеживания контекста.
    /// </summary>
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