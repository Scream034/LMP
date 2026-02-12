using System.Net;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Channels;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube;

/// <summary>
/// Client for interacting with YouTube.
/// Configures SocketsHttpHandler with connection pooling.
/// </summary>
public class YoutubeClient : IDisposable
{
    private readonly HttpClient _youtubeHttp;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Основной конструктор с готовым HttpClient.
    /// </summary>
    public YoutubeClient(HttpClient http, bool ownsHttpClient = false)
    {
        _youtubeHttp = http;
        _ownsHttpClient = ownsHttpClient;

        Videos = new VideoClient(_youtubeHttp);
        Playlists = new PlaylistClient(_youtubeHttp);
        Channels = new ChannelClient(_youtubeHttp);
        Search = new SearchClient(_youtubeHttp);
        Music = new MusicClient(_youtubeHttp);
    }

    /// <summary>
    /// Конструктор по умолчанию с оптимальными настройками connection pooling.
    /// </summary>
    public YoutubeClient()
        : this(CreateOptimizedClient(), ownsHttpClient: true) { }

    /// <summary>
    /// Создает HttpClient с оптимизированным SocketsHttpHandler.
    /// </summary>
    private static HttpClient CreateOptimizedClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Connection pooling
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 20,

            // Keep-alive
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),

            // Performance
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,

            // Timeouts
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public VideoClient Videos { get; }
    public PlaylistClient Playlists { get; }
    public ChannelClient Channels { get; }
    public SearchClient Search { get; }
    public MusicClient Music { get; }

    internal ValueTask<PlayerResponse> GetPlayerResponseAsync(VideoId videoId, CancellationToken ct = default)
    {
        return Videos.GetPlayerResponseAsync(videoId, ct);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _youtubeHttp.Dispose();
        
        GC.SuppressFinalize(this);
    }
}