using YoutubeExplode.Bridge;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils;

namespace YoutubeExplode.Channels;

internal class ChannelController(HttpClient http)
{
    private async ValueTask<ChannelPage> GetChannelPageAsync(
        string channelRoute,
        CancellationToken cancellationToken = default
    )
    {
        for (var retriesRemaining = 5; ; retriesRemaining--)
        {
            var channelPage = ChannelPage.TryParse(
                await http.GetStringAsync(
                    "https://www.youtube.com/" + channelRoute,
                    cancellationToken
                )
            );

            if (channelPage is null)
            {
                if (retriesRemaining > 0)
                    continue;

                throw new YoutubeExplodeException(
                    "Channel page is broken. Please try again in a few minutes."
                );
            }

            return channelPage;
        }
    }

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("channel/" + channelId, cancellationToken);

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        UserName userName,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("user/" + userName, cancellationToken);

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        ChannelSlug channelSlug,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("c/" + channelSlug, cancellationToken);

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        ChannelHandle channelHandle,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("@" + channelHandle, cancellationToken);

    public async ValueTask<ChannelPlaylistsResponse> GetChannelPlaylistsResponseAsync(
      ChannelId channelId,
      string? continuationToken,
      CancellationToken cancellationToken = default
  )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/browse"
        );

        // "EglwbGF5bGlzdHM%3D" - это base64 от protobuf params для вкладки "Playlists"
        // Если это первый запрос (нет токена), отправляем browseId канала и params.
        // Если есть токен продолжения, отправляем только его.

        var payload = continuationToken == null
            ? $$"""
            {
              "browseId": {{Json.Serialize(channelId)}},
              "params": "EglwbGF5bGlzdHM%3D", 
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20210408.08.00",
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """
            : $$"""
            {
              "continuation": {{Json.Serialize(continuationToken)}},
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20210408.08.00",
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """;

        request.Content = new StringContent(payload);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return ChannelPlaylistsResponse.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)
        );
    }

    public async ValueTask<ChannelPlaylistsResponse> GetChannelPlaylistsPageAsync(
    ChannelId channelId,
    string? continuationToken,
    CancellationToken cancellationToken = default
)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/browse"
        );

        // Магия здесь: params "EglwbGF5bGlzdHM%3D" == Tab "Playlists"
        var payload = continuationToken == null
            ? $$"""
            {
              "browseId": {{Json.Serialize(channelId)}},
              "params": "EglwbGF5bGlzdHM%3D",
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20210408.08.00",
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """
            : $$"""
            {
              "continuation": {{Json.Serialize(continuationToken)}},
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20210408.08.00",
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """;

        request.Content = new StringContent(payload);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return ChannelPlaylistsResponse.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)
        );
    }
}
