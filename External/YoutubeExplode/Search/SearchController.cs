using YoutubeExplode.Bridge;
using YoutubeExplode.Utils;

namespace YoutubeExplode.Search;

internal class SearchController(HttpClient http)
{
    public async ValueTask<SearchResponse> GetSearchResponseAsync(
        string searchQuery,
        SearchFilter searchFilter,
        string? continuationToken,
        CancellationToken cancellationToken = default
    )
    {
        // Для Youtube Music используем специального клиента WEB_REMIX
        var isMusicContext = searchFilter == SearchFilter.Music;

        // Параметры поиска зависят от контекста
        string? searchParams = null;

        if (isMusicContext)
        {
            // Параметры для WEB_REMIX (YT Music)
            // Обычно для поиска треков (Songs) используется: "Eg-KAQwIABAAGAAgACgAMABqChAAGAAgACgAMAA%3D"
            // Но для общего поиска в Music params часто пустой или специфичный. 
            // Используем фильтр "Songs" для WEB_REMIX, если это Music.
            // Params для "Songs" в YT Music: "EgWKAQIIAWoKEAMQBBAJEAoQBQ%3D%3D"
            searchParams = "EgWKAQIIAWoKEAMQBBAJEAoQBQ%3D%3D"; 
        }
        else
        {
            // Параметры для обычного WEB клиента
            searchParams = searchFilter switch
            {
                SearchFilter.Video => "EgIQAQ%3D%3D",
                SearchFilter.Playlist => "EgIQAw%3D%3D",
                SearchFilter.Channel => "EgIQAg%3D%3D",
                // Если Music запрашивается через старый контекст (fallback), оставляем старый хэш,
                // но в данной реализации мы пойдем через ветку isMusicContext.
                _ => null
            };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://music.youtube.com/youtubei/v1/search" // Используем домен music.youtube.com для семантической точности, хотя www тоже работает с WEB_REMIX
        );

        // Формируем payload в зависимости от клиента
        string payload;
        
        if (isMusicContext)
        {
             payload = $$"""
            {
              "query": {{Json.Serialize(searchQuery)}},
              "params": {{Json.Serialize(searchParams)}},
              "continuation": {{Json.Serialize(continuationToken)}},
              "context": {
                "client": {
                  "clientName": "WEB_REMIX",
                  "clientVersion": "1.20240101.01.00",
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """;
        }
        else
        {
            // Стандартный YouTube WEB клиент
            request.RequestUri = new Uri("https://www.youtube.com/youtubei/v1/search");
            payload = $$"""
            {
              "query": {{Json.Serialize(searchQuery)}},
              "params": {{Json.Serialize(searchParams)}},
              "continuation": {{Json.Serialize(continuationToken)}},
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20250331.09.00",
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """;
        }

        request.Content = new StringContent(payload);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return SearchResponse.Parse(responseContent);
    }
}