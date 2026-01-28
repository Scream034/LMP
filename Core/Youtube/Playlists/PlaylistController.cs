using System.Net.Http.Headers;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube.Playlists;

internal class PlaylistController(HttpClient http)
{
    public async ValueTask<PlaylistBrowseResponse> GetPlaylistBrowseResponseAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/browse"
        );

        string browseId = playlistId.Value;

        if (browseId == "LL") browseId = "VLLL";
        else if (browseId == "LM") browseId = "VLLM";
        else if (browseId == "WL") browseId = "VLWL";
        else if (!browseId.StartsWith("VL")) browseId = "VL" + browseId;

        var hl = YoutubeHttpHandler.GetHl();
        var gl = YoutubeHttpHandler.GetGl();

        request.Content = new StringContent(
            $$"""
            {
              "browseId": {{Json.Serialize(browseId)}},
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "{{YoutubeHttpHandler.WebClientVersion}}",
                  "hl": {{Json.Serialize(hl)}},
                  "gl": {{Json.Serialize(gl)}},
                  "utcOffsetMinutes": 0
                }
              }
            }
            """
        );
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var playlistResponse = PlaylistBrowseResponse.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)
        );

        if (!playlistResponse.IsAvailable && browseId != "VLLL")
            throw new PlaylistUnavailableException($"Плейлист '{playlistId}' недоступен.");

        return playlistResponse;
    }

    /// <summary>
    /// Получает следующую страницу видео плейлиста через continuation token
    /// </summary>
    public async ValueTask<PlaylistContinuationResponse> GetPlaylistContinuationAsync(
        string continuationToken,
        string? visitorData = null,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/browse"
        );

        var hl = YoutubeHttpHandler.GetHl();
        var gl = YoutubeHttpHandler.GetGl();

        if (!string.IsNullOrEmpty(visitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);
        }

        request.Content = new StringContent(
            $$"""
            {
              "continuation": {{Json.Serialize(continuationToken)}},
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "{{YoutubeHttpHandler.WebClientVersion}}",
                  "hl": {{Json.Serialize(hl)}},
                  "gl": {{Json.Serialize(gl)}},
                  "utcOffsetMinutes": 0,
                  "visitorData": {{Json.Serialize(visitorData)}}
                }
              }
            }
            """
        );

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return PlaylistContinuationResponse.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)
        );
    }

    public async ValueTask<PlaylistNextResponse> GetPlaylistNextResponseAsync(
        PlaylistId playlistId,
        VideoId? videoId = null,
        int index = 0,
        string? visitorData = null,
        CancellationToken cancellationToken = default
    )
    {
        var hl = YoutubeHttpHandler.GetHl();
        var gl = YoutubeHttpHandler.GetGl();

        const int retriesCount = 3;
        for (var retriesRemaining = retriesCount; ; retriesRemaining--)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.youtube.com/youtubei/v1/next"
            );

            if (!string.IsNullOrEmpty(visitorData))
            {
                request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);
            }

            request.Content = new StringContent(
                $$"""
                {
                  "playlistId": {{Json.Serialize(playlistId)}},
                  "videoId": {{Json.Serialize(videoId)}},
                  "playlistIndex": {{Json.Serialize(index)}},
                  "context": {
                    "client": {
                      "clientName": "WEB",
                      "clientVersion": "{{YoutubeHttpHandler.WebClientVersion}}",
                      "hl": {{Json.Serialize(hl)}},
                      "gl": {{Json.Serialize(gl)}},
                      "utcOffsetMinutes": 0,
                      "visitorData": {{Json.Serialize(visitorData)}}
                    }
                  }
                }
                """
            );

            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var playlistResponse = PlaylistNextResponse.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken)
            );

            if (!playlistResponse.IsAvailable)
            {
                if (retriesRemaining > 0) continue;
                throw new PlaylistUnavailableException($"Плейлист '{playlistId}' недоступен.");
            }

            return playlistResponse;
        }
    }

    public async ValueTask<IPlaylistData> GetPlaylistResponseAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await GetPlaylistBrowseResponseAsync(playlistId, cancellationToken);
        }
        catch (PlaylistUnavailableException)
        {
            return await GetPlaylistNextResponseAsync(playlistId, null, 0, null, cancellationToken);
        }
    }
}