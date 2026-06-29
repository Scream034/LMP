using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class DashManifest(XElement content)
{
    public IReadOnlyList<IStreamData> Streams
    {
        get
        {
            var result = new List<IStreamData>();

            foreach (var x in content.Descendants("Representation"))
            {
                var idValue = x.Attribute("id")?.Value;
                if (string.IsNullOrEmpty(idValue) || !IsAllDigits(idValue.AsSpan()))
                    continue;

                bool hasSqInit = false;
                foreach (var init in x.Descendants("Initialization"))
                {
                    if (init.Attribute("sourceURL")?.Value.Contains("sq/") == true)
                    {
                        hasSqInit = true;
                        break;
                    }
                }
                if (hasSqInit) continue;

                if (string.IsNullOrWhiteSpace(x.Attribute("codecs")?.Value))
                    continue;

                result.Add(new StreamData(x));
            }

            return result;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
            if (!char.IsAsciiDigit(span[i])) return false;
        return true;
    }
}

internal partial class DashManifest
{
    public partial class StreamData(XElement content) : IStreamData
    {
        public int? Itag => (int?)content.Attribute("id");

        public string? Url => (string?)content.Element("BaseURL");

        public string? Signature => null;

        public string? SignatureParameter => null;

        public long? ContentLength =>
                    (long?)content.Attribute("contentLength")
                    ?? Url?.Pipe(static s => UrlEx.TryGetQueryParameterValue(s, "clen"))
                        ?.NullIfWhiteSpace()
                        ?.Pipe(static s =>
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

        public string? MimeType => content.Attribute("mimeType")?.Value;

        public string? AudioCodec => IsAudioOnly ? (string?)content.Attribute("codecs") : null;

        public string? AudioLanguageCode => null;

        public string? AudioLanguageName => null;

        public bool? IsAudioLanguageDefault => null;

        public float LoudnessDb => float.NaN;

        public string? VideoCodec => IsAudioOnly ? null : (string?)content.Attribute("codecs");

        public string? VideoQualityLabel => null;

        public int? VideoWidth => (int?)content.Attribute("width");

        public int? VideoHeight => (int?)content.Attribute("height");

        public int? VideoFramerate => (int?)content.Attribute("frameRate");

        [GeneratedRegex(@"mime[/=]\w*%2F([\w\d]*)")]
        private static partial Regex MyRegex1();
    }
}

internal partial class DashManifest
{
    public static DashManifest Parse(string raw) => new(Xml.Parse(raw));
}