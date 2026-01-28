using LMP.Core.Services;
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

        // Логика для приватных/специальных плейлистов
        if (browseId == "LL") browseId = "VLLL"; // Понравившиеся (Основной YouTube)
        else if (browseId == "LM") browseId = "VLLM"; // Понравившиеся (YouTube Music)
        else if (browseId == "WL") browseId = "VLWL"; // Посмотреть позже
        else if (!browseId.StartsWith("VL")) browseId = "VL" + browseId;

        var hl = YoutubeHttpHandler.GetHl();
        var gl = YoutubeHttpHandler.GetGl();

        // Мы используем здесь WEB-клиент, так как для приватных списков нужны куки.
        // YoutubeHttpHandler внедрит куки автоматически.
        // Также будет добавлен SAPISIDHASH, так как запрос НЕ помечен как Android-контекст.
        request.Content = new StringContent(
            // lang=json
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

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var playlistResponse = PlaylistBrowseResponse.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)
        );

        // Проверки сайдбара обычно достаточно для определения доступности,
        // но для пустых приватных плейлистов структура может отличаться.
        if (!playlistResponse.IsAvailable && browseId != "VLLL") 
            throw new PlaylistUnavailableException($"Плейлист '{playlistId}' недоступен.");

        return playlistResponse;
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

        // Примечание: Для пагинации «Понравившихся», иногда продолжение 'browse' лучше,
        // но эндпоинт 'next' работает, если у нас есть контекст видео.

        const int retriesCount = 3;
        for (var retriesRemaining = retriesCount; ; retriesRemaining--)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.youtube.com/youtubei/v1/next"
            );
            
            // Передаем visitor data, если она есть из предыдущих запросов
            if (!string.IsNullOrEmpty(visitorData))
            {
                request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);
            }

            request.Content = new StringContent(
                // lang=json
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
            // Резервный вариант для некоторых публичных плейлистов или миксов
            return await GetPlaylistNextResponseAsync(playlistId, null, 0, null, cancellationToken);
        }
    }
}