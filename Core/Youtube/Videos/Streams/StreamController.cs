using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Videos.Streams;

internal partial class StreamController(HttpClient http) : VideoController(http)
{
    public async ValueTask<PlayerSource> GetPlayerSourceAsync(
        CancellationToken cancellationToken = default
    )
    {
        var iframe = await Http.GetStringAsync(
            "https://www.youtube.com/iframe_api",
            cancellationToken
        );

        var version = MyRegex().Match(iframe).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(version))
            throw new YoutubeExplodeException("Failed to extract the player version.");

        return PlayerSource.Parse(
            await Http.GetStringAsync(
                $"https://www.youtube.com/s/player/{version}/player_ias.vflset/en_US/base.js",
                cancellationToken
            )
        );
    }

    public async ValueTask<DashManifest> GetDashManifestAsync(
        string url,
        CancellationToken cancellationToken = default
    ) => DashManifest.Parse(await Http.GetStringAsync(url, cancellationToken));
    [GeneratedRegex(@"player\\?/([0-9a-fA-F]{8})\\?/")]
    private static partial Regex MyRegex();
}
