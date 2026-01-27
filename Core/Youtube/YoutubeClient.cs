using System.Net;
using System.Text;
using LMP.Core.Youtube.Channels;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube;

/// <summary>
/// Client for interacting with YouTube.
/// </summary>
public class YoutubeClient : IDisposable
{
    private readonly HttpClient _youtubeHttp;

    /// <summary>
    /// Основной конструктор.
    /// Принимает готовый HttpClient. Если вы используете YoutubeProvider,
    /// сюда передается клиент, уже настроенный через YoutubeHttpHandler.
    /// </summary>
    public YoutubeClient(HttpClient http)
    {
        _youtubeHttp = http;

        Videos = new VideoClient(_youtubeHttp);
        Playlists = new PlaylistClient(_youtubeHttp);
        Channels = new ChannelClient(_youtubeHttp);
        Search = new SearchClient(_youtubeHttp);
        Music = new MusicClient(_youtubeHttp);
    }

    /// <summary>
    /// Конструктор для создания клиента с настройкой CookieContainer.
    /// Адаптирует контейнер в строку для YoutubeHttpHandler.
    /// </summary>
    public YoutubeClient(HttpClient http, CookieContainer cookieContainer)
    {
        // Конвертируем Container в строку для нашего "Hardcore Mode"
        string rawCookies = ConvertContainerToString(cookieContainer);

        // Создаем HttpClient, обернутый в наш YoutubeHttpHandler
        _youtubeHttp = new HttpClient(new YoutubeHttpHandler(http, rawCookies), true);

        Videos = new VideoClient(_youtubeHttp);
        Playlists = new PlaylistClient(_youtubeHttp);
        Channels = new ChannelClient(_youtubeHttp);
        Search = new SearchClient(_youtubeHttp);
        Music = new MusicClient(_youtubeHttp);
    }

    /// <summary>
    /// Легаси-конструктор для совместимости (List -> String).
    /// </summary>
    public YoutubeClient(HttpClient http, IReadOnlyList<Cookie> initialCookies)
    {
        // Конвертируем список в строку
        var sb = new StringBuilder();
        foreach (var cookie in initialCookies)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"{cookie.Name}={cookie.Value}");
        }
        string rawCookies = sb.ToString();

        _youtubeHttp = new HttpClient(new YoutubeHttpHandler(http, rawCookies), true);

        Videos = new VideoClient(_youtubeHttp);
        Playlists = new PlaylistClient(_youtubeHttp);
        Channels = new ChannelClient(_youtubeHttp);
        Search = new SearchClient(_youtubeHttp);
        Music = new MusicClient(_youtubeHttp);
    }

    /// <summary>
    /// Initializes an instance of <see cref="YoutubeClient" />.
    /// </summary>
    public YoutubeClient()
        : this(Http.Client) { }

    /// <summary>
    /// Operations related to YouTube videos.
    /// </summary>
    public VideoClient Videos { get; }

    /// <summary>
    /// Operations related to YouTube playlists.
    /// </summary>
    public PlaylistClient Playlists { get; }

    /// <summary>
    /// Operations related to YouTube channels.
    /// </summary>
    public ChannelClient Channels { get; }

    /// <summary>
    /// Operations related to YouTube search.
    /// </summary>
    public SearchClient Search { get; }

    public MusicClient Music { get; } 

    /// <inheritdoc />
    public void Dispose() => _youtubeHttp.Dispose();

    // Вспомогательный метод для извлечения куки из контейнера в строку
    private static string ConvertContainerToString(CookieContainer container)
    {
        // Собираем куки с основных доменов, чтобы сформировать полную строку
        var uris = new[] 
        { 
            new Uri("https://youtube.com"), 
            new Uri("https://music.youtube.com"), 
            new Uri("https://google.com") 
        };

        var uniqueCookies = new Dictionary<string, string>();

        foreach (var uri in uris)
        {
            var collection = container.GetCookies(uri);
            foreach (Cookie cookie in collection)
            {
                if (!uniqueCookies.ContainsKey(cookie.Name))
                {
                    uniqueCookies[cookie.Name] = cookie.Value;
                }
            }
        }

        var sb = new StringBuilder();
        foreach (var kvp in uniqueCookies)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"{kvp.Key}={kvp.Value}");
        }

        return sb.ToString();
    }
}