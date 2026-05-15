using System.Diagnostics.CodeAnalysis;
using LMP.Core.Models;
using LMP.Core.Youtube.Playlists;

namespace LMP.Core.Youtube.Search;

/// <summary>
/// Metadata associated with a YouTube playlist returned by a search query.
/// </summary>
public sealed class PlaylistSearchResult(
    PlaylistId id,
    string title,
    Author? author,
    IReadOnlyList<Thumbnail> thumbnails
) : ISearchResult, IPlaylist
{
    /// <inheritdoc />
    public PlaylistId Id { get; } = id;

    /// <inheritdoc cref="IPlaylist.Url" />
    public string Url => $"https://www.youtube.com/playlist?list={Id}";

    /// <inheritdoc cref="IPlaylist.Title" />
    public string Title { get; } = title;

    /// <inheritdoc />
    public Author? Author { get; } = author;

    /// <inheritdoc />
    public IReadOnlyList<Thumbnail> Thumbnails { get; } = thumbnails;

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public override string ToString() => $"Playlist ({Title})";
}
