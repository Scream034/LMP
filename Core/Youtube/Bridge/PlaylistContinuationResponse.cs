using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

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

            // Ищем действие добавления элементов продолжения
            var appendAction = actions.Value.EnumerateArrayOrNull()?.FirstOrNull()
                ?.GetPropertyOrNull("appendContinuationItemsAction");

            if (appendAction == null) return null;

            // Получаем список элементов
            var items = appendAction.Value.GetPropertyOrNull("continuationItems");
            if (items == null) return null;

            // Используем общий утилитный метод для поиска токена в конце списка
            return BridgeUtils.FindTokenInContents(items.Value);
        }
    }

    /// <summary>
    /// Данные о сессии пользователя.
    /// </summary>
    public string? VisitorData =>
        content
            .GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();
}

internal partial class PlaylistContinuationResponse
{
    public static PlaylistContinuationResponse Parse(string raw) => new(Json.Parse(raw));
}