using YoutubeExplode.Common;

namespace YoutubeExplode.Videos;

/// <summary>
/// Облегченная модель видео с данными для музыкального плеера.
/// </summary>
public class Video(
    VideoId id,
    string title,
    Author author,
    TimeSpan? duration,
    IReadOnlyList<Thumbnail> thumbnails,
    long viewCount,
    long likeCount
) : IVideo
{
    public VideoId Id { get; } = id;
    public string Url => $"https://www.youtube.com/watch?v={Id}";
    public string Title { get; } = title;
    public Author Author { get; } = author;
    public TimeSpan? Duration { get; } = duration;
    public IReadOnlyList<Thumbnail> Thumbnails { get; } = thumbnails;

    public long ViewCount { get; } = viewCount;
    public long LikeCount { get; } = likeCount;

    public override string ToString() => $"Video ({Title})";
}