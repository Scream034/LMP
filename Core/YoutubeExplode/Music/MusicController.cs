using System.Text;
using System.Text.Json;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

    private JsonElement GetContext()
    {
        // Используем актуальную версию клиента из логов
        var json = """
        {
            "context": {
                "client": {
                    "clientName": "WEB_REMIX",
                    "clientVersion": "1.20260121.03.00",
                    "hl": "ru",
                    "gl": "RU",
                    "platform": "DESKTOP",
                    "userInterfaceTheme": "USER_INTERFACE_THEME_DARK"
                },
                "user": {},
                "request": {
                    "useSsl": true
                }
            }
        }
        """;
        return Json.Parse(json);
    }

    public async ValueTask<MusicBrowseResponse> GetBrowseAsync(string? browseId = null, string? continuation = null, CancellationToken cancellationToken = default)
    {
        // ВАЖНО: Для плейлистов (тип BROWSE) продолжение также идет на /browse
        // /next используется только для очереди воспроизведения (WatchNext)
        var url = $"{ApiUrl}/browse";
        
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            // Вставляем полный контекст
            var context = GetContext();
            foreach (var prop in context.EnumerateObject())
            {
                prop.WriteTo(writer);
            }

            // Логика выбора параметра
            if (!string.IsNullOrEmpty(continuation))
            {
                writer.WriteString("continuation", continuation);
            }
            else if (!string.IsNullOrEmpty(browseId))
            {
                writer.WriteString("browseId", browseId);
            }

            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(ms.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // отладка
        _ = File.WriteAllTextAsync($"music_browse_response{browseId ?? continuation}.json", await response.Content.ReadAsStringAsync(cancellationToken));

        return new MusicBrowseResponse(Json.Parse(await response.Content.ReadAsStringAsync(cancellationToken)));
    }

    public async Task SendLikeActionAsync(string endpoint, string videoId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}");

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            var context = GetContext();
            foreach (var prop in context.EnumerateObject()) prop.WriteTo(writer);

            writer.WriteStartObject("target");
            writer.WriteString("videoId", videoId);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(ms.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> CreatePlaylistAsync(string title, string description, List<string>? videoIds, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/playlist/create");

        // Ручная сериализация для простоты, так как структура простая
        var videoIdsJson = videoIds != null && videoIds.Count > 0
            ? $", \"videoIds\": {JsonSerializer.Serialize(videoIds)}"
            : "";
            
        // Внимание: JsonSerializer.Serialize(title) добавит кавычки, это ок
        var jsonTitle = JsonSerializer.Serialize(title);
        var jsonDesc = JsonSerializer.Serialize(description);
        
        // Получаем контекст как строку (убираем внешние скобки для вставки)
        var contextStr = GetContext().ToString();
        // Убираем первую { и последнюю }
        contextStr = contextStr.Substring(1, contextStr.Length - 2);

        var payload = $$"""
        {
          {{contextStr}},
          "title": {{jsonTitle}},
          "description": {{jsonDesc}}
          {{videoIdsJson}}
        }
        """;

        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = Json.Parse(json);

        return doc.GetPropertyOrNull("playlistId")?.GetStringOrNull()
            ?? throw new YoutubeExplodeException("Failed to create playlist.");
    }

    public async Task EditPlaylistAsync(string playlistId, string videoId, string action, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/browse/edit_playlist");

        var contextStr = GetContext().ToString();
        contextStr = contextStr.Substring(1, contextStr.Length - 2);

        var payload = $$"""
        {
          {{contextStr}},
          "playlistId": {{JsonSerializer.Serialize(playlistId)}},
          "actions": [
            {
              "action": {{JsonSerializer.Serialize(action)}},
              "addedVideoId": {{JsonSerializer.Serialize(videoId)}}
            }
          ]
        }
        """;

        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}