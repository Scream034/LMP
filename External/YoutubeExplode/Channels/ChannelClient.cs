using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using YoutubeExplode.Bridge;
using YoutubeExplode.Common;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Playlists;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Channels;

/// <summary>
/// Operations related to YouTube channels.
/// </summary>
public partial class ChannelClient(HttpClient http)
{
    private readonly ChannelController _controller = new(http);

    private Channel Get(ChannelPage channelPage)
    {
        var channelId =
            channelPage.Id
            ?? throw new YoutubeExplodeException("Failed to extract the channel ID.");

        var title =
            channelPage.Title
            ?? throw new YoutubeExplodeException("Failed to extract the channel title.");

        var logoUrl =
            channelPage.LogoUrl
            ?? throw new YoutubeExplodeException("Failed to extract the channel logo URL.");

        var logoSize =
            MyRegex().Matches(logoUrl)
                .ToArray()
                .LastOrDefault()
                ?.Groups[1]
                .Value.NullIfWhiteSpace()
                ?.Pipe(s =>
                    int.TryParse(s, CultureInfo.InvariantCulture, out var result)
                        ? result
                        : (int?)null
                )
            ?? 100;

        var thumbnails = new[] { new Thumbnail(logoUrl, new Resolution(logoSize, logoSize)) };

        return new Channel(channelId, title, thumbnails);
    }

    /// <summary>
    /// Gets the metadata associated with the specified channel.
    /// </summary>
    public async ValueTask<Channel> GetAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    )
    {
        // Special case for the "Movies & TV" channel, which has a custom page
        if (channelId == "UCuVPpxrm2VAgpH3Ktln4HXg")
        {
            return new Channel(
                "UCuVPpxrm2VAgpH3Ktln4HXg",
                "Movies & TV",
                [
                    new Thumbnail(
                        "https://www.gstatic.com/youtube/img/tvfilm/clapperboard_profile.png",
                        new Resolution(1024, 1024)
                    ),
                ]
            );
        }

        return Get(await _controller.GetChannelPageAsync(channelId, cancellationToken));
    }

    /// <summary>
    /// Gets the metadata associated with the channel of the specified user.
    /// </summary>
    public async ValueTask<Channel> GetByUserAsync(
        UserName userName,
        CancellationToken cancellationToken = default
    ) => Get(await _controller.GetChannelPageAsync(userName, cancellationToken));

    /// <summary>
    /// Gets the metadata associated with the channel identified by the specified slug or legacy custom URL.
    /// </summary>
    public async ValueTask<Channel> GetBySlugAsync(
        ChannelSlug channelSlug,
        CancellationToken cancellationToken = default
    ) => Get(await _controller.GetChannelPageAsync(channelSlug, cancellationToken));

    /// <summary>
    /// Gets the metadata associated with the channel identified by the specified handle or custom URL.
    /// </summary>
    public async ValueTask<Channel> GetByHandleAsync(
        ChannelHandle channelHandle,
        CancellationToken cancellationToken = default
    ) => Get(await _controller.GetChannelPageAsync(channelHandle, cancellationToken));

    /// <summary>
    /// Enumerates videos uploaded by the specified channel.
    /// </summary>
    // TODO: should return <IVideo> sequence instead (breaking change)
    public IAsyncEnumerable<PlaylistVideo> GetUploadsAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    )
    {
        // Replace 'UC' in the channel ID with 'UU'
        var playlistId = "UU" + channelId.Value[2..];
        return new PlaylistClient(http).GetVideosAsync(playlistId, cancellationToken);
    }

    /// <summary>
    /// Enumerates playlists found on the channel's "Playlists" tab.
    /// </summary>
    public IAsyncEnumerable<Playlist> GetPlaylistsAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    ) => GetPlaylistBatchesAsync(channelId, cancellationToken).FlattenAsync();

    /// <summary>
    /// Enumerates batches of playlists found on the channel's "Playlists" tab.
    /// </summary>
    public async IAsyncEnumerable<Batch<Playlist>> GetPlaylistBatchesAsync(
        ChannelId channelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Получаем информацию о канале один раз, чтобы заполнить поле Author у плейлистов
        // (так как в списке плейлистов часто нет инфы об авторе, т.к. мы и так на его странице)
        var channelPage = await _controller.GetChannelPageAsync(channelId, cancellationToken);
        // Небольшой хак: вытаскиваем имя канала из страницы
        var channelTitle = channelPage.Title ?? "Unknown Channel";
        var author = new Author(channelId, channelTitle);

        var encounteredIds = new HashSet<string>(StringComparer.Ordinal);
        var continuationToken = default(string?);

        do
        {
            var response = await _controller.GetChannelPlaylistsPageAsync(
                channelId,
                continuationToken,
                cancellationToken
            );

            var batch = new List<Playlist>();

            foreach (var item in response.Playlists)
            {
                if (!encounteredIds.Add(item.Id.Value))
                    continue;

                // Создаем новый объект Playlist, обогащенный правильным Автором
                var enrichedPlaylist = new Playlist(
                    item.Id,
                    item.Title,
                    author, // Вставляем автора явно
                    item.Description,
                    item.Count,
                    item.Thumbnails
                );

                batch.Add(enrichedPlaylist);
            }

            // Если ничего не нашли в первом запросе, но токена нет - выходим
            if (batch.Count == 0 && continuationToken == null && response.ContinuationToken == null)
                yield break;

            if (batch.Count > 0)
                yield return Batch.Create(batch);

            continuationToken = response.ContinuationToken;

        } while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    [GeneratedRegex(@"\bs(\d+)\b")]
    private static partial Regex MyRegex();
}
