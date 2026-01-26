using System.Xml.Linq;

namespace LMP.Core.Youtube.Utils.Extensions;

internal static class XElementExtensions
{
    extension(XElement element)
    {
        public XElement StripNamespaces()
        {
            // Adapted from http://stackoverflow.com/a/1147012

            var result = new XElement(element);

            foreach (var descendantElement in result.DescendantsAndSelf())
            {
                descendantElement.Name = XNamespace.None.GetName(descendantElement.Name.LocalName);

                descendantElement.ReplaceAttributes(
                    descendantElement
                        .Attributes()
                        .Where(static a => !a.IsNamespaceDeclaration)
                        .Where(static a =>
                            a.Name.Namespace != XNamespace.Xml
                            && a.Name.Namespace != XNamespace.Xmlns
                        )
                        .Select(static a => new XAttribute(
                            XNamespace.None.GetName(a.Name.LocalName),
                            a.Value
                        ))
                );
            }

            return result;
        }
    }
}
