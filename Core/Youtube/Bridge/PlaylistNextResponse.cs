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

    public int? Count
    {
        get
        {
            var text = ContentRoot
                ?.GetPropertyOrNull("totalVideosText")
                ?.GetPropertyOrNull("runs")
                ?.GetArrayElementOrNull(0)
                ?.GetPropertyOrNull("text")
                ?.GetStringOrNull();

            if (text is not null && int.TryParse(text, CultureInfo.InvariantCulture, out var r1))
                return r1;

            text = ContentRoot
                ?.GetPropertyOrNull("videoCountText")
                ?.GetPropertyOrNull("runs")
                ?.GetArrayElementOrNull(2)
                ?.GetPropertyOrNull("text")
                ?.GetStringOrNull();

            if (text is not null && int.TryParse(text, CultureInfo.InvariantCulture, out var r2))
                return r2;

            return null;
        }
    }

    public IReadOnlyList<ThumbnailData> Thumbnails => Videos.FirstOrDefault()?.Thumbnails ?? [];

    public IReadOnlyList<PlaylistVideoData> Videos
    {
        get
        {
            var contents = ContentRoot?.GetPropertyOrNull("contents");
            if (contents is null || contents.Value.ValueKind != JsonValueKind.Array)
                return [];

            var array = contents.Value;
            int len = array.GetArrayLength();
            if (len == 0) return [];

            var result = new List<PlaylistVideoData>(len);
            for (int i = 0; i < len; i++)
            {
                var renderer = array[i].GetPropertyOrNull("playlistPanelVideoRenderer");
                if (renderer is not null)
                    result.Add(new PlaylistVideoData(renderer.Value));
            }

            return result.Count > 0 ? result : [];
        }
    }

    /// <summary>
    /// Данные о сессии пользователя.
    /// </summary>
    public string? VisitorData => content.GetVisitorData();
}

internal partial class PlaylistNextResponse
{
    public static PlaylistNextResponse Parse(string raw) => new(Json.Parse(raw));
}
