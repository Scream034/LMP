using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Videos.Streams;

internal partial class StreamController(HttpClient http) : VideoController(http)
{
    public async ValueTask<PlayerSource> GetPlayerSourceAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var iframe = await Http.GetStringAsync(
                "https://www.youtube.com/iframe_api",
                cancellationToken
            );

            var version = PlayerRegex().Match(iframe).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(version))
            {
                Log.Warn("[StreamController] Failed to extract player version from iframe_api");
                throw new YoutubeExplodeException("Failed to extract the player version.");
            }

            var playerJs = await Http.GetStringAsync(
                $"https://www.youtube.com/s/player/{version}/player_ias.vflset/en_US/base.js",
                cancellationToken
            );

            return PlayerSource.Parse(playerJs);
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"[StreamController] Failed to fetch player source: {ex.Message}");
            throw new YoutubeExplodeException($"Failed to fetch player source: {ex.Message}");
        }
    }

    public async ValueTask<DashManifest> GetDashManifestAsync(
        string url,
        CancellationToken cancellationToken = default) 
        => DashManifest.Parse(await Http.GetStringAsync(url, cancellationToken));

    [GeneratedRegex(@"player\\?/([0-9a-fA-F]{8})\\?/")]
    private static partial Regex PlayerRegex();
}