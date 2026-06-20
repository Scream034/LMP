using System.Globalization;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет ответ API YouTube на запрос страницы просмотра плейлиста (Browse).
/// </summary>
internal partial class PlaylistBrowseResponse(JsonElement content) : IPlaylistData
{
    private JsonElement? _cachedPlaylistContents;
    private bool _playlistContentsCached;

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

    /// <summary>
    /// Кэшированный результат поиска <c>playlistVideoListRenderer</c>.
    /// Вычисляется один раз при первом обращении через <see cref="ResolvePlaylistContents"/>.
    /// </summary>
    private JsonElement? EffectivePlaylistContents
    {
        get
        {
            if (!_playlistContentsCached)
            {
                _cachedPlaylistContents = ResolvePlaylistContents();
                _playlistContentsCached = true;
            }
            return _cachedPlaylistContents;
        }
    }

    /// <summary>
    /// Выполняет структурный поиск <c>playlistVideoListRenderer</c> по всем вкладкам,
    /// секциям и элементам browse-ответа. Не предполагает фиксированную позицию
    /// рендерера в массивах <c>contents</c>. При неудаче использует ограниченный
    /// descendant-fallback по всему корню ответа.
    /// </summary>
    private JsonElement? ResolvePlaylistContents()
    {
        var tabs = content
            .GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs is { } tabsEl && tabsEl.ValueKind == JsonValueKind.Array)
        {
            int tabCount = tabsEl.GetArrayLength();
            for (int t = 0; t < tabCount; t++)
            {
                var tabContent = tabsEl[t]
                    .GetPropertyOrNull("tabRenderer")
                    ?.GetPropertyOrNull("content");
                if (tabContent is null) continue;

                var found = FindRendererInTabContent(tabContent.Value);
                if (found is not null) return found;
            }
        }

        return content.FindFirstDescendantProperty("playlistVideoListRenderer");
    }

    /// <summary>
    /// Ищет <c>playlistVideoListRenderer</c> внутри содержимого одной вкладки.
    /// Проходит по всем секциям <c>sectionListRenderer</c> и всем элементам
    /// каждой <c>itemSectionRenderer</c> — YouTube может помещать список видео
    /// не в первый элемент (alert/notice/chips-блоки смещают позицию).
    /// </summary>
    private static JsonElement? FindRendererInTabContent(JsonElement tabContent)
    {
        var sections = tabContent
            .GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sections is { } arr && arr.ValueKind == JsonValueKind.Array)
        {
            int sectionCount = arr.GetArrayLength();
            for (int s = 0; s < sectionCount; s++)
            {
                var section = arr[s];

                var itemContents = section
                    .GetPropertyOrNull("itemSectionRenderer")
                    ?.GetPropertyOrNull("contents");

                if (itemContents is { } items && items.ValueKind == JsonValueKind.Array)
                {
                    int itemCount = items.GetArrayLength();
                    for (int i = 0; i < itemCount; i++)
                    {
                        var renderer = items[i].GetPropertyOrNull("playlistVideoListRenderer");
                        if (renderer is not null) return renderer;
                    }
                }

                var direct = section.GetPropertyOrNull("playlistVideoListRenderer");
                if (direct is not null) return direct;
            }
        }

        var directInTab = tabContent.GetPropertyOrNull("playlistVideoListRenderer");
        if (directInTab is not null) return directInTab;

        return tabContent.FindFirstDescendantProperty("playlistVideoListRenderer");
    }

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
            var videoListRenderer = EffectivePlaylistContents;
            if (videoListRenderer != null)
            {
                var continuations = videoListRenderer.Value.GetPropertyOrNull("continuations");
                if (continuations != null)
                {
                    var token = continuations.Value.EnumerateArrayOrNull()?.FirstOrNull()
                        ?.GetPropertyOrNull("nextContinuationData")
                        ?.GetPropertyOrNull("continuation")
                        ?.GetStringOrNull();
                    if (token != null) return token;
                }

                var contents = videoListRenderer.Value.GetPropertyOrNull("contents");
                if (contents != null)
                {
                    var token = BridgeUtils.FindTokenInContents(contents.Value);
                    if (token != null) return token;
                }
            }

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