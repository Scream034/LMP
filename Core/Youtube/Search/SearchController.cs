using System.Text.Json;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Search;

internal class SearchController(HttpClient http)
{
  // Params из Muzza (YouTube.kt -> SearchFilter)
  private const string FilterSong = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
  private const string FilterVideo = "EgWKAQIQAWoKEAkQChAFEAMQBA%3D%3D";
  private const string FilterAlbum = "EgWKAQIYAWoKEAkQChAFEAMQBA%3D%3D";
  private const string FilterArtist = "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D";
  private const string FilterFeaturedPlaylist = "EgeKAQQoADgBagwQDhAKEAMQBRAJEAQ%3D";
  private const string FilterCommunityPlaylist = "EgeKAQQoAEABagoQAxAEEAoQCRAF";

  private static JsonElement GetMusicContext()
  {
    // Используем WEB_REMIX как в Muzza
    var json = $$"""
        {
            "context": {
                "client": {
                    "clientName": "WEB_REMIX",
                    "clientVersion": {{YoutubeHttpHandler.MusicClientVersion}},
                    "hl": "en",
                    "gl": "US"
                },
                "user": {}
            }
        }
        """;
    return Json.Parse(json);
  }

  private static JsonElement GetWebContext()
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
    // Определяем, используем ли мы YouTube Music (WEB_REMIX) или обычный YouTube (WEB)
    bool isMusicContext = searchFilter is SearchFilter.MusicSong
                                       or SearchFilter.MusicVideo
                                       or SearchFilter.MusicAlbum
                                       or SearchFilter.MusicArtist
                                       or SearchFilter.MusicPlaylist;

    string? searchParams = null;

    if (isMusicContext)
    {
      searchParams = searchFilter switch
      {
        SearchFilter.MusicSong => FilterSong,
        SearchFilter.MusicVideo => FilterVideo,
        SearchFilter.MusicAlbum => FilterAlbum,
        SearchFilter.MusicArtist => FilterArtist,
        SearchFilter.MusicPlaylist => FilterCommunityPlaylist, // Или Featured, по ситуации
        _ => null
      };
    }
    else
    {
      // Стандартные фильтры YouTube
      searchParams = searchFilter switch
      {
        SearchFilter.Video => "EgIQAQ%3D%3D",
        SearchFilter.Playlist => "EgIQAw%3D%3D",
        SearchFilter.Channel => "EgIQAg%3D%3D",
        _ => null
      };
    }

    // Если есть токен продолжения, параметры не нужны (они вшиты в токен)
    if (continuationToken != null) searchParams = null;

    var url = isMusicContext
        ? "https://music.youtube.com/youtubei/v1/search"
        : "https://www.youtube.com/youtubei/v1/search";

    using var request = new HttpRequestMessage(HttpMethod.Post, url);

    using var ms = new MemoryStream();
    using (var writer = new Utf8JsonWriter(ms))
    {
      writer.WriteStartObject();
      writer.WriteString("query", searchQuery);

      if (continuationToken != null)
        writer.WriteString("continuation", continuationToken);

      if (searchParams != null)
        writer.WriteString("params", searchParams);

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