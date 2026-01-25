using YoutubeExplode.Bridge;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils;
using System.Text;

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

        string payload;

        if (continuationToken == null)
        {
            // Первый запрос: запрашиваем страницу канала с открытой вкладкой Плейлисты
            payload = $$"""
        {
            "browseId": "{{channelId.Value}}",
            "params": "EglwbGF5bGlzdHPyBgQKAkIA", 
            "context": {
                "client": {
                    "clientName": "WEB",
                    "clientVersion": "2.20240101.00.00",
                    "hl": "en",
                    "gl": "US"
                }
            }
        }
        """;
        }
        else
        {
            // Продолжение пагинации
            payload = $$"""
        {
            "continuation": {{Json.Serialize(continuationToken)}},
            "context": {
                "client": {
                    "clientName": "WEB",
                    "clientVersion": "2.20240101.00.00",
                    "hl": "en",
                    "gl": "US"
                }
            }
        }
        """;
        }

        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        return ChannelPlaylistsResponse.Parse(responseText);
    }
}