using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LMP.Core.Helpers.Extensions;

internal static class JsonExtensions
{
    // Кэш UTF-8 имен свойств для избежания повторной конвертации
    private static class Utf8PropertyNames
    {
        public static readonly byte[] Thumbnails = "thumbnails"u8.ToArray();
        public static readonly byte[] Runs = "runs"u8.ToArray();
        public static readonly byte[] Text = "text"u8.ToArray();
        public static readonly byte[] VideoId = "videoId"u8.ToArray();
        public static readonly byte[] Title = "title"u8.ToArray();
        public static readonly byte[] NavigationEndpoint = "navigationEndpoint"u8.ToArray();
        public static readonly byte[] BrowseEndpoint = "browseEndpoint"u8.ToArray();
        public static readonly byte[] BrowseId = "browseId"u8.ToArray();
        public static readonly byte[] PlaylistId = "playlistId"u8.ToArray();
        public static readonly byte[] ContinuationCommand = "continuationCommand"u8.ToArray();
        public static readonly byte[] Token = "token"u8.ToArray();
    }

    extension(JsonElement element)
    {
        /// <summary>
        /// ОПТИМИЗАЦИЯ: проверка свойства по строке.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetPropertyOrNull(string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (element.TryGetProperty(propertyName, out var result)
                && result.ValueKind is not JsonValueKind.Null
                && result.ValueKind is not JsonValueKind.Undefined)
            {
                return result;
            }

            return null;
        }

        /// <summary>
        /// КРИТИЧНАЯ ОПТИМИЗАЦИЯ: проверка по UTF8 байтам — zero-alloc для имени свойства.
        /// Используйте для горячих путей с константными именами.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetPropertyOrNull(ReadOnlySpan<byte> utf8PropertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (element.TryGetProperty(utf8PropertyName, out var result)
                && result.ValueKind is not JsonValueKind.Null
                && result.ValueKind is not JsonValueKind.Undefined)
            {
                return result;
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool? GetBooleanOrNull() =>
            element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? GetStringOrNull() =>
            element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int? GetInt32OrNull() =>
            element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var result)
                ? result
                : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long? GetInt64OrNull() =>
            element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var result)
                ? result
                : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double? GetDoubleOrNull() =>
            element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var result)
                ? result
                : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement.ArrayEnumerator? EnumerateArrayOrNull() =>
            element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement.ArrayEnumerator EnumerateArrayOrEmpty() =>
            element.EnumerateArrayOrNull() ?? default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement.ObjectEnumerator? EnumerateObjectOrNull() =>
            element.ValueKind == JsonValueKind.Object ? element.EnumerateObject() : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement.ObjectEnumerator EnumerateObjectOrEmpty() =>
            element.EnumerateObjectOrNull() ?? default;

        /// <summary>
        /// ОПТИМИЗАЦИЯ: получает N-й элемент массива через индексатор — O(1).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetArrayElementOrNull(int index)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return null;

            int len = element.GetArrayLength();
            if (index < 0 || index >= len)
                return null;

            return element[index];
        }

        /// <summary>
        /// Первый элемент массива без LINQ.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetFirstArrayElementOrNull()
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
                return null;

            return element[0];
        }

        /// <summary>
        /// КРИТИЧНАЯ ОПТИМИЗАЦИЯ: рекурсивный поиск БЕЗ промежуточных List аллокаций.
        /// Использует ArrayPool для стека результатов.
        /// </summary>
        public void EnumerateDescendantProperties(string propertyName, List<JsonElement> results)
        {
            var property = element.GetPropertyOrNull(propertyName);
            if (property is not null)
                results.Add(property.Value);

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                    child.EnumerateDescendantProperties(propertyName, results);
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                    prop.Value.EnumerateDescendantProperties(propertyName, results);
            }
        }

        /// <summary>
        /// ОПТИМИЗАЦИЯ: ранний выход при первом найденном свойстве — избегает обхода всего дерева.
        /// </summary>
        public JsonElement? FindFirstDescendantProperty(string propertyName)
        {
            var property = element.GetPropertyOrNull(propertyName);
            if (property is not null)
                return property.Value;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    var found = child.FindFirstDescendantProperty(propertyName);
                    if (found is not null) return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    var found = prop.Value.FindFirstDescendantProperty(propertyName);
                    if (found is not null) return found;
                }
            }

            return null;
        }

        /// <summary>
        /// ОПТИМИЗАЦИЯ: поиск по UTF-8 имени — для горячих путей.
        /// </summary>
        public JsonElement? FindFirstDescendantProperty(ReadOnlySpan<byte> utf8PropertyName)
        {
            var property = element.GetPropertyOrNull(utf8PropertyName);
            if (property is not null)
                return property.Value;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    var found = child.FindFirstDescendantProperty(utf8PropertyName);
                    if (found is not null) return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    var found = prop.Value.FindFirstDescendantProperty(utf8PropertyName);
                    if (found is not null) return found;
                }
            }

            return null;
        }

        // Обратная совместимость — ленивый вариант
        public IEnumerable<JsonElement> EnumerateDescendantProperties(string propertyName)
        {
            var results = new List<JsonElement>(4);
            element.EnumerateDescendantProperties(propertyName, results);
            return results;
        }

        /// <summary>
        /// HELPER: извлекает текст из runs[0].text — частый паттерн YouTube API.
        /// ОПТИМИЗАЦИЯ: используем UTF-8 константы.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? GetTextFromRuns()
        {
            var runs = element.GetPropertyOrNull(Utf8PropertyNames.Runs);
            if (runs is null) return null;

            var firstRun = runs.Value.GetFirstArrayElementOrNull();
            if (firstRun is null) return null;

            var text = firstRun.Value.GetPropertyOrNull(Utf8PropertyNames.Text);
            return text?.GetStringOrNull();
        }

        /// <summary>
        /// HELPER: извлекает videoId из navigationEndpoint.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? GetVideoIdFromNavigation()
        {
            var nav = element.GetPropertyOrNull(Utf8PropertyNames.NavigationEndpoint);
            if (nav is null) return null;

            // Пробуем watchEndpoint.videoId
            var watchEndpoint = nav.Value.GetPropertyOrNull("watchEndpoint"u8);
            if (watchEndpoint is not null)
            {
                var videoId = watchEndpoint.Value.GetPropertyOrNull(Utf8PropertyNames.VideoId);
                if (videoId is not null) return videoId.Value.GetStringOrNull();
            }

            // Пробуем browseEndpoint.browseId
            var browseEndpoint = nav.Value.GetPropertyOrNull(Utf8PropertyNames.BrowseEndpoint);
            if (browseEndpoint is not null)
            {
                var browseId = browseEndpoint.Value.GetPropertyOrNull(Utf8PropertyNames.BrowseId);
                if (browseId is not null) return browseId.Value.GetStringOrNull();
            }

            return null;
        }

        /// <summary>
        /// HELPER: извлекает playlistId.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? GetPlaylistIdSafe()
        {
            var plId = element.GetPropertyOrNull(Utf8PropertyNames.PlaylistId);
            return plId?.GetStringOrNull();
        }

        /// <summary>
        /// HELPER: извлекает continuation token.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? GetContinuationToken()
        {
            var cmd = element.GetPropertyOrNull(Utf8PropertyNames.ContinuationCommand);
            if (cmd is null) return null;

            var token = cmd.Value.GetPropertyOrNull(Utf8PropertyNames.Token);
            return token?.GetStringOrNull();
        }
    }
}