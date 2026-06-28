using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет ответ API YouTube на запрос продолжения списка видео (пагинация).
/// </summary>
internal partial class PlaylistContinuationResponse(JsonElement content)
{
    /// <summary>
    /// Список видео, полученных в текущей итерации пагинации.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos =>
        content
            .GetPropertyOrNull("onResponseReceivedActions")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetPropertyOrNull("playlistVideoRenderer"))
            .WhereNotNull()
            .Select(static j => new PlaylistVideoData(j))
            .ToArray() ?? [];

    /// <summary>
    /// Токен для получения следующей страницы, если она существует.
    /// </summary>
    public string? ContinuationToken
    {
        get
        {
            var actions = content.GetPropertyOrNull("onResponseReceivedActions");
            if (actions == null) return null;

            var appendAction = actions.Value.EnumerateArrayOrNull()?.FirstOrNull()
                ?.GetPropertyOrNull("appendContinuationItemsAction");

            if (appendAction == null) return null;

            var items = appendAction.Value.GetPropertyOrNull("continuationItems");
            if (items == null) return null;

            return BridgeUtils.FindTokenInContents(items.Value);
        }
    }

    /// <summary>
    /// Данные о сессии пользователя.
    /// </summary>
    public string? VisitorData => content.GetVisitorData();
}

internal partial class PlaylistContinuationResponse
{
    public static PlaylistContinuationResponse Parse(string raw) => new(Json.Parse(raw));
}