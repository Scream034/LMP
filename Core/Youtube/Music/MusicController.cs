using System.Text.Json;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

    public string VisitorData { get; set; } = "";

    private JsonElement GetContext()
    {
        // Сериализуем все строковые значения через JsonSerializer для корректного экранирования
        var visitorDataJson = !string.IsNullOrEmpty(VisitorData) 
            ? JsonSerializer.Serialize(VisitorData) 
            : "null";
        
        var clientVersionJson = JsonSerializer.Serialize(YoutubeHttpHandler.MusicClientVersion);
        var hlJson = JsonSerializer.Serialize(YoutubeHttpHandler.GetHl());
        var glJson = JsonSerializer.Serialize(YoutubeHttpHandler.GetGl());

        var json = $$"""
        {
            "context": {
                "client": {
                    "clientName": "WEB_REMIX",
                    "clientVersion": {{clientVersionJson}},
                    "hl": {{hlJson}},
                    "gl": {{glJson}},
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
        var newVisitorData = root.GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();

        if (!string.IsNullOrWhiteSpace(newVisitorData) && newVisitorData != VisitorData)
        {
            VisitorData = newVisitorData;
        }
    }

    private void AttachVisitorDataToRequest(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(VisitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, VisitorData);
        }
    }

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

        // Используем Utf8JsonWriter для безопасной сериализации
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            
            // Копируем context
            foreach (var prop in GetContext().EnumerateObject()) 
                prop.WriteTo(writer);

            writer.WriteString("title", title);
            writer.WriteString("description", description);
            
            if (videoIds != null && videoIds.Count > 0)
            {
                writer.WriteStartArray("videoIds");
                foreach (var id in videoIds)
                    writer.WriteStringValue(id);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(ms.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

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

        // Используем Utf8JsonWriter для безопасной сериализации
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            
            foreach (var prop in GetContext().EnumerateObject()) 
                prop.WriteTo(writer);

            writer.WriteString("playlistId", playlistId);
            
            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WriteString("addedVideoId", videoId);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(ms.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

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
        var result = Json.Parse(content);
        UpdateVisitorData(result);
        
        return result;
    }
}