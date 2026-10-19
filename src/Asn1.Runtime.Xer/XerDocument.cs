using System.Text;
using System.Xml;

namespace Asn1.Runtime.Xer;

/// <summary>An element of a parsed XER document: its name and its content (strings and child elements).</summary>
internal sealed class XerElement(string name)
{
    public string Name { get; } = name;

    public List<object> Content { get; } = [];

    /// <summary>Re-serialises the element (used to keep unknown extensions and open types as raw XML).</summary>
    public void AppendXml(StringBuilder sb)
    {
        if (Content.Count == 0)
        {
            sb.Append('<').Append(Name).Append("/>");
            return;
        }

        sb.Append('<').Append(Name).Append('>');
        AppendContent(sb);
        sb.Append("</").Append(Name).Append('>');
    }

    public void AppendContent(StringBuilder sb)
    {
        foreach (var item in Content)
        {
            if (item is XerElement e)
            {
                e.AppendXml(sb);
            }
            else
            {
                sb.Append(((string)item).Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal));
            }
        }
    }
}

/// <summary>
/// Parses a XER document with <see cref="XmlReader"/> (DTDs prohibited, no external resolution) into
/// <see cref="XerElement"/>s, rejecting the XML constructs that X.693 8.1.2 excludes (comments, processing
/// instructions, attributes, document type declarations).
/// </summary>
internal static class XerDocument
{
    public static XerElement Parse(string text, Asn1DecodingOptions options)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false,
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Document,
            MaxCharactersFromEntities = 0,
        };

        XerElement? root = null;
        var stack = new Stack<XerElement>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.XmlDeclaration:
                        break;
                    case XmlNodeType.Element:
                        {
                            if (reader.HasAttributes)
                            {
                                throw Malformed($"attributes are not used in BASIC-XER (element <{reader.Name}>)");
                            }

                            if (stack.Count >= options.MaxDepth)
                            {
                                throw new Asn1DecodeException(Asn1DecodeError.LimitExceeded, $"element nesting exceeds the limit of {options.MaxDepth}");
                            }

                            var element = new XerElement(reader.Name);
                            if (stack.Count == 0)
                            {
                                root = element;
                            }
                            else
                            {
                                stack.Peek().Content.Add(element);
                            }

                            if (!reader.IsEmptyElement)
                            {
                                stack.Push(element);
                            }

                            break;
                        }

                    case XmlNodeType.EndElement:
                        stack.Pop();
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        if (stack.Count > 0)
                        {
                            stack.Peek().Content.Add(reader.Value);
                        }

                        break;
                    case XmlNodeType.Comment or XmlNodeType.ProcessingInstruction:
                        throw Malformed($"XML {reader.NodeType} is not allowed in a XER encoding (X.693 8.1.2)");
                    default:
                        throw Malformed($"XML {reader.NodeType} is not allowed in a XER encoding (X.693 8.1.2)");
                }
            }
        }
        catch (XmlException e)
        {
            throw Malformed($"invalid XML: {e.Message}");
        }

        return root ?? throw Malformed("the document has no element");
    }

    private static Asn1DecodeException Malformed(string message) => new(Asn1DecodeError.Malformed, message);
}
