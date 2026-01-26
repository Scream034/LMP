using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class DashManifest(XElement content)
{
    public IReadOnlyList<IStreamData> Streams =>
        content
            .Descendants("Representation")
            // Skip non-media representations (like "rawcc")
            // https://github.com/Tyrrrz/YoutubeExplode/issues/546
            .Where(x => x.Attribute("id")?.Value.All(char.IsDigit) == true)
            // Skip segmented streams
            // https://github.com/Tyrrrz/YoutubeExplode/issues/159
            .Where(x =>
                x.Descendants("Initialization")
                    .FirstOrDefault()
                    ?.Attribute("sourceURL")
                    ?.Value.Contains("sq/") != true
            )
            // Skip streams without codecs
            .Where(x => !string.IsNullOrWhiteSpace(x.Attribute("codecs")?.Value))
            .Select(x => new StreamData(x))
            .ToArray();
}

internal partial class DashManifest
{
    public partial class StreamData(XElement content) : IStreamData
    {
        public int? Itag => (int?)content.Attribute("id");

        public string? Url => (string?)content.Element("BaseURL");

        // DASH streams don't have signatures
        public string? Signature => null;

        // DASH streams don't have signatures
        public string? SignatureParameter => null;

        public long? ContentLength =>
            (long?)content.Attribute("contentLength")
            ?? Url?.Pipe(s => MyRegex().Match(s).Groups[1].Value)
                .NullIfWhiteSpace()
                ?.Pipe(s =>
                    long.TryParse(s, CultureInfo.InvariantCulture, out var result)
                        ? result
                        : (long?)null
                );

        public long? Bitrate => (long?)content.Attribute("bandwidth");

        public string? Container =>
            Url
                ?.Pipe(static s => MyRegex1().Match(s).Groups[1].Value)
                .Pipe(WebUtility.UrlDecode);

        private bool IsAudioOnly => content.Element("AudioChannelConfiguration") is not null;

        public string? AudioCodec => IsAudioOnly ? (string?)content.Attribute("codecs") : null;

        public string? AudioLanguageCode => null;

        public string? AudioLanguageName => null;

        public bool? IsAudioLanguageDefault => null;

        public string? VideoCodec => IsAudioOnly ? null : (string?)content.Attribute("codecs");

        public string? VideoQualityLabel => null;

        public int? VideoWidth => (int?)content.Attribute("width");

        public int? VideoHeight => (int?)content.Attribute("height");

        public int? VideoFramerate => (int?)content.Attribute("frameRate");

        [GeneratedRegex(@"[/\?]clen[/=](\d+)")]
        private static partial Regex MyRegex();
        [GeneratedRegex(@"mime[/=]\w*%2F([\w\d]*)")]
        private static partial Regex MyRegex1();
    }
}

internal partial class DashManifest
{
    public static DashManifest Parse(string raw) => new(Xml.Parse(raw));
}
