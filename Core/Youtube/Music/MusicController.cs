using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

    // Кэшированные UTF-8 байты для статических ключей JSON
    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8Hl = "hl"u8.ToArray();
    private static readonly byte[] Utf8Gl = "gl"u8.ToArray();
    private static readonly byte[] Utf8VisitorData = "visitorData"u8.ToArray();
    private static readonly byte[] Utf8User = "user"u8.ToArray();
    private static readonly byte[] Utf8WebRemix = "WEB_REMIX"u8.ToArray();

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    public string VisitorData { get; set; } = "";

    /// <summary>
    /// Записывает context блок напрямую в writer — без промежуточного JsonDocument.
    /// </summary>
    private void WriteContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8WebRemix);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.MusicClientVersion);
        writer.WriteString(Utf8Hl, YoutubeHttpHandler.GetHl());
        writer.WriteString(Utf8Gl, YoutubeHttpHandler.GetGl());

        if (!string.IsNullOrEmpty(VisitorData))
            writer.WriteString(Utf8VisitorData, VisitorData);
        else
            writer.WriteNull(Utf8VisitorData);

        writer.WriteEndObject(); // client

        writer.WritePropertyName(Utf8User);
        writer.WriteStartObject();
        writer.WriteEndObject(); // user

        writer.WriteEndObject(); // context
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

    /// <summary>
    /// Создает HttpContent с использованием ArrayPool.
    /// Пишет JSON напрямую в pooled буфер, без двойного копирования.
    /// </summary>
    private HttpContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        // Используем ArrayBufferWriter с ArrayPool внутри
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            WriteContext(writer);
            writeBody(writer);
            writer.WriteEndObject();
        }

        // ByteArrayContent забирает копию из WrittenSpan — одна копия вместо двух
        var content = new ByteArrayContent(bufferWriter.WrittenSpan.ToArray());
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    public async ValueTask<MusicBrowseResponse> GetBrowseAsync(
        string? browseId = null,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/browse");
        AttachVisitorDataToRequest(request);

        request.Content = CreateJsonContent(writer =>
        {
            if (!string.IsNullOrEmpty(continuation))
                writer.WriteString("continuation", continuation);
            else if (!string.IsNullOrEmpty(browseId))
                writer.WriteString("browseId", browseId);
        });

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Парсим напрямую из потока — без промежуточной строки
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var jsonDoc = await Json.ParseAsync(stream, cancellationToken);
        UpdateVisitorData(jsonDoc);

        return new MusicBrowseResponse(jsonDoc);
    }

    public async Task SendLikeActionAsync(string endpoint, string videoId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}");
        AttachVisitorDataToRequest(request);

        request.Content = CreateJsonContent(writer =>
        {
            writer.WritePropertyName("target");
            writer.WriteStartObject();
            writer.WriteString("videoId", videoId);
            writer.WriteEndObject();
        });

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            UpdateVisitorData(await Json.ParseAsync(stream, cancellationToken));
        }
        catch { /* best effort */ }
    }

    public async Task<string> CreatePlaylistAsync(
        string title,
        string description,
        List<string>? videoIds,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/playlist/create");
        AttachVisitorDataToRequest(request);

        request.Content = CreateJsonContent(writer =>
        {
            writer.WriteString("title", title);
            writer.WriteString("description", description);

            if (videoIds is { Count: > 0 })
            {
                writer.WriteStartArray("videoIds");
                foreach (var id in videoIds)
                    writer.WriteStringValue(id);
                writer.WriteEndArray();
            }
        });

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var jsonDoc = await Json.ParseAsync(stream, cancellationToken);
        UpdateVisitorData(jsonDoc);

        return jsonDoc.GetPropertyOrNull("playlistId")?.GetStringOrNull()
            ?? throw new YoutubeExplodeException("Failed to create playlist.");
    }

    public async Task EditPlaylistAsync(
        string playlistId,
        string videoId,
        string action,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/browse/edit_playlist");
        AttachVisitorDataToRequest(request);

        request.Content = CreateJsonContent(writer =>
        {
            writer.WriteString("playlistId", playlistId);

            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WriteString("addedVideoId", videoId);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            UpdateVisitorData(await Json.ParseAsync(stream, cancellationToken));
        }
        catch { /* best effort */ }
    }

    public async Task<JsonElement> GetAccountMenuAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/account/account_menu");
        AttachVisitorDataToRequest(request);

        // Только context, без дополнительных полей
        request.Content = CreateJsonContent(_ => { });

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await Json.ParseAsync(stream, cancellationToken);
        UpdateVisitorData(result);

        return result;
    }
}