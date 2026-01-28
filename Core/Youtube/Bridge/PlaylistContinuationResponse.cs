using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Ответ на запрос продолжения плейлиста (пагинация через browse)
/// </summary>
internal partial class PlaylistContinuationResponse(JsonElement content)
{
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

    public string? ContinuationToken =>
        content
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

internal partial class PlaylistContinuationResponse
{
    public static PlaylistContinuationResponse Parse(string raw) => new(Json.Parse(raw));
}