using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class SearchResponse
{
    // FrozenSet для O(1) проверки имён рендереров
    private static readonly FrozenSet<string> ItemRendererNames = new[]
    {
        "musicResponsiveListItemRenderer",
        "videoRenderer",
        "playlistRenderer",
        "channelRenderer",
        "shortsLockupViewModel",
        "reelItemRenderer",
        "continuationItemRenderer",
        "lockupViewModel"
    }.ToFrozenSet(StringComparer.Ordinal);

    // FrozenSet для контейнеров (не нужно проверять каждый по отдельности)
    private static readonly FrozenSet<string> ContainerNames = new[]
    {
        "contents", "items", "primaryContents", "secondaryContents",
        "twoColumnSearchResultsRenderer", "sectionListRenderer",
        "itemSectionRenderer", "musicShelfRenderer", "richGridRenderer",
        "shelfRenderer", "tabbedSearchResultsRenderer", "tabRenderer",
        "tabs", "content", "continuations", "onResponseReceivedCommands",
        "appendContinuationItemsAction", "continuationItems"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IReadOnlyList<VideoData> Videos { get; }
    public IReadOnlyList<PlaylistData> Playlists { get; }
    public IReadOnlyList<ChannelData> Channels { get; }
    public string? ContinuationToken { get; }

    private SearchResponse(JsonElement content)
    {
        // Используем начальную ёмкость для уменьшения реаллокаций
        var videos = new List<VideoData>(32);
        var playlists = new List<PlaylistData>(8);
        var channels = new List<ChannelData>(4);
        string? foundToken = null;

        // Оптимизированный сбор элементов
        foreach (var item in CollectItemsFast(content))
        {
            // Один проход по свойствам объекта вместо множественных TryGetProperty
            var result = ClassifyAndExtract(item, ref foundToken);

            switch (result.Type)
            {
                case ItemType.Video when result.Video is not null:
                    videos.Add(result.Video);
                    break;
                case ItemType.Playlist when result.Playlist is not null:
                    playlists.Add(result.Playlist);
                    break;
                case ItemType.Channel when result.Channel is not null:
                    channels.Add(result.Channel);
                    break;
            }
        }

        Videos = videos;
        Playlists = playlists;
        Channels = channels;
        ContinuationToken = foundToken ?? ExtractContinuationTokenFast(content);
    }

    /// <summary>
    /// Классифицирует элемент за ОДИН проход по его свойствам
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ClassificationResult ClassifyAndExtract(JsonElement item, ref string? token)
    {
        foreach (var prop in item.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "continuationItemRenderer":
                    token = prop.Value.GetPropertyOrNull("continuationEndpoint")
                        ?.GetPropertyOrNull("continuationCommand")
                        ?.GetPropertyOrNull("token")?.GetStringOrNull();
                    return default;

                case "musicResponsiveListItemRenderer":
                    return ProcessMusicItem(prop.Value);

                case "videoRenderer":
                    var videoData = new VideoData(prop.Value, isYtm: false);
                    return string.IsNullOrEmpty(videoData.Id)
                        ? default
                        : new(ItemType.Video, videoData, null, null);

                case "shortsLockupViewModel":
                case "reelItemRenderer":
                    var shortData = new VideoData(prop.Value, isYtm: false);
                    return string.IsNullOrEmpty(shortData.Id)
                        ? default
                        : new(ItemType.Video, shortData, null, null);

                case "lockupViewModel":
                    var contentId = prop.Value.GetPropertyOrNull("contentId")?.GetStringOrNull();
                    if (!string.IsNullOrEmpty(contentId) && IsPlaylistId(contentId))
                        return new(ItemType.Playlist, null, new PlaylistData(prop.Value), null);
                    return default;

                case "playlistRenderer":
                    return new(ItemType.Playlist, null, new PlaylistData(prop.Value), null);

                case "channelRenderer":
                    return new(ItemType.Channel, null, null, new ChannelData(prop.Value));
            }
        }
        return default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPlaylistId(string id) =>
        id.StartsWith("PL", StringComparison.Ordinal) ||
        id.StartsWith("OL", StringComparison.Ordinal) ||
        id.StartsWith("RD", StringComparison.Ordinal);

    private static ClassificationResult ProcessMusicItem(JsonElement musicItem)
    {
        var data = new VideoData(musicItem, isYtm: true);

        if (data.IsPlaylistContext)
            return new(ItemType.Playlist, null, new PlaylistData(musicItem, isYtm: true), null);

        if (data.IsArtistContext)
            return new(ItemType.Channel, null, null, new ChannelData(musicItem, isYtm: true));

        return string.IsNullOrEmpty(data.Id)
            ? default
            : new(ItemType.Video, data, null, null);
    }

    /// <summary>
    /// Оптимизированный сборщик с использованием Stack (меньше аллокаций чем Queue для DFS)
    /// </summary>
    private static IEnumerable<JsonElement> CollectItemsFast(JsonElement root)
    {
        // Stack выделяется на стеке для небольших размеров
        var stack = new Stack<JsonElement>(64);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                    stack.Push(item);
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object)
                continue;

            // Проверяем является ли это целевым элементом за один проход
            bool isItem = false;
            bool hasLockupWithId = false;

            foreach (var prop in current.EnumerateObject())
            {
                if (ItemRendererNames.Contains(prop.Name))
                {
                    if (prop.Name == "lockupViewModel")
                    {
                        // Дополнительная проверка для lockupViewModel
                        hasLockupWithId = prop.Value.GetPropertyOrNull("contentId") != null;
                        if (hasLockupWithId) { isItem = true; break; }
                    }
                    else
                    {
                        isItem = true;
                        break;
                    }
                }
            }

            if (isItem)
            {
                yield return current;
                continue; // Не углубляемся в item renderers
            }

            // Добавляем только известные контейнеры
            foreach (var prop in current.EnumerateObject())
            {
                if (ContainerNames.Contains(prop.Name))
                    stack.Push(prop.Value);
            }
        }
    }

    /// <summary>
    /// Быстрый поиск токена с ранним выходом
    /// </summary>
    private static string? ExtractContinuationTokenFast(JsonElement root)
    {
        var stack = new Stack<JsonElement>(32);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in current.EnumerateObject())
                {
                    if (prop.Name == "continuationCommand")
                    {
                        var token = prop.Value.GetPropertyOrNull("token")?.GetStringOrNull();
                        if (token != null) return token;
                    }
                    else if (prop.Name == "nextContinuationData")
                    {
                        var token = prop.Value.GetPropertyOrNull("continuation")?.GetStringOrNull();
                        if (token != null) return token;
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        stack.Push(prop.Value);
                    }
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                    stack.Push(item);
            }
        }
        return null;
    }

    public static SearchResponse Parse(string raw) => new(Json.Parse(raw));

    // Вспомогательные типы
    private enum ItemType { None, Video, Playlist, Channel }

    private readonly record struct ClassificationResult(
        ItemType Type,
        VideoData? Video,
        PlaylistData? Playlist,
        ChannelData? Channel);

    // VideoData с кэшированием свойств при первом доступе
    internal sealed class VideoData(JsonElement content, bool isYtm)
    {
        // Кэшированные значения
        private string? _id;
        private bool _idComputed;
        private string? _title;
        private bool _titleComputed;
        private string? _author;
        private bool _authorComputed;
        private string? _channelId;
        private bool _channelIdComputed;
        private TimeSpan? _duration;
        private bool _durationComputed;
        private IReadOnlyList<ThumbnailData>? _thumbnails;
        private bool? _isPlaylistContext;
        private bool? _isArtistContext;

        public bool IsMusicItem => isYtm;

        public string? Id
        {
            get
            {
                if (_idComputed) return _id;
                _id = ComputeId();
                _idComputed = true;
                return _id;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string? ComputeId()
        {
            if (isYtm)
            {
                // Проверяем наиболее вероятные пути первыми
                var vid = content.GetPropertyOrNull("playlistItemData")
                    ?.GetPropertyOrNull("videoId")?.GetStringOrNull();
                if (!string.IsNullOrEmpty(vid)) return vid;

                var nav = content.GetPropertyOrNull("playNavigationEndpoint")
                    ?? content.GetPropertyOrNull("navigationEndpoint");
                return nav?.GetPropertyOrNull("watchEndpoint")
                    ?.GetPropertyOrNull("videoId")?.GetStringOrNull();
            }

            var id = content.GetPropertyOrNull("videoId")?.GetStringOrNull();
            if (!string.IsNullOrEmpty(id)) return id;

            return content.GetPropertyOrNull("onTap")
                ?.GetPropertyOrNull("innertubeCommand")
                ?.GetPropertyOrNull("reelWatchEndpoint")
                ?.GetPropertyOrNull("videoId")?.GetStringOrNull();
        }

        public string? Title
        {
            get
            {
                if (_titleComputed) return _title;
                _title = ComputeTitle();
                _titleComputed = true;
                return _title;
            }
        }

        private string? ComputeTitle()
        {
            if (isYtm) return GetRunText(content, 0);

            var titleProp = content.GetPropertyOrNull("title");
            if (titleProp.HasValue)
            {
                var runs = titleProp.Value.GetPropertyOrNull("runs")?.EnumerateArrayOrNull();
                if (runs != null)
                {
                    foreach (var run in runs)
                        return run.GetPropertyOrNull("text")?.GetStringOrNull();
                }

                return titleProp.Value.GetPropertyOrNull("simpleText")?.GetStringOrNull();
            }

            return content.GetPropertyOrNull("overlayMetadata")
                ?.GetPropertyOrNull("primaryText")
                ?.GetPropertyOrNull("content")?.GetStringOrNull();
        }

        public string? Author
        {
            get
            {
                if (_authorComputed) return _author;
                _author = ComputeAuthor();
                _authorComputed = true;
                return _author;
            }
        }

        private string? ComputeAuthor()
        {
            if (isYtm)
            {
                var runs = GetRuns(content, 1);
                if (runs == null) return null;

                foreach (var run in runs)
                {
                    var pageType = run.GetPropertyOrNull("navigationEndpoint")
                        ?.GetPropertyOrNull("browseEndpoint")
                        ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                        ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                        ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

                    if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
                        return run.GetPropertyOrNull("text")?.GetStringOrNull();
                }

                foreach (var run in runs)
                    return run.GetPropertyOrNull("text")?.GetStringOrNull();

                return null;
            }

            var ownerRuns = content.GetPropertyOrNull("ownerText")
                ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull();
            if (ownerRuns != null)
            {
                foreach (var run in ownerRuns)
                    return run.GetPropertyOrNull("text")?.GetStringOrNull();
            }

            var bylineRuns = content.GetPropertyOrNull("shortBylineText")
                ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull();
            if (bylineRuns != null)
            {
                foreach (var run in bylineRuns)
                    return run.GetPropertyOrNull("text")?.GetStringOrNull();
            }

            return null;
        }

        public string? ChannelId
        {
            get
            {
                if (_channelIdComputed) return _channelId;
                _channelId = ComputeChannelId();
                _channelIdComputed = true;
                return _channelId;
            }
        }

        private string? ComputeChannelId()
        {
            if (isYtm)
            {
                var runs = GetRuns(content, 1);
                if (runs != null)
                {
                    foreach (var run in runs)
                    {
                        var id = run.GetPropertyOrNull("navigationEndpoint")
                            ?.GetPropertyOrNull("browseEndpoint")
                            ?.GetPropertyOrNull("browseId")?.GetStringOrNull();

                        if (id != null && id.StartsWith("UC", StringComparison.Ordinal))
                            return id;
                    }
                }
                return null;
            }

            // Ищем browseId только в известных местах, а не через EnumerateDescendantProperties
            var browsePath = content.GetPropertyOrNull("ownerText")
                ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull();
            if (browsePath != null)
            {
                foreach (var run in browsePath)
                {
                    var id = run.GetPropertyOrNull("navigationEndpoint")
                        ?.GetPropertyOrNull("browseEndpoint")
                        ?.GetPropertyOrNull("browseId")?.GetStringOrNull();
                    if (id != null) return id;
                }
            }

            return content.GetPropertyOrNull("channelThumbnailSupportedRenderers")
                ?.GetPropertyOrNull("channelThumbnailWithLinkRenderer")
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")?.GetStringOrNull();
        }

        public bool IsOfficialArtist
        {
            get
            {
                if (isYtm) return true;

                // Быстрая проверка без полного обхода
                var badges = content.GetPropertyOrNull("ownerBadges")?.EnumerateArrayOrNull();
                if (badges != null)
                {
                    foreach (var badge in badges)
                    {
                        var iconType = badge.GetPropertyOrNull("metadataBadgeRenderer")
                            ?.GetPropertyOrNull("icon")
                            ?.GetPropertyOrNull("iconType")?.GetStringOrNull();
                        if (iconType == "AUDIO_BADGE") return true;
                    }
                }
                return false;
            }
        }

        public bool IsShort => !isYtm &&
            (content.TryGetProperty("shortsLockupViewModel", out _) ||
             content.TryGetProperty("reelItemRenderer", out _));

        public TimeSpan? Duration
        {
            get
            {
                if (_durationComputed) return _duration;
                _duration = ComputeDuration();
                _durationComputed = true;
                return _duration;
            }
        }

        private TimeSpan? ComputeDuration()
        {
            if (isYtm)
            {
                var runs = GetRuns(content, 1);
                if (runs != null)
                {
                    foreach (var run in runs)
                    {
                        var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                        if (text != null && text.Contains(':') && !text.Contains('•'))
                        {
                            if (TryParseDuration(text, out var ts)) return ts;
                        }
                    }
                }
                return null;
            }

            var textDuration = content.GetPropertyOrNull("lengthText")
                ?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

            return textDuration != null && TryParseDuration(textDuration, out var d) ? d : null;
        }

        public IReadOnlyList<ThumbnailData> Thumbnails
        {
            get
            {
                if (_thumbnails != null) return _thumbnails;
                _thumbnails = ComputeThumbnails();
                return _thumbnails;
            }
        }

        private IReadOnlyList<ThumbnailData> ComputeThumbnails()
        {
            var thumbs = content.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("musicThumbnailRenderer")
                ?.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull();

            if (thumbs == null)
            {
                thumbs = content.GetPropertyOrNull("thumbnail")
                    ?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull();
            }

            if (thumbs == null)
            {
                thumbs = content.GetPropertyOrNull("thumbnailViewModel")
                    ?.GetPropertyOrNull("image")
                    ?.GetPropertyOrNull("sources")?.EnumerateArrayOrNull();
            }

            if (thumbs == null) return Array.Empty<ThumbnailData>();

            var list = new List<ThumbnailData>(4);
            foreach (var t in thumbs)
                list.Add(new ThumbnailData(t));
            return list;
        }

        public bool IsPlaylistContext
        {
            get
            {
                if (_isPlaylistContext.HasValue) return _isPlaylistContext.Value;
                _isPlaylistContext = ComputeIsPlaylistContext();
                return _isPlaylistContext.Value;
            }
        }

        private bool ComputeIsPlaylistContext()
        {
            if (!isYtm) return false;

            var pageType = content.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

            return pageType == "MUSIC_PAGE_TYPE_ALBUM" || (Id == null && Title != null);
        }

        public bool IsArtistContext
        {
            get
            {
                if (_isArtistContext.HasValue) return _isArtistContext.Value;
                _isArtistContext = ComputeIsArtistContext();
                return _isArtistContext.Value;
            }
        }

        private bool ComputeIsArtistContext()
        {
            if (!isYtm) return false;

            var pageType = content.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

            return pageType == "MUSIC_PAGE_TYPE_ARTIST";
        }

        private static IEnumerable<JsonElement>? GetRuns(JsonElement item, int columnIndex)
        {
            var cols = item.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull();
            if (cols == null) return null;

            int idx = 0;
            foreach (var col in cols)
            {
                if (idx == columnIndex)
                {
                    return col.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")
                        ?.GetPropertyOrNull("text")
                        ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull();
                }
                idx++;
            }
            return null;
        }

        public static string? GetRunText(JsonElement item, int columnIndex)
        {
            var runs = GetRuns(item, columnIndex);
            if (runs == null) return null;

            StringBuilder? sb = null;
            string? single = null;
            int count = 0;

            foreach (var run in runs)
            {
                var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                if (text == null) continue;

                if (count == 0)
                {
                    single = text;
                }
                else
                {
                    sb ??= new StringBuilder(single);
                    sb.Append(text);
                }
                count++;
            }

            return sb?.ToString() ?? single;
        }

        // Кэшированные форматы для парсинга
        private static readonly string[] DurationFormats =
            { @"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss" };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParseDuration(string text, out TimeSpan timeSpan) =>
            TimeSpan.TryParseExact(text, DurationFormats, CultureInfo.InvariantCulture, out timeSpan);
    }

    internal sealed class PlaylistData
    {
        private readonly JsonElement _content;
        private readonly bool _isYtm;

        public PlaylistData(JsonElement content, bool isYtm = false)
        {
            _content = content;
            _isYtm = isYtm;
        }

        public string? Id =>
            _content.GetPropertyOrNull("playlistId")?.GetStringOrNull() ??
            _content.GetPropertyOrNull("contentId")?.GetStringOrNull() ??
            (_isYtm ? _content.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")?.GetStringOrNull() : null);

        public string? Title =>
            _content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull() ??
            _content.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()
                ?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull() ??
            _content.GetPropertyOrNull("metadata")?.GetPropertyOrNull("lockupMetadataViewModel")
                ?.GetPropertyOrNull("title")?.GetPropertyOrNull("content")?.GetStringOrNull() ??
            (_isYtm ? VideoData.GetRunText(_content, 0) : null);

        public string? Author =>
            _content.GetPropertyOrNull("shortBylineText")?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()?.FirstOrDefault()
                .GetPropertyOrNull("text")?.GetStringOrNull() ??
            (_isYtm ? VideoData.GetRunText(_content, 1) : null);

        public IReadOnlyList<ThumbnailData> Thumbnails
        {
            get
            {
                var thumbs = _content.GetPropertyOrNull("thumbnail")
                    ?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull();

                if (thumbs == null)
                {
                    thumbs = _content.GetPropertyOrNull("thumbnail")
                        ?.GetPropertyOrNull("musicThumbnailRenderer")
                        ?.GetPropertyOrNull("thumbnail")
                        ?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull();
                }

                if (thumbs == null) return Array.Empty<ThumbnailData>();

                var list = new List<ThumbnailData>(4);
                foreach (var t in thumbs)
                    list.Add(new ThumbnailData(t));
                return list;
            }
        }
    }

    internal sealed class ChannelData
    {
        private readonly JsonElement _content;
        private readonly bool _isYtm;

        public ChannelData(JsonElement content, bool isYtm = false)
        {
            _content = content;
            _isYtm = isYtm;
        }

        public string? Id =>
            _content.GetPropertyOrNull("channelId")?.GetStringOrNull() ??
            (_isYtm ? _content.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")?.GetStringOrNull() : null);

        public string? Title =>
            _content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull() ??
            (_isYtm ? VideoData.GetRunText(_content, 0) : null);

        public IReadOnlyList<ThumbnailData> Thumbnails
        {
            get
            {
                var thumbs = _content.GetPropertyOrNull("thumbnail")
                    ?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull();

                if (thumbs == null)
                {
                    thumbs = _content.GetPropertyOrNull("thumbnail")
                        ?.GetPropertyOrNull("musicThumbnailRenderer")
                        ?.GetPropertyOrNull("thumbnail")
                        ?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull();
                }

                if (thumbs == null) return Array.Empty<ThumbnailData>();

                var list = new List<ThumbnailData>(4);
                foreach (var t in thumbs)
                    list.Add(new ThumbnailData(t));
                return list;
            }
        }
    }
}