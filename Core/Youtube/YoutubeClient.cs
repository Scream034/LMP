using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Channels;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube;

public class YoutubeClient : IDisposable
{
    private readonly HttpClient _youtubeHttp;
    private readonly bool _ownsHttpClient;

    public YoutubeClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null,
        bool ownsHttpClient = false)
    {
        _youtubeHttp = http;
        _ownsHttpClient = ownsHttpClient;

        Videos = new VideoClient(_youtubeHttp, nTokenDecryptor, sigCipherDecryptor, isAuthenticatedCheck);
        Playlists = new PlaylistClient(_youtubeHttp);
        Channels = new ChannelClient(_youtubeHttp);
        Search = new SearchClient(_youtubeHttp);
        Music = new MusicClient(_youtubeHttp);
        Mutations = new PlaylistMutationController(_youtubeHttp);
        Sync = new PlaylistSyncController(_youtubeHttp);
    }

    public VideoClient Videos { get; }
    public PlaylistClient Playlists { get; }
    public ChannelClient Channels { get; }
    public SearchClient Search { get; }
    public MusicClient Music { get; }

    /// <summary>
    /// Playlist mutations via WEB client (create, add, remove, rename, delete).
    /// </summary>
    internal PlaylistMutationController Mutations { get; }

    /// <summary>
    /// Playlist sync operations (user playlist listing, track fetching with setVideoId).
    /// </summary>
    internal PlaylistSyncController Sync { get; }

    public void Dispose()
    {
        if (_ownsHttpClient) _youtubeHttp.Dispose();
        GC.SuppressFinalize(this);
    }
}