using System.Text;
using System.Text.Json;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

    // Сохраняем VisitorData
    private string _lastVisitorData = "CgtZZXhhaXZaNTBFUSjlyd3LBjIKCgJSVRIEGgAgHWLfAgrcAjE1LllUPU1Qb2lOR21KdXBvNlVKMkVKTUZ1TE9mZU9jZzIzQTJ1SXdicGN4Q0FzNXpNQjRoaFJTNENqU3pLTHlITHB5Tkk0bEJNRGpSWEFiRW1TNW13QWZvRlJoS01zNjBDOXU4RmtnYUNHdWZrSEhHUEJ6RHFjcU00cGctQThkeF8ySk5pT3JxRU1vYk5FOUVpQ1VOZ2VIak55QmVoYURmVGkyQVZkQjZIbDZNeDEweGlmWm80OGphMmxCdl9VdnAtRFJJV29ybGFJNm0zcTFEWno1RjZSaWJZeUNhQUpKSXowdFdvUkpaakJTSlZmU2U1XzBCZ25lcjdQYTJtWjB0X1FOdmNfOW8xSVVGNUp2Qk1LVzBobWZjLXBFQ2dRT01LcVlIWHB2VzRrbmMyZDEtMkczUGU4Y2kxY1ZScVBoTnBzZUk3NnBXSDNlUC1vRUQtcUtFZGQ2MHBOUQ%3D%3D";

    private JsonElement GetContext()
    {
        var visitorDataJson = JsonSerializer.Serialize(_lastVisitorData);
        var json = $$"""
        {
            "context": {
                "client": {
                    "clientName": "WEB_REMIX",
                    "clientVersion": "{{YoutubeHttpHandler.MusicClientVersion}}",
                    "hl": "en",
                    "gl": "US",
                    "platform": "DESKTOP",
                    "userInterfaceTheme": "USER_INTERFACE_THEME_DARK",
                    "visitorData": {{visitorDataJson}}
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

    private void UpdateVisitorData(JsonElement root)
    {
        var newVisitorData = root.GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();

        if (!string.IsNullOrWhiteSpace(newVisitorData) && newVisitorData != _lastVisitorData)
        {
            _lastVisitorData = newVisitorData;
        }
    }

    private void AttachVisitorDataToRequest(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_lastVisitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, _lastVisitorData);
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
}