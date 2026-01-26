using System.Net;
using System.Text;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Utils;

internal static class UrlEx
{
    // Оптимизировано: используем индексы вместо string.Split, чтобы избежать аллокации массива.
    // Мы не используем Span здесь, так как yield return не позволяет хранить ref-структуры.
    private static IEnumerable<KeyValuePair<string, string>> EnumerateQueryParameters(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            yield break;

        var queryIndex = url.IndexOf('?');
        var startIndex = queryIndex >= 0 ? queryIndex + 1 : 0;

        if (queryIndex < 0 && url.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            // Это полный URL без параметров запроса
            yield break;
        }

        var currentIndex = startIndex;
        while (currentIndex < url.Length)
        {
            var ampIndex = url.IndexOf('&', currentIndex);
            var segmentEnd = ampIndex < 0 ? url.Length : ampIndex;
            var segmentLength = segmentEnd - currentIndex;

            if (segmentLength > 0)
            {
                var eqIndex = url.IndexOf('=', currentIndex, segmentLength);
                if (eqIndex >= 0)
                {
                    var key = WebUtility.UrlDecode(url[currentIndex..eqIndex]);
                    var value = WebUtility.UrlDecode(url.Substring(eqIndex + 1, segmentEnd - eqIndex - 1));

                    if (!string.IsNullOrWhiteSpace(key))
                        yield return new KeyValuePair<string, string>(key, value);
                }
                else
                {
                    var key = WebUtility.UrlDecode(url.Substring(currentIndex, segmentLength));
                    if (!string.IsNullOrWhiteSpace(key))
                        yield return new KeyValuePair<string, string>(key, "");
                }
            }

            currentIndex = segmentEnd + 1;
        }
    }

    public static IReadOnlyDictionary<string, string> GetQueryParameters(string url)
    {
        var dic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in EnumerateQueryParameters(url))
        {
            dic[kvp.Key] = kvp.Value;
        }
        return dic;
    }

    public static string? TryGetQueryParameterValue(string url, string key)
    {
        foreach (var parameter in EnumerateQueryParameters(url))
        {
            if (string.Equals(parameter.Key, key, StringComparison.Ordinal))
                return parameter.Value;
        }
        return null;
    }

    public static bool ContainsQueryParameter(string url, string key) =>
        TryGetQueryParameterValue(url, key) is not null;

    public static string SetQueryParameter(string url, string key, string value)
    {
        var queryIndex = url.IndexOf('?');
        var baseUrl = queryIndex < 0 ? url : url[..queryIndex];

        var sb = new StringBuilder(baseUrl);
        sb.Append('?');

        var hasParams = false;
        foreach (var p in EnumerateQueryParameters(url))
        {
            if (string.Equals(p.Key, key, StringComparison.Ordinal))
                continue;

            if (hasParams) sb.Append('&');
            sb.Append(Uri.EscapeDataString(p.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(p.Value));
            hasParams = true;
        }

        if (hasParams) sb.Append('&');
        sb.Append(Uri.EscapeDataString(key));
        sb.Append('=');
        sb.Append(Uri.EscapeDataString(value));

        return sb.ToString();
    }
}