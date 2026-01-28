using System.Text.Json;
using LMP.Core.Services;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Search;

internal class SearchController(HttpClient http)
{
  private const string FilterSong = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
  private const string FilterVideo = "EgWKAQIQAWoKEAkQChAFEAMQBA%3D%3D";
  private const string FilterAlbum = "EgWKAQIYAWoKEAkQChAFEAMQBA%3D%3D";
  private const string FilterArtist = "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D";
  private const string FilterCommunityPlaylist = "EgeKAQQoAEABagoQAxAEEAoQCRAF";

  private static JsonElement GetMusicContext()
  {
    var json = $$"""
        {
            "context": {
                "client": {
                    "clientName": "WEB_REMIX",
                    "clientVersion": "{{YoutubeHttpHandler.MusicClientVersion}}",
                    "hl": "{{YoutubeHttpHandler.GetHl()}}",
                    "gl": "{{YoutubeHttpHandler.GetGl()}}"
                },
                "user": {}
            }
        }
        """;
    return Json.Parse(json);
  }

  private static JsonElement GetWebContext()
  {
    var json = $$"""
        {
            "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "{{YoutubeHttpHandler.WebClientVersion}}",
                  "hl": "{{YoutubeHttpHandler.GetHl()}}",
                  "gl": "{{YoutubeHttpHandler.GetGl()}}"
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
    bool isMusicContext = searchFilter is SearchFilter.Music
                                       or SearchFilter.MusicSong
                                       or SearchFilter.MusicVideo
                                       or SearchFilter.MusicAlbum
                                       or SearchFilter.MusicArtist
                                       or SearchFilter.MusicPlaylist;

    string? searchParams = null;

    if (isMusicContext)
    {
      searchParams = searchFilter switch
      {
        SearchFilter.Music => FilterSong,
        SearchFilter.MusicSong => FilterSong,
        SearchFilter.MusicVideo => FilterVideo,
        SearchFilter.MusicAlbum => FilterAlbum,
        SearchFilter.MusicArtist => FilterArtist,
        SearchFilter.MusicPlaylist => FilterCommunityPlaylist,
        _ => null
      };
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