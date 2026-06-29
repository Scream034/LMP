using System.Xml.Linq;

namespace LMP.Core.Helpers.Extensions;

internal static class XElementExtensions
{
    extension(XElement element)
    {
        public XElement StripNamespaces()
        {
            var result = new XElement(element);

            foreach (var descendantElement in result.DescendantsAndSelf())
            {
                descendantElement.Name = XNamespace.None.GetName(
                    descendantElement.Name.LocalName);

                var filtered = new List<XAttribute>();
                foreach (var a in descendantElement.Attributes())
                {
                    if (a.IsNamespaceDeclaration) continue;
                    if (a.Name.Namespace == XNamespace.Xml
                        || a.Name.Namespace == XNamespace.Xmlns) continue;

                    filtered.Add(new XAttribute(
                        XNamespace.None.GetName(a.Name.LocalName),
                        a.Value));
                }

                descendantElement.ReplaceAttributes(filtered);
            }

            return result;
        }
    }
}