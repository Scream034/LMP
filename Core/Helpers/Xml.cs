using System.Xml.Linq;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Helpers;

internal static class Xml
{
    public static XElement Parse(string source) =>
        XElement.Parse(source, LoadOptions.PreserveWhitespace).StripNamespaces();
}
