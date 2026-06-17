using System.Diagnostics.CodeAnalysis;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube.Search;

/// <summary>
/// Metadata associated with a YouTube video returned by a search query.
/// </summary>
public class VideoSearchResult(
    VideoId id,
    string title,
    Author author,
    TimeSpan? duration,
    IReadOnlyList<Thumbnail> thumbnails,
    bool isOfficialArtist,
    bool isShort,
    bool isMusic // <-- Новый аргумент
) : ISearchResult, IVideo
{
    /// <inheritdoc />
    public VideoId Id { get; } = id;

    /// <inheritdoc cref="IVideo.Url" />
    public string Url => IsShort
        ? $"https://www.youtube.com/shorts/{Id}"
        : $"https://www.youtube.com/watch?v={Id}";

    /// <inheritdoc cref="IVideo.Title" />
    public string Title { get; } = title;

    /// <inheritdoc />
    public Author Author { get; } = author;

    public bool IsOfficialArtist { get; } = isOfficialArtist;

    /// <summary>
    /// Indicates if this video is a YouTube Short.
    /// </summary>
    public bool IsShort { get; } = isShort;

    /// <inheritdoc />
    public bool IsMusic { get; } = isMusic;

    /// <inheritdoc />
    public TimeSpan? Duration { get; } = duration;

    /// <inheritdoc />
    public IReadOnlyList<Thumbnail> Thumbnails { get; } = thumbnails;

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public override string ToString() => $"Video ({Title})";
}