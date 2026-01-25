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
        // Determine the params parameter based on the filter
        var searchParams = searchFilter switch
        {
            SearchFilter.Video => "EgIQAQ%3D%3D",
            SearchFilter.Playlist => "EgIQAw%3D%3D",
            SearchFilter.Channel => "EgIQAg%3D%3D",
            // This specific param forces the "Songs" category in search results
            SearchFilter.Music => "Eg-KAQwIABAAGAAgACgAMABqChAAGAAgACgAMAA%3D", 
            _ => null
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/search"
        );

        request.Content = new StringContent(
            // lang=json
            $$"""
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
            """
        );

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Отладка: Сохранить в файл
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync($"search_response_{searchFilter}_{DateTime.Now:yyyyMMddHHmmss}.json", responseContent, cancellationToken);
        return SearchResponse.Parse(responseContent);
        

        // var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        // return SearchResponse.Parse(responseContent);
    }
}