using System.Globalization;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlaylistNextResponse(JsonElement content) : IPlaylistData
{
    private JsonElement? ContentRoot =>
        content
            .GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnWatchNextResults")
            ?.GetPropertyOrNull("playlist")
            ?.GetPropertyOrNull("playlist");

    public bool IsAvailable => ContentRoot is not null;

    public string? Title => ContentRoot?.GetPropertyOrNull("title")?.GetStringOrNull();

    public string? Author =>
        ContentRoot
            ?.GetPropertyOrNull("ownerName")
            ?.GetPropertyOrNull("simpleText")
            ?.GetStringOrNull();

    public string? ChannelId => null;

    public string? Description => null;

    public int? Count =>
        ContentRoot
            ?.GetPropertyOrNull("totalVideosText")
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull()
            ?.Pipe(static s =>
                int.TryParse(s, CultureInfo.InvariantCulture, out var result) ? result : (int?)null
            )
        ?? ContentRoot
            ?.GetPropertyOrNull("videoCountText")
            ?.GetPropertyOrNull("runs")
            ?.EnumerateArrayOrNull()
            ?.ElementAtOrNull(2)
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull()
            ?.Pipe(static s =>
                int.TryParse(s, CultureInfo.InvariantCulture, out var result) ? result : (int?)null
            );

    public IReadOnlyList<ThumbnailData> Thumbnails => Videos.FirstOrDefault()?.Thumbnails ?? [];

    public IReadOnlyList<PlaylistVideoData> Videos =>
        ContentRoot
            ?.GetPropertyOrNull("contents")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetPropertyOrNull("playlistPanelVideoRenderer"))
            .WhereNotNull()
            .Select(static j => new PlaylistVideoData(j))
            .ToArray() ?? [];

    /// <summary>
    /// Данные о сессии пользователя.
    /// </summary>
    public string? VisitorData => content.GetVisitorData();
}

internal partial class PlaylistNextResponse
{
    public static PlaylistNextResponse Parse(string raw) => new(Json.Parse(raw));
}
