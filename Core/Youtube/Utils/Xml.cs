using System.Xml.Linq;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Utils;

internal static class Xml
{
    public static XElement Parse(string source) =>
        XElement.Parse(source, LoadOptions.PreserveWhitespace).StripNamespaces();
}
