using System.Text.RegularExpressions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class VideoWatchPage(string rawContent)
{
    public bool IsAvailable => !rawContent.Contains("og:url") || rawContent.Contains("video_id");

    public DateTimeOffset? UploadDate =>
        MyRegex().Match(rawContent)
            .Groups[1].Value.NullIfWhiteSpace()
            ?.Pipe(s => DateTimeOffset.TryParse(s, out var d) ? d : (DateTimeOffset?)null);

    // Лайки и дизлайки убираем — YouTube API их часто не отдает в HTML без JS, 
    // а для плеера это лишний мусор в памяти.

    // Парсинг лайков без AngleSharp
    // YouTube часто меняет формат, ищем "likeCount":"12345" или в тултипе "12,345 likes"
    public long? LikeCount
    {
        get
        {
            // Вариант 1: JSON внутри initialData
            var matchJson = LikeRegex1().Match(rawContent);
            if (matchJson.Success && long.TryParse(matchJson.Groups[1].Value, out var l1))
                return l1;

            // Вариант 2: Старый формат текста
            var matchText = LikeRegex2().Match(rawContent);
            if (matchText.Success)
            {
                var clean = matchText.Groups[1].Value.Replace(",", "").Replace(".", "");
                if (long.TryParse(clean, out var l2)) return l2;
            }

            return null; // Не нашли (скрыты или новый лейаут)
        }
    }

    // То же самое для дизлайков (обычно 0 или скрыты)
    public long? DislikeCount => 0;

    public PlayerResponse? PlayerResponse
    {
        get
        {
            // 1. Пробуем найти ytInitialPlayerResponse
            var json = InitialYTRegex().Match(rawContent)
                .Groups[1].Value;

            if (!string.IsNullOrWhiteSpace(json))
            {
                return PlayerResponse.Parse(json);
            }

            // 2. Пробуем найти ytplayer.config (старый формат, иногда встречается)
            var configJson = Regex.Match(rawContent, @"ytplayer\.config\s*=\s*(\{.*?\});", RegexOptions.Singleline)
                .Groups[1].Value;

            if (!string.IsNullOrWhiteSpace(configJson))
            {
                var config = Json.TryParse(configJson);
                var argsResponse = config?.GetPropertyOrNull("args")?.GetPropertyOrNull("player_response")?.GetStringOrNull();
                if (!string.IsNullOrWhiteSpace(argsResponse))
                {
                    return PlayerResponse.Parse(argsResponse);
                }
            }

            return null;
        }
    }

    public static VideoWatchPage? TryParse(string raw)
    {
        // Простая проверка на наличие признаков страницы видео
        if (!raw.Contains("ytInitialPlayerResponse") && !raw.Contains("ytplayer.config"))
            return null;

        return new VideoWatchPage(raw);
    }

    [GeneratedRegex(@"itemprop=""datePublished"" content=""(.*?)(?:"")")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"""likeCount""\s*:\s*""(\d+)""")]
    private static partial Regex LikeRegex1();
    [GeneratedRegex(@"([\d,\.]+)\s+likes")]
    private static partial Regex LikeRegex2();
    [GeneratedRegex(@"var\s+ytInitialPlayerResponse\s*=\s*(\{.*?\});", RegexOptions.Singleline)]
    private static partial Regex InitialYTRegex();
}