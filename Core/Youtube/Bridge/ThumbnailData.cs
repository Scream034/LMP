using System.Text.Json;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal class ThumbnailData(JsonElement content)
{
    public string? Url => content.GetPropertyOrNull("url")?.GetStringOrNull();

    public int? Width => content.GetPropertyOrNull("width")?.GetInt32OrNull();

    public int? Height => content.GetPropertyOrNull("height")?.GetInt32OrNull();
}
