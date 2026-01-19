namespace YoutubeExplode.Utils;

internal static class Http
{
    private static readonly HttpClient HttpClientLazy = new();

    public static HttpClient Client => HttpClientLazy;
}
