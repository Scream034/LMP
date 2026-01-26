using System.Net;
using YoutubeExplode.Channels;
using YoutubeExplode.Music;
using YoutubeExplode.Playlists;
using YoutubeExplode.Search;
using YoutubeExplode.Utils;
using YoutubeExplode.Videos;

namespace YoutubeExplode;

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
    /// Используется, если нужно создать клиент с нуля внутри YoutubeExplode.
    /// </summary>
    public YoutubeClient(HttpClient http, CookieContainer cookieContainer)
    {
        // Создаем HttpClient, обернутый в наш YoutubeHttpHandler
        _youtubeHttp = new HttpClient(new YoutubeHttpHandler(http, cookieContainer), true);

        Videos = new VideoClient(_youtubeHttp);
        Playlists = new PlaylistClient(_youtubeHttp);
        Channels = new ChannelClient(_youtubeHttp);
        Search = new SearchClient(_youtubeHttp);
        Music = new MusicClient(_youtubeHttp);
    }

    /// <summary>
    /// Легаси-конструктор для совместимости (List -> CookieContainer).
    /// </summary>
    public YoutubeClient(HttpClient http, IReadOnlyList<Cookie> initialCookies)
    {
        var container = new CookieContainer();
        // Добавляем куки для нужных доменов
        var youtubeUri = new Uri("https://youtube.com");
        var musicUri = new Uri("https://music.youtube.com");

        foreach (var cookie in initialCookies)
        {
            try 
            {
                container.Add(youtubeUri, new Cookie(cookie.Name, cookie.Value));
                container.Add(musicUri, new Cookie(cookie.Name, cookie.Value));
            }
            catch { /* Игнорируем некорректные куки */ }
        }

        _youtubeHttp = new HttpClient(new YoutubeHttpHandler(http, container), true);

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
}