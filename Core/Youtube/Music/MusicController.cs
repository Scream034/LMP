using System.Text;
using System.Text.Json;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

    // VisitorData - это сессионный ID, который YouTube использует для трекинга контекста.
    // Если он меняется в ответе, его нужно обновить для следующих запросов.
    public string VisitorData { get; set; } = "";

    private JsonElement GetContext()
    {
        var visitorDataJson = !string.IsNullOrEmpty(VisitorData) ? JsonSerializer.Serialize(VisitorData) : "null";

        var json = $$"""
        {
            "context": {
                "client": {
                    "clientName": "WEB_REMIX",
                    "clientVersion": "{{YoutubeHttpHandler.MusicClientVersion}}",
                    "hl": "ru",
                    "gl": "RU",
                    "visitorData": {{visitorDataJson}}
                },
                "user": {}
            }
        }
        """;
        return Json.Parse(json);
    }

    private void UpdateVisitorData(JsonElement root)
    {
        // Пытаемся найти новый visitorData в ответе (responseContext)
        var newVisitorData = root.GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();

        if (!string.IsNullOrWhiteSpace(newVisitorData) && newVisitorData != VisitorData)
        {
            VisitorData = newVisitorData;
            // Можно залогировать смену контекста для отладки
        }
    }

    private void AttachVisitorDataToRequest(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(VisitorData))
        {
            // Передаем в Handler, чтобы он добавил заголовок X-Goog-Visitor-Id
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, VisitorData);
        }
    }

    // Пример метода Browse (остальные аналогично обновляют VisitorData)
    public async ValueTask<MusicBrowseResponse> GetBrowseAsync(string? browseId = null, string? continuation = null, CancellationToken cancellationToken = default)
    {
        var url = $"{ApiUrl}/browse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        AttachVisitorDataToRequest(request);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in GetContext().EnumerateObject()) prop.WriteTo(writer);

            if (!string.IsNullOrEmpty(continuation))
                writer.WriteString("continuation", continuation);
            else if (!string.IsNullOrEmpty(browseId))
                writer.WriteString("browseId", browseId);

            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(ms.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var jsonDoc = Json.Parse(content);

        // ВАЖНО: Обновляем контекст из ответа
        UpdateVisitorData(jsonDoc);

        return new MusicBrowseResponse(jsonDoc);
    }

    public async Task SendLikeActionAsync(string endpoint, string videoId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}");
        AttachVisitorDataToRequest(request);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in GetContext().EnumerateObject()) prop.WriteTo(writer);

            writer.WriteStartObject("target");
            writer.WriteString("videoId", videoId);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(ms.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        try { UpdateVisitorData(Json.Parse(await response.Content.ReadAsStringAsync(cancellationToken))); } catch { }
    }

    public async Task<string> CreatePlaylistAsync(string title, string description, List<string>? videoIds, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/playlist/create");
        AttachVisitorDataToRequest(request);

        var videoIdsJson = videoIds != null && videoIds.Count > 0
            ? $", \"videoIds\": {JsonSerializer.Serialize(videoIds)}"
            : "";

        var jsonTitle = JsonSerializer.Serialize(title);
        var jsonDesc = JsonSerializer.Serialize(description);

        var contextStr = GetContext().ToString();
        contextStr = contextStr[1..^1];

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

        var jsonDoc = Json.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        UpdateVisitorData(jsonDoc);

        return jsonDoc.GetPropertyOrNull("playlistId")?.GetStringOrNull()
            ?? throw new YoutubeExplodeException("Failed to create playlist.");
    }

    public async Task EditPlaylistAsync(string playlistId, string videoId, string action, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/browse/edit_playlist");
        AttachVisitorDataToRequest(request);

        var contextStr = GetContext().ToString();
        contextStr = contextStr[1..^1];

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

        try { UpdateVisitorData(Json.Parse(await response.Content.ReadAsStringAsync(cancellationToken))); } catch { }
    }

    public async Task<JsonElement> GetAccountMenuAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/account/account_menu");
        AttachVisitorDataToRequest(request);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in GetContext().EnumerateObject()) prop.WriteTo(writer);
            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(ms.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return Json.Parse(content);
    }
}