using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using System.Globalization;

namespace LMP.Core.Youtube.Music;

internal sealed class PlaylistSyncController(HttpClient http)
{
    private static readonly string[] DateFormats =
    [
        "d MMM yyyy 'г.'",
        "d MMMM yyyy 'г.'",
        "d MMM yyyy",
        "d MMMM yyyy",
        "MMM d, yyyy",
        "MMMM d, yyyy",
    ];

    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    private const string WebApiUrl = "https://www.youtube.com/youtubei/v1";
    private const string MusicApiUrl = "https://music.youtube.com/youtubei/v1";
    private const string ConstantParams = "qAIC";
    private const string GreyOutPolicy = "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8Request = "request"u8.ToArray();
    private static readonly byte[] Utf8UseSsl = "useSsl"u8.ToArray();
    private static readonly byte[] Utf8Web = "WEB"u8.ToArray();
    private static readonly byte[] Utf8WebRemix = "WEB_REMIX"u8.ToArray();
    private static readonly byte[] Utf8Hl = "hl"u8.ToArray();
    private static readonly byte[] Utf8Gl = "gl"u8.ToArray();
    private static readonly byte[] Utf8VisitorData = "visitorData"u8.ToArray();

    #region Context Writers

    /// <summary>
    /// Minimal WEB context for playlist listing.
    /// </summary>
    private static void WriteWebContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8Web);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.WebClientVersion);
        writer.WriteEndObject();

        writer.WritePropertyName(Utf8Request);
        writer.WriteStartObject();
        writer.WriteBoolean(Utf8UseSsl, false);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// WEB_REMIX context for YouTube Music browse requests.
    /// </summary>
    private static void WriteWebRemixContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8WebRemix);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.MusicClientVersion);
        writer.WriteString(Utf8Hl, YoutubeHttpHandler.GetHl());
        writer.WriteString(Utf8Gl, YoutubeHttpHandler.GetGl());

        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            writer.WriteString(Utf8VisitorData, visitorData);
        else
            writer.WriteNull(Utf8VisitorData);

        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    #endregion

    #region HTTP

    private static ReadOnlyMemoryContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        // Оптимизация: нулевое копирование промежуточного массива
        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    private async Task<JsonElement> PostWebAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{WebApiUrl}/{endpoint}?prettyPrint=false");
        request.Content = CreateJsonContent(writer =>
        {
            WriteWebContext(writer);
            writeBody(writer);
        });

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await Json.ParseAsync(stream, ct);
    }

    private async Task<JsonElement> PostMusicAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MusicApiUrl}/{endpoint}?prettyPrint=false");

        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);

        request.Content = CreateJsonContent(writer =>
        {
            WriteWebRemixContext(writer);
            writeBody(writer);
        });

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var root = await Json.ParseAsync(stream, ct);

        var freshVisitorData = root.GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();

        if (!string.IsNullOrWhiteSpace(freshVisitorData) && freshVisitorData != YoutubeClientUtils.VisitorData)
            YoutubeClientUtils.VisitorData = freshVisitorData;

        return root;
    }

    #endregion

    #region Full Playlist Data (WEB_REMIX)

    /// <summary>
    /// Получает полный снимок плейлиста за минимальное количество HTTP-запросов.
    /// Использует WEB_REMIX browse, который возвращает метаданные и треки одновременно.
    /// </summary>
    public async Task<FullPlaylistSyncData> GetFullPlaylistDataAsync(
      string playlistId,
      CancellationToken ct = default)
    {
        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal)
            ? playlistId
            : "VL" + playlistId;

        var root = await PostMusicAsync("browse", writer =>
        {
            writer.WriteString("browseId", browseId);
        }, ct);

        var title = ParsePlaylistTitle(root);
        var description = ParsePlaylistDescription(root);
        var thumbnailUrl = ParsePlaylistThumbnail(root);
        var viewCount = ParsePlaylistViewCount(root);
        var releaseDate = ParsePlaylistReleaseDate(root); // Свойство переведено на ReleaseDate

        var tracks = new List<RemoteTrackInfo>(64);
        ParseMusicPlaylistTracks(root, tracks);

        var continuationToken = ExtractMusicContinuationToken(root);
        int page = 0;

        while (!string.IsNullOrEmpty(continuationToken) && page < 50)
        {
            ct.ThrowIfCancellationRequested();

            var contRoot = await PostMusicAsync("browse", writer =>
            {
                writer.WriteString("continuation", continuationToken);
            }, ct);

            int prevCount = tracks.Count;
            ParseMusicContinuationTracks(contRoot, tracks);

            if (tracks.Count == prevCount)
                break;

            continuationToken = ExtractMusicContinuationToken(contRoot);
            page++;
        }

        Log.Debug($"[PlaylistSync] Parsed header: title='{title ?? ""}', " +
                  $"desc='{description?.Length ?? 0} chars', thumb='{thumbnailUrl ?? "null"}', " +
                  $"views={viewCount?.ToString() ?? "null"}, date='{releaseDate ?? null}'");
        Log.Debug($"[PlaylistSync] WEB_REMIX: fetched {tracks.Count} tracks for {playlistId}");

        return new FullPlaylistSyncData
        {
            Title = title,
            Description = description,
            ThumbnailUrl = thumbnailUrl,
            ViewCount = viewCount,
            ReleaseDate = releaseDate, // Заменено на ReleaseDate
            Tracks = tracks
        };
    }

    #endregion

    #region Header Parsers

    /// <summary>
    /// Извлекает количество просмотров плейлиста.
    /// Источник: <c>musicResponsiveHeaderRenderer.secondSubtitle.runs[0].text</c>.
    /// Парсит локализованные строки вида «1,234,567 просмотров», «5K views».
    /// </summary>
    private static long? ParsePlaylistViewCount(JsonElement root)
    {
        var headerRenderer = ResolveMusicResponsiveHeader(root);
        if (headerRenderer is null) return null;

        var text = headerRenderer.Value
            .GetPropertyOrNull("secondSubtitle")
            ?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull();

        return ParseLongFromText(text);
    }

    /// <summary>
    /// Извлекает и парсит дату обновления плейлиста из <c>musicResponsiveHeaderRenderer.subtitle.runs</c>.
    /// </summary>
    private static DateOnly? ParsePlaylistReleaseDate(JsonElement root)
    {
        var headerRenderer = ResolveMusicResponsiveHeader(root);
        if (headerRenderer is null) return null;

        var runs = headerRenderer.Value
            .GetPropertyOrNull("subtitle")
            ?.GetPropertyOrNull("runs");

        if (runs?.ValueKind != JsonValueKind.Array)
            return null;

        int len = runs.Value.GetArrayLength();

        // Прямой путь: runs[4] обычно хранит дату/год в YouTube Music
        if (len > 4)
        {
            var text = runs.Value[4].GetPropertyOrNull("text")?.GetStringOrNull();
            var parsed = TryParseDateText(text);
            if (parsed.HasValue) return parsed;
        }

        // Fallback: сканируем все runs справа налево в поисках даты
        for (int i = len - 1; i >= 0; i--)
        {
            var text = runs.Value[i].GetPropertyOrNull("text")?.GetStringOrNull();
            if (text != null && (ParseYearFromText(text).HasValue || IsRelativeDate(text)))
            {
                var parsed = TryParseDateText(text);
                if (parsed.HasValue) return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Парсит строку даты из YouTube Music (обычно год «2024» или полная дата).
    /// </summary>
    private static DateOnly? TryParseDateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var clean = text.Trim();

        // Строгий парсинг
        if (DateOnly.TryParseExact(clean, DateFormats, RuCulture, DateTimeStyles.AllowWhiteSpaces, out var dateRu))
            return dateRu;
        if (DateOnly.TryParseExact(clean, DateFormats, EnCulture, DateTimeStyles.AllowWhiteSpaces, out var dateEn))
            return dateEn;

        // Гибкий парсинг
        if (DateOnly.TryParse(clean, RuCulture, DateTimeStyles.AllowWhiteSpaces, out var flexRu))
            return flexRu;
        if (DateOnly.TryParse(clean, EnCulture, DateTimeStyles.AllowWhiteSpaces, out var flexEn))
            return flexEn;

        // Относительные даты
        if (IsRelativeDate(clean))
        {
            var now = DateTime.UtcNow;

            if (clean.Contains("сегодня", StringComparison.OrdinalIgnoreCase) ||
                clean.Contains("today", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(now);

            if (clean.Contains("вчера", StringComparison.OrdinalIgnoreCase) ||
                clean.Contains("yesterday", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(now.AddDays(-1));

            var val = ParseLongFromText(clean);
            if (val.HasValue)
            {
                int delta = (int)val.Value;
                if (clean.Contains("недел", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("week", StringComparison.OrdinalIgnoreCase))
                    return DateOnly.FromDateTime(now.AddDays(-delta * 7));
                if (clean.Contains("месяц", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("month", StringComparison.OrdinalIgnoreCase))
                    return DateOnly.FromDateTime(now.AddMonths(-delta));
                if (clean.Contains("час", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("hour", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("минут", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("minute", StringComparison.OrdinalIgnoreCase))
                    return DateOnly.FromDateTime(now);

                return DateOnly.FromDateTime(now.AddDays(-delta));
            }
        }

        // Год
        var year = ParseYearFromText(clean);
        if (year.HasValue)
            return new DateOnly(year.Value, 1, 1);

        return null;
    }

    /// <summary>
    /// Находит <c>musicResponsiveHeaderRenderer</c> в browse-ответе.
    /// Поддерживает оба варианта: editable header (свои плейлисты)
    /// и обычный header (чужие плейлисты).
    /// </summary>
    private static JsonElement? ResolveMusicResponsiveHeader(JsonElement root)
    {
        var header = root.GetPropertyOrNull("header");
        if (header is null) return null;

        // Path 1: musicEditablePlaylistDetailHeaderRenderer.header.musicResponsiveHeaderRenderer
        var editable = header.Value
            .GetPropertyOrNull("musicEditablePlaylistDetailHeaderRenderer")
            ?.GetPropertyOrNull("header")
            ?.GetPropertyOrNull("musicResponsiveHeaderRenderer");

        if (editable is not null) return editable;

        // Path 2: musicResponsiveHeaderRenderer напрямую
        var direct = header.Value.GetPropertyOrNull("musicResponsiveHeaderRenderer");
        if (direct is not null) return direct;

        // Path 3: descendant search
        return header.Value.FindFirstDescendantProperty("musicResponsiveHeaderRenderer");
    }

    /// <summary>
    /// Парсит число из локализованной строки. Игнорирует разделители
    /// тысяч (запятая, точка, NBSP) и нечисловой суффикс.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long? ParseLongFromText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        long result = 0;
        bool foundDigit = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                result = result * 10 + (c - '0');
                foundDigit = true;
            }
            else if (foundDigit && c != ',' && c != '.' && c != ' ' && c != '\u00A0')
            {
                break;
            }
        }

        return foundDigit ? result : null;
    }

    /// <summary>
    /// Парсит 4-значный год (1900..2100) из произвольной строки.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int? ParseYearFromText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 4) return null;

        for (int i = 0; i <= text.Length - 4; i++)
        {
            if (char.IsAsciiDigit(text[i])
                && char.IsAsciiDigit(text[i + 1])
                && char.IsAsciiDigit(text[i + 2])
                && char.IsAsciiDigit(text[i + 3]))
            {
                // Граница: убедиться, что это ровно 4 цифры (не часть большего числа)
                bool leftOk = i == 0 || !char.IsAsciiDigit(text[i - 1]);
                bool rightOk = i + 4 == text.Length || !char.IsAsciiDigit(text[i + 4]);

                if (leftOk && rightOk)
                {
                    int year = (text[i] - '0') * 1000
                             + (text[i + 1] - '0') * 100
                             + (text[i + 2] - '0') * 10
                             + (text[i + 3] - '0');
                    if (year >= 1900 && year <= 2100)
                        return year;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Извлекает строковое значение из нескольких форм представления YouTube JSON:
    /// string, content, simpleText, text, runs[0].text.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? GetTextValue(JsonElement? element)
    {
        if (element is null)
            return null;

        var value = element.Value;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return value.GetPropertyOrNull("content")?.GetStringOrNull()
            ?? value.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? value.GetPropertyOrNull("text")?.GetStringOrNull()
            ?? value.GetPropertyOrNull("runs")
                ?.GetFirstArrayElementOrNull()
                ?.GetPropertyOrNull("text")
                ?.GetStringOrNull();
    }

    /// <summary>
    /// Путь заголовка в WEB_REMIX нестабилен между вариантами страницы.
    /// Сначала используется быстрый путь pageHeaderRenderer.pageTitle,
    /// затем безопасные fallback-пути внутри header subtree.
    /// </summary>
    private static string? ParsePlaylistTitle(JsonElement root)
    {
        var header = root.GetPropertyOrNull("header");
        if (header is null)
            return null;

        return GetTextValue(
                   header.Value
                       .GetPropertyOrNull("pageHeaderRenderer")
                       ?.GetPropertyOrNull("pageTitle"))
            ?? GetTextValue(
                   header.Value
                       .GetPropertyOrNull("pageHeaderRenderer")
                       ?.GetPropertyOrNull("content")
                       ?.GetPropertyOrNull("pageHeaderViewModel")
                       ?.GetPropertyOrNull("title"))
            ?? GetTextValue(header.Value.FindFirstDescendantProperty("pageTitle"))
            ?? GetTextValue(header.Value.FindFirstDescendantProperty("title"));
    }

    /// <summary>
    /// Путь описания в WEB_REMIX может приходить через descriptionPreviewViewModel
    /// либо через альтернативные рендереры. Сначала используется точный путь,
    /// затем ограниченный fallback внутри header subtree.
    /// </summary>
    private static string? ParsePlaylistDescription(JsonElement root)
    {
        var header = root.GetPropertyOrNull("header");
        if (header is null)
            return null;

        return GetTextValue(
                   header.Value
                       .GetPropertyOrNull("pageHeaderRenderer")
                       ?.GetPropertyOrNull("content")
                       ?.GetPropertyOrNull("pageHeaderViewModel")
                       ?.GetPropertyOrNull("description")
                       ?.GetPropertyOrNull("descriptionPreviewViewModel")
                       ?.GetPropertyOrNull("description")
                       ?.GetPropertyOrNull("content"))
            ?? GetTextValue(
                   header.Value
                       .GetPropertyOrNull("pageHeaderRenderer")
                       ?.GetPropertyOrNull("content")
                       ?.GetPropertyOrNull("pageHeaderViewModel")
                       ?.GetPropertyOrNull("description"))
            ?? GetTextValue(header.Value.FindFirstDescendantProperty("description"));
    }

    /// <summary>
    /// Берёт URL обложки из sources[last] для максимального разрешения.
    /// Если точный путь не найден, используется fallback-поиск массива sources внутри header.
    /// </summary>
    private static string? ParsePlaylistThumbnail(JsonElement root)
    {
        var header = root.GetPropertyOrNull("header");
        if (header is null)
            return null;

        var sources = header.Value
            .GetPropertyOrNull("pageHeaderRenderer")
            ?.GetPropertyOrNull("content")
            ?.GetPropertyOrNull("pageHeaderViewModel")
            ?.GetPropertyOrNull("image")
            ?.GetPropertyOrNull("contentPreviewImageViewModel")
            ?.GetPropertyOrNull("image")
            ?.GetPropertyOrNull("sources");

        if (sources is null)
            sources = header.Value.FindFirstDescendantProperty("sources");

        if (sources is null || sources.Value.ValueKind != JsonValueKind.Array)
            return null;

        int len = sources.Value.GetArrayLength();
        if (len == 0)
            return null;

        return sources.Value[len - 1]
            .GetPropertyOrNull("url")
            ?.GetStringOrNull();
    }

    #endregion

    #region Track Parsers

    /// <summary>
    /// Ищет массив contents у playlistVideoListRenderer.
    /// Сначала проверяет выбранные вкладки, затем все вкладки,
    /// затем использует ограниченный descendant fallback.
    /// </summary>
    private static JsonElement? GetInitialTrackContents(JsonElement root)
    {
        var tabs = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs is { } tabsElement && tabsElement.ValueKind == JsonValueKind.Array)
        {
            int len = tabsElement.GetArrayLength();

            for (int i = 0; i < len; i++)
            {
                var tabRenderer = tabsElement[i].GetPropertyOrNull("tabRenderer");
                if (tabRenderer?.GetPropertyOrNull("selected")?.GetBooleanOrNull() != true)
                    continue;

                var selectedContents = TryGetTrackContentsFromTab(tabRenderer.Value);
                if (selectedContents is not null)
                    return selectedContents;
            }

            for (int i = 0; i < len; i++)
            {
                var tabRenderer = tabsElement[i].GetPropertyOrNull("tabRenderer");
                if (tabRenderer is null)
                    continue;

                var contents = TryGetTrackContentsFromTab(tabRenderer.Value);
                if (contents is not null)
                    return contents;
            }
        }

        var listRenderer = root.FindFirstDescendantProperty("playlistVideoListRenderer");
        return listRenderer?.GetPropertyOrNull("contents");
    }

    /// <summary>
    /// Извлекает playlistVideoListRenderer.contents из содержимого вкладки.
    /// </summary>
    private static JsonElement? TryGetTrackContentsFromTab(JsonElement tabRenderer)
    {
        var content = tabRenderer.GetPropertyOrNull("content");
        if (content is null)
            return null;

        var sectionContents = content.Value
            .GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sectionContents is { } sectionArray &&
            sectionArray.ValueKind == JsonValueKind.Array &&
            sectionArray.GetArrayLength() > 0)
        {
            for (int i = 0; i < sectionArray.GetArrayLength(); i++)
            {
                var section = sectionArray[i];

                var itemSectionContents = section
                    .GetPropertyOrNull("itemSectionRenderer")
                    ?.GetPropertyOrNull("contents");

                if (itemSectionContents is { } itemArray &&
                    itemArray.ValueKind == JsonValueKind.Array &&
                    itemArray.GetArrayLength() > 0)
                {
                    for (int j = 0; j < itemArray.GetArrayLength(); j++)
                    {
                        var direct = itemArray[j]
                            .GetPropertyOrNull("playlistVideoListRenderer")
                            ?.GetPropertyOrNull("contents");

                        if (direct is not null)
                            return direct;
                    }
                }

                var directSection = section
                    .GetPropertyOrNull("playlistVideoListRenderer")
                    ?.GetPropertyOrNull("contents");

                if (directSection is not null)
                    return directSection;
            }
        }

        var directContent = content.Value
            .GetPropertyOrNull("playlistVideoListRenderer")
            ?.GetPropertyOrNull("contents");

        if (directContent is not null)
            return directContent;

        var fallback = content.Value.FindFirstDescendantProperty("playlistVideoListRenderer");
        return fallback?.GetPropertyOrNull("contents");
    }

    private static void ParseMusicPlaylistTracks(JsonElement root, List<RemoteTrackInfo> results)
    {
        var contents = GetInitialTrackContents(root);
        if (contents is null || contents.Value.ValueKind != JsonValueKind.Array)
            return;

        ParseTrackArray(contents.Value, results);
    }

    private static void ParseMusicContinuationTracks(JsonElement root, List<RemoteTrackInfo> results)
    {
        var continuationItems = root.GetPropertyOrNull("onResponseReceivedActions")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems");

        if (continuationItems is { } actionItems && actionItems.ValueKind == JsonValueKind.Array)
        {
            ParseTrackArray(actionItems, results);
            return;
        }

        var continuationContents = root.GetPropertyOrNull("continuationContents")
            ?.GetPropertyOrNull("musicPlaylistShelfContinuation")
            ?.GetPropertyOrNull("contents");

        if (continuationContents is { } shelfItems && shelfItems.ValueKind == JsonValueKind.Array)
            ParseTrackArray(shelfItems, results);
    }

    private static void ParseTrackArray(JsonElement array, List<RemoteTrackInfo> results)
    {
        int len = array.GetArrayLength();
        for (int i = 0; i < len; i++)
        {
            var item = array[i];

            var renderer = item.GetPropertyOrNull("playlistVideoRenderer");
            if (renderer is null)
                renderer = item.FindFirstDescendantProperty("playlistVideoRenderer");

            if (renderer is null)
                continue;

            if (TryParseRemoteTrack(renderer.Value, results.Count, out var track))
                results.Add(track);
        }
    }

    /// <summary>
    /// Парсит один playlistVideoRenderer в RemoteTrackInfo.
    /// </summary>
    private static bool TryParseRemoteTrack(
        JsonElement renderer,
        int position,
        out RemoteTrackInfo track)
    {
        track = default;

        var videoId = renderer.GetPropertyOrNull("videoId")?.GetStringOrNull();
        var setVideoId = renderer.GetPropertyOrNull("setVideoId")?.GetStringOrNull();

        if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(setVideoId))
            return false;

        var title = GetTextValue(renderer.GetPropertyOrNull("title")) ?? "";
        var author = ExtractArtist(renderer);

        int durationSeconds = 0;
        var lenStr = renderer.GetPropertyOrNull("lengthSeconds")?.GetStringOrNull();
        if (!string.IsNullOrEmpty(lenStr))
            int.TryParse(lenStr, out durationSeconds);

        var thumbUrl = ExtractLastThumbnailUrl(renderer);

        var policy = renderer.GetPropertyOrNull("musicItemRendererDisplayPolicy")?.GetStringOrNull();
        bool isPlayable = !string.Equals(policy, GreyOutPolicy, StringComparison.Ordinal);

        track = new RemoteTrackInfo(
            VideoId: videoId,
            SetVideoId: setVideoId,
            Title: title,
            Author: author,
            DurationSeconds: durationSeconds,
            ThumbnailUrl: thumbUrl,
            IsPlayable: isPlayable,
            Position: position);

        return true;
    }

    /// <summary>
    /// Извлекает автора из shortBylineText.runs. 
    /// Поддерживает официальных артистов и пользовательские каналы.
    /// Fallback — первый непустой текст.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ExtractArtist(JsonElement renderer)
    {
        var runs = renderer.GetPropertyOrNull("shortBylineText")
            ?.GetPropertyOrNull("runs");

        if (runs is null || runs.Value.ValueKind != JsonValueKind.Array)
            return "";

        string? fallback = null;
        int len = runs.Value.GetArrayLength();

        for (int i = 0; i < len; i++)
        {
            var run = runs.Value[i];
            var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
            if (string.IsNullOrEmpty(text))
                continue;

            fallback ??= text;

            var pageType = run.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                ?.GetPropertyOrNull("pageType")
                ?.GetStringOrNull();

            // Расширенная проверка типов каналов
            if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
                return text;
        }

        return fallback ?? "";
    }

    /// <summary>
    /// Берёт последний thumbnail из массива для максимального разрешения.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ExtractLastThumbnailUrl(JsonElement renderer)
    {
        var thumbs = renderer.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails");

        if (thumbs is null)
            thumbs = renderer.FindFirstDescendantProperty("thumbnails");

        if (thumbs is null || thumbs.Value.ValueKind != JsonValueKind.Array)
            return "";

        int len = thumbs.Value.GetArrayLength();
        if (len == 0)
            return "";

        return thumbs.Value[len - 1]
            .GetPropertyOrNull("url")
            ?.GetStringOrNull() ?? "";
    }

    #endregion

    #region Continuation Token

    /// <summary>
    /// Извлекает continuation token из initial browse или continuation response.
    /// </summary>
    private static string? ExtractMusicContinuationToken(JsonElement root)
    {
        var actionItems = root.GetPropertyOrNull("onResponseReceivedActions")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems");

        if (actionItems is { } actionArray && actionArray.ValueKind == JsonValueKind.Array)
        {
            var token = TryGetTokenFromLastItem(actionArray);
            if (!string.IsNullOrEmpty(token))
                return token;
        }

        var continuationContents = root.GetPropertyOrNull("continuationContents")
            ?.GetPropertyOrNull("musicPlaylistShelfContinuation");

        if (continuationContents is { } continuationRoot)
        {
            var directToken = continuationRoot
                .GetPropertyOrNull("continuations")
                ?.GetFirstArrayElementOrNull()
                ?.GetPropertyOrNull("nextContinuationData")
                ?.GetPropertyOrNull("continuation")
                ?.GetStringOrNull();

            if (!string.IsNullOrEmpty(directToken))
                return directToken;

            var shelfItems = continuationRoot.GetPropertyOrNull("contents");
            if (shelfItems is { } shelfArray && shelfArray.ValueKind == JsonValueKind.Array)
            {
                var token = TryGetTokenFromLastItem(shelfArray);
                if (!string.IsNullOrEmpty(token))
                    return token;
            }
        }

        var initialContents = GetInitialTrackContents(root);
        if (initialContents is { } initialArray && initialArray.ValueKind == JsonValueKind.Array)
        {
            var token = TryGetTokenFromLastItem(initialArray);
            if (!string.IsNullOrEmpty(token))
                return token;
        }

        var listRenderer = root.FindFirstDescendantProperty("playlistVideoListRenderer");
        return listRenderer?
            .GetPropertyOrNull("continuations")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("nextContinuationData")
            ?.GetPropertyOrNull("continuation")
            ?.GetStringOrNull();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? TryGetTokenFromLastItem(JsonElement array)
    {
        int len = array.GetArrayLength();
        if (len == 0)
            return null;

        var last = array[len - 1];

        return last.GetPropertyOrNull("continuationItemRenderer")
            ?.GetPropertyOrNull("continuationEndpoint")
            ?.GetPropertyOrNull("continuationCommand")
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull()
            ?? last.FindFirstDescendantProperty("continuationCommand")
                ?.GetPropertyOrNull("token")
                ?.GetStringOrNull();
    }

    #endregion

    #region User Playlists (WEB client)

    /// <summary>
    /// Gets all user playlists via WEB client with pagination.
    /// Uses FEplaylist_aggregation with "qAIC" filter (Music playlists).
    /// Parses lockupViewModel for compact data.
    /// </summary>
    public async IAsyncEnumerable<List<RemotePlaylistInfo>> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = await PostWebAsync("browse", writer =>
        {
            writer.WriteString("browseId", "FEplaylist_aggregation");
            writer.WriteString("params", ConstantParams);
        }, ct);

        var batch = ParseLockupViewModels(root);
        if (batch.Count > 0)
            yield return batch;

        var continuation = ExtractWebContinuationToken(root);
        int page = 0;

        while (!string.IsNullOrEmpty(continuation) && page < 50)
        {
            ct.ThrowIfCancellationRequested();

            var contRoot = await PostWebAsync("browse", writer =>
            {
                writer.WriteString("continuation", continuation);
            }, ct);

            var contBatch = ParseLockupViewModelsFromContinuation(contRoot);
            if (contBatch.Count == 0)
                break;

            yield return contBatch;

            continuation = ExtractWebContinuationToken(contRoot);
            page++;
        }
    }

    private static List<RemotePlaylistInfo> ParseLockupViewModels(JsonElement root)
    {
        var results = new List<RemotePlaylistInfo>(32);
        var lockups = new List<JsonElement>(32);
        root.EnumerateDescendantProperties("lockupViewModel", lockups);

        for (int i = 0; i < lockups.Count; i++)
        {
            var info = ParseSingleLockup(lockups[i]);
            if (info.HasValue)
                results.Add(info.Value);
        }

        return results;
    }

    private static List<RemotePlaylistInfo> ParseLockupViewModelsFromContinuation(JsonElement root)
    {
        var results = new List<RemotePlaylistInfo>(32);

        var commands = root.GetPropertyOrNull("onResponseReceivedCommands");
        if (commands?.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var cmd in commands.Value.EnumerateArray())
        {
            var items = cmd.GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems");
            if (items?.ValueKind != JsonValueKind.Array)
                continue;

            var lockups = new List<JsonElement>(32);
            items.Value.EnumerateDescendantProperties("lockupViewModel", lockups);

            for (int i = 0; i < lockups.Count; i++)
            {
                var info = ParseSingleLockup(lockups[i]);
                if (info.HasValue)
                    results.Add(info.Value);
            }
        }

        return results;
    }

    private static RemotePlaylistInfo? ParseSingleLockup(JsonElement lockup)
    {
        var contentId = lockup.GetPropertyOrNull("contentId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(contentId))
            return null;

        var title = lockup.GetPropertyOrNull("metadata")
            ?.GetPropertyOrNull("lockupMetadataViewModel")
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("content")
            ?.GetStringOrNull() ?? "";

        var badgeText = lockup.GetPropertyOrNull("contentImage")
            ?.GetPropertyOrNull("collectionThumbnailViewModel")
            ?.GetPropertyOrNull("primaryThumbnail")
            ?.GetPropertyOrNull("thumbnailViewModel")
            ?.GetPropertyOrNull("overlays")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("thumbnailOverlayBadgeViewModel")
            ?.GetPropertyOrNull("thumbnailBadges")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("thumbnailBadgeViewModel")
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull();

        int trackCount = ParseTrackCount(badgeText);

        string? thumbUrl = lockup.GetPropertyOrNull("contentImage")
            ?.GetPropertyOrNull("collectionThumbnailViewModel")
            ?.GetPropertyOrNull("primaryThumbnail")
            ?.GetPropertyOrNull("thumbnailViewModel")
            ?.GetPropertyOrNull("image")
            ?.GetPropertyOrNull("sources")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("url")
            ?.GetStringOrNull();

        return new RemotePlaylistInfo(contentId, title, trackCount, thumbUrl);
    }

    /// <summary>
    /// Проверяет, является ли строка относительным форматом указания времени.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRelativeDate(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var span = text.AsSpan();
        return span.Contains("сегодня".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("today".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("вчера".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("yesterday".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("назад".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("ago".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("час".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("hour".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("минут".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("minute".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("день".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("day".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("недел".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("week".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("месяц".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("month".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractWebContinuationToken(JsonElement root)
    {
        var commands = root.GetPropertyOrNull("onResponseReceivedCommands");
        if (commands?.ValueKind == JsonValueKind.Array)
        {
            foreach (var cmd in commands.Value.EnumerateArray())
            {
                var items = cmd.GetPropertyOrNull("appendContinuationItemsAction")
                    ?.GetPropertyOrNull("continuationItems");
                if (items?.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.Value.EnumerateArray())
                {
                    var token = item.GetPropertyOrNull("continuationItemRenderer")
                        ?.GetPropertyOrNull("continuationEndpoint")
                        ?.GetPropertyOrNull("continuationCommand")
                        ?.GetPropertyOrNull("token")
                        ?.GetStringOrNull();

                    if (!string.IsNullOrEmpty(token))
                        return token;
                }
            }
        }

        var found = root.FindFirstDescendantProperty("continuationCommand");
        return found?.GetPropertyOrNull("token")?.GetStringOrNull();
    }

    /// <summary>
    /// Парсит количество треков из локализованных строк вида "4 видео", "5,000 videos".
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ParseTrackCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int result = 0;
        bool foundDigit = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                result = result * 10 + (c - '0');
                foundDigit = true;
            }
            else if (foundDigit && c != ',' && c != '.' && c != ' ' && c != '\u00A0')
            {
                break;
            }
        }

        return result;
    }

    #endregion

    #region Legacy Wrapper

    /// <summary>
    /// Fetches playlist tracks with setVideoId for sync/removal.
    /// </summary>
    /// <remarks>
    /// Wrapper над <see cref="GetFullPlaylistDataAsync"/> для обратной совместимости.
    /// </remarks>
    public async Task<List<RemoteTrackInfo>> GetPlaylistTracksAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        var data = await GetFullPlaylistDataAsync(playlistId, ct);
        return data.Tracks;
    }

    #endregion
}

/// <summary>
/// Compact playlist info from WEB client lockupViewModel.
/// </summary>
public readonly record struct RemotePlaylistInfo(
    string PlaylistId,
    string Title,
    int TrackCount,
    string? ThumbnailUrl);

/// <summary>
/// Трек плейлиста с метаданными и setVideoId для sync/removal операций.
/// IsPlayable=false — трек недоступен в YTM, но присутствует в плейлисте.
/// </summary>
public readonly record struct RemoteTrackInfo(
    string VideoId,
    string SetVideoId,
    string Title,
    string Author,
    int DurationSeconds,
    string ThumbnailUrl,
    bool IsPlayable,
    int Position);