using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Helpers;

namespace LMP.Core.Youtube.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

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

    private static void WriteContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8WebRemix);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.MusicClientVersion);
        writer.WriteString(Utf8Hl, YoutubeHttpHandler.GetHl());
        writer.WriteString(Utf8Gl, YoutubeHttpHandler.GetGl());

        // Читаем глобальный VisitorData напрямую
        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            writer.WriteString(Utf8VisitorData, visitorData);
        else
            writer.WriteNull(Utf8VisitorData);

        writer.WriteEndObject(); // client

        writer.WritePropertyName(Utf8User);
        writer.WriteStartObject();
        writer.WriteEndObject(); // user

        writer.WriteEndObject(); // context
    }

    private static void UpdateVisitorData(JsonElement root)
    {
        var newVisitorData = root.GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();

        // Обновляем глобальный VisitorData напрямую
        if (!string.IsNullOrWhiteSpace(newVisitorData) && newVisitorData != YoutubeClientUtils.VisitorData)
        {
            YoutubeClientUtils.VisitorData = newVisitorData;
        }
    }

    private static void AttachVisitorDataToRequest(HttpRequestMessage request)
    {
        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);
        }
    }

    private static HttpContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            WriteContext(writer);
            writeBody(writer);
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(bufferWriter.WrittenSpan.ToArray());
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    private async Task<JsonElement> PostAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}");
        AttachVisitorDataToRequest(request);
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var jsonDoc = await Json.ParseAsync(stream, cancellationToken);
        UpdateVisitorData(jsonDoc);

        return jsonDoc;
    }

    private async Task PostFireAndForgetAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}");
        AttachVisitorDataToRequest(request);
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            UpdateVisitorData(await Json.ParseAsync(stream, cancellationToken));
        }
        catch { /* best effort */ }
    }

    #region Browse

    public async ValueTask<MusicBrowseResponse> GetBrowseAsync(
        string? browseId = null,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        var jsonDoc = await PostAsync("browse", writer =>
        {
            if (!string.IsNullOrEmpty(continuation))
                writer.WriteString("continuation", continuation);
            else if (!string.IsNullOrEmpty(browseId))
                writer.WriteString("browseId", browseId);
        }, cancellationToken);

        return new MusicBrowseResponse(jsonDoc);
    }

    #endregion

    #region Like

    public async Task SendLikeActionAsync(
        string endpoint, string videoId, CancellationToken cancellationToken)
    {
        await PostFireAndForgetAsync(endpoint, writer =>
        {
            writer.WritePropertyName("target");
            writer.WriteStartObject();
            writer.WriteString("videoId", videoId);
            writer.WriteEndObject();
        }, cancellationToken);
    }

    #endregion

    #region Account

    public async Task<JsonElement> GetAccountMenuAsync(
        CancellationToken cancellationToken = default)
    {
        return await PostAsync("account/account_menu", _ => { }, cancellationToken);
    }

    #endregion
}