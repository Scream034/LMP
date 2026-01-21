using System.Text.RegularExpressions;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal partial class ChannelPage(string content)
{
    public string? Url =>
        MyRegex().Match(content).Groups[1].Value.NullIfWhiteSpace();

    public string? Id => Url?.SubstringAfter("channel/", StringComparison.OrdinalIgnoreCase);

    public string? Title =>
        MyRegex1().Match(content).Groups[1].Value.NullIfWhiteSpace();

    public string? LogoUrl =>
        MyRegex2().Match(content).Groups[1].Value.NullIfWhiteSpace();

    public static ChannelPage? TryParse(string raw)
    {
        if (!raw.Contains("og:url") || !raw.Contains("channel/"))
            return null;

        return new ChannelPage(raw);
    }

    [GeneratedRegex(@"<meta property=""og:url"" content=""(.*?)""")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"<meta property=""og:title"" content=""(.*?)""")]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"<meta property=""og:image"" content=""(.*?)""")]
    private static partial Regex MyRegex2();
}