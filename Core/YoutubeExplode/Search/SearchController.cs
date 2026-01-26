using System.Text;
using System.Text.Json;
using YoutubeExplode.Bridge;
using YoutubeExplode.Utils;

namespace YoutubeExplode.Search;

internal class SearchController(HttpClient http)
{
  private JsonElement GetMusicContext()
  {
    var json = $$"""
        {
            "context": {
                "client": {
                    "clientName": "WEB_REMIX",
                    "clientVersion": {{YoutubeHttpHandler.MusicClientVersion}},
                    "hl": "ru",
                    "gl": "RU"
                },
                "user": {}
            }
        }
        """;
    return Json.Parse(json);
  }

  private JsonElement GetWebContext()
  {
    var json = """
        {
            "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20250331.09.00",
                  "hl": "en",
                  "gl": "US"
                }
            }
        }
        """;
    return Json.Parse(json);
  }

  public async ValueTask<SearchResponse> GetSearchResponseAsync(
      string searchQuery,
      SearchFilter searchFilter,
      string? continuationToken,
      CancellationToken cancellationToken = default
  )
  {
    var isMusicContext = searchFilter == SearchFilter.Music;
    string? searchParams = null;

    if (isMusicContext)
    {
      searchParams = "EgWKAQIIAWoKEAMQBBAJEAoQBQ%3D%3D";
    }
    else
    {
      searchParams = searchFilter switch
      {
        SearchFilter.Video => "EgIQAQ%3D%3D",
        SearchFilter.Playlist => "EgIQAw%3D%3D",
        SearchFilter.Channel => "EgIQAg%3D%3D",
        _ => null
      };
    }

    using var request = new HttpRequestMessage(
           HttpMethod.Post,
           isMusicContext ? "https://music.youtube.com/youtubei/v1/search" : "https://www.youtube.com/youtubei/v1/search"
       );

    // Формируем тело запроса через Utf8JsonWriter
    using var ms = new MemoryStream();
    using (var writer = new Utf8JsonWriter(ms))
    {
      writer.WriteStartObject();
      writer.WriteString("query", searchQuery);
      if (continuationToken != null) writer.WriteString("continuation", continuationToken);

      var context = isMusicContext ? GetMusicContext() : GetWebContext();
      foreach (var prop in context.EnumerateObject()) prop.WriteTo(writer);

      writer.WriteEndObject();
    }

    request.Content = new ByteArrayContent(ms.ToArray());
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

    using var response = await http.SendAsync(request, cancellationToken);
    response.EnsureSuccessStatusCode();

    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
    return SearchResponse.Parse(responseContent);
  }
}