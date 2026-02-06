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