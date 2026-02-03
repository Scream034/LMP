using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Search;

internal class SearchController(HttpClient http)
{
    // Proto-параметры для YouTube Music API (WEB_REMIX)
    private const string FilterMusicSong = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string FilterMusicVideo = "EgWKAQIQAWoKEAkQChAFEAMQBA%3D%3D";
    private const string FilterMusicAlbum = "EgWKAQIYAWoKEAkQChAFEAMQBA%3D%3D";
    private const string FilterMusicArtist = "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D";
    private const string FilterMusicPlaylist = "EgeKAQQoAEABagoQAxAEEAoQCRAF";

    // Proto-параметры для стандартного YouTube API (WEB)
    private const string FilterVideoWeb = "EgIQAQ%3D%3D";
    private const string FilterPlaylistWeb = "EgIQAw%3D%3D";
    private const string FilterChannelWeb = "EgIQAg%3D%3D";

    public async ValueTask<SearchResponse> GetSearchResponseAsync(
        string searchQuery,
        SearchFilter searchFilter,
        string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        bool isMusicContext = IsMusicFilter(searchFilter);

        // Параметры фильтра (при continuation не передаём)
        string? searchParams = continuationToken == null 
            ? GetSearchParams(searchFilter, isMusicContext) 
            : null;

        var url = isMusicContext
            ? "https://music.youtube.com/youtubei/v1/search"
            : "https://www.youtube.com/youtubei/v1/search";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        // Формируем JSON напрямую (быстрее чем через MemoryStream)
        var context = isMusicContext ? GetMusicContextJson() : GetWebContextJson();
        
        string jsonBody;
        if (continuationToken != null)
        {
            jsonBody = $$"""
                {
                    "query": {{Json.Serialize(searchQuery)}},
                    "continuation": {{Json.Serialize(continuationToken)}},
                    {{context}}
                }
                """;
        }
        else if (searchParams != null)
        {
            jsonBody = $$"""
                {
                    "query": {{Json.Serialize(searchQuery)}},
                    "params": {{Json.Serialize(searchParams)}},
                    {{context}}
                }
                """;
        }
        else
        {
            jsonBody = $$"""
                {
                    "query": {{Json.Serialize(searchQuery)}},
                    {{context}}
                }
                """;
        }

        request.Content = new StringContent(jsonBody);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return SearchResponse.Parse(responseContent);
    }

    private static bool IsMusicFilter(SearchFilter filter) =>
        filter is SearchFilter.Music 
            or SearchFilter.MusicSong 
            or SearchFilter.MusicVideo 
            or SearchFilter.MusicAlbum 
            or SearchFilter.MusicArtist 
            or SearchFilter.MusicPlaylist;

    private static string? GetSearchParams(SearchFilter filter, bool isMusicContext)
    {
        if (isMusicContext)
        {
            return filter switch
            {
                SearchFilter.Music => FilterMusicSong,
                SearchFilter.MusicSong => FilterMusicSong,
                SearchFilter.MusicVideo => FilterMusicVideo,
                SearchFilter.MusicAlbum => FilterMusicAlbum,
                SearchFilter.MusicArtist => FilterMusicArtist,
                SearchFilter.MusicPlaylist => FilterMusicPlaylist,
                _ => null
            };
        }

        return filter switch
        {
            SearchFilter.Video => FilterVideoWeb,
            SearchFilter.Playlist => FilterPlaylistWeb,
            SearchFilter.Channel => FilterChannelWeb,
            _ => null
        };
    }

    private static string GetMusicContextJson() =>
        $$"""
        "context": {
            "client": {
                "clientName": "WEB_REMIX",
                "clientVersion": "{{YoutubeHttpHandler.MusicClientVersion}}",
                "hl": "{{YoutubeHttpHandler.GetHl()}}",
                "gl": "{{YoutubeHttpHandler.GetGl()}}"
            },
            "user": {}
        }
        """;

    private static string GetWebContextJson() =>
        $$"""
        "context": {
            "client": {
                "clientName": "WEB",
                "clientVersion": "{{YoutubeHttpHandler.WebClientVersion}}",
                "hl": "{{YoutubeHttpHandler.GetHl()}}",
                "gl": "{{YoutubeHttpHandler.GetGl()}}"
            }
        }
        """;
}