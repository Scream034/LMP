using System.Xml.Linq;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Helpers;

namespace LMP.Core.Youtube.Bridge;

internal partial class ClosedCaptionTrackResponse(XElement content)
{
    public IEnumerable<CaptionData> Captions =>
        content.Descendants("p").Select(x => new CaptionData(x));
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