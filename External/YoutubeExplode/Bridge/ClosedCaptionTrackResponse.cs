using System.Xml.Linq;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal partial class ClosedCaptionTrackResponse(XElement content)
{
    public IReadOnlyList<CaptionData> Captions =>
        content.Descendants("p").Select(x => new CaptionData(x)).ToArray();
}

internal partial class ClosedCaptionTrackResponse
{
    public class CaptionData(XElement content)
    {
        public string? Text => (string?)content;

        public TimeSpan? Offset =>
            ((double?)content.Attribute("t"))?.Pipe(TimeSpan.FromMilliseconds);

        public TimeSpan? Duration =>
            ((double?)content.Attribute("d"))?.Pipe(TimeSpan.FromMilliseconds);

        public IReadOnlyList<PartData> Parts =>
            content.Elements("s").Select(x => new PartData(x)).ToArray();
    }
}

internal partial class ClosedCaptionTrackResponse
{
    public class PartData(XElement content)
    {
        public string? Text => (string?)content;

        public TimeSpan? Offset =>
            ((double?)content.Attribute("t"))?.Pipe(TimeSpan.FromMilliseconds)
            ?? ((double?)content.Attribute("ac"))?.Pipe(TimeSpan.FromMilliseconds)
            ?? TimeSpan.Zero;
    }
}

internal partial class ClosedCaptionTrackResponse
{
    public static ClosedCaptionTrackResponse Parse(string raw) => new(Xml.Parse(raw));
}
