using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Parses XML documentation comments and formats them as human-readable plain text
///     optimized for LLM consumption.
/// </summary>
internal static class XmlDocFormatter
{
    private const int MaxInheritDocDepth = 10;

    /// <summary>
    ///     Formats a symbol's XML documentation as multi-line plain text.
    ///     Resolves <c>&lt;inheritdoc/&gt;</c> by walking the override/implementation chain.
    ///     Returns null if no documentation is available.
    /// </summary>
    public static string? Format(ISymbol symbol)
    {
        XElement? member = ResolveMember(symbol);
        return member is not null ? FormatMember(member) : null;
    }

    /// <summary>
    ///     Extracts just the summary line from a symbol's documentation.
    ///     Resolves <c>&lt;inheritdoc/&gt;</c> by walking the override/implementation chain.
    ///     Returns null if no summary is available.
    /// </summary>
    public static string? FormatSummaryOnly(ISymbol symbol)
    {
        XElement? member = ResolveMember(symbol);
        return member is not null ? ExtractSummary(member) : null;
    }

    /// <summary>
    ///     Formats raw XML documentation as multi-line plain text.
    ///     Returns null if the input is null, empty, or contains no meaningful content.
    /// </summary>
    public static string? Format(string? xmlDocComment)
    {
        XElement? member = ParseMember(xmlDocComment);
        return member is not null ? FormatMember(member) : null;
    }

    /// <summary>
    ///     Extracts just the summary text from raw XML documentation.
    ///     Returns null if no summary is available.
    /// </summary>
    public static string? FormatSummaryOnly(string? xmlDocComment)
    {
        XElement? member = ParseMember(xmlDocComment);
        return member is not null ? ExtractSummary(member) : null;
    }

    /// <summary>
    ///     Appends formatted documentation lines to a <see cref="StringBuilder" />,
    ///     each prefixed with the specified indentation.
    /// </summary>
    internal static void AppendDocumentation(StringBuilder sb, ISymbol symbol, string indent)
    {
        string? docs = Format(symbol);
        if (docs is null)
        {
            return;
        }

        sb.AppendLine($"{indent}Documentation:");
        foreach (string line in docs.Split('\n'))
        {
            sb.Append(indent);
            sb.Append("  ");
            sb.AppendLine(line.TrimEnd('\r'));
        }
    }

    private static string? FormatMember(XElement member)
    {
        var sb = new StringBuilder();

        AppendElement(sb, member, "summary", null);
        AppendNamedElements(sb, member, "typeparam", "name", "Type parameters");
        AppendNamedElements(sb, member, "param", "name", "Parameters");
        AppendElement(sb, member, "returns", "Returns");
        AppendElement(sb, member, "value", "Value");
        AppendExceptions(sb, member);
        AppendElement(sb, member, "remarks", "Remarks");
        AppendElement(sb, member, "example", "Example");
        AppendSeeAlso(sb, member);

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private static string? ExtractSummary(XElement member)
    {
        XElement? summary = member.Element("summary");
        if (summary is null)
        {
            return null;
        }

        string text = ExtractText(summary);
        return text.Length == 0 ? null : text;
    }

    private static XElement? ResolveMember(ISymbol symbol)
    {
        XElement? member = ParseMember(symbol.GetDocumentationCommentXml());

        if (member is not null && !IsInheritDocOnly(member))
        {
            return member;
        }

        // Walk the inheritance chain to resolve <inheritdoc/>
        for (var depth = 0; depth < MaxInheritDocDepth; depth++)
        {
            ISymbol? inherited = GetInheritedSymbol(symbol);
            if (inherited is null)
            {
                return null;
            }

            member = ParseMember(inherited.GetDocumentationCommentXml());
            if (member is not null && !IsInheritDocOnly(member))
            {
                return member;
            }

            symbol = inherited;
        }

        return null;
    }

    private static bool IsInheritDocOnly(XElement member)
    {
        bool hasInheritDoc = member.Elements("inheritdoc").Any();
        bool hasOtherContent = member.Elements()
            .Any(e => e.Name.LocalName != "inheritdoc");

        return hasInheritDoc && !hasOtherContent;
    }

    private static ISymbol? GetInheritedSymbol(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.OverriddenMethod is not null)
                {
                    return method.OverriddenMethod;
                }

                if (method.ExplicitInterfaceImplementations.Length > 0)
                {
                    return method.ExplicitInterfaceImplementations[0];
                }

                return FindInterfaceMember(method);

            case IPropertySymbol property:
                if (property.OverriddenProperty is not null)
                {
                    return property.OverriddenProperty;
                }

                if (property.ExplicitInterfaceImplementations.Length > 0)
                {
                    return property.ExplicitInterfaceImplementations[0];
                }

                return FindInterfaceMember(property);

            case IEventSymbol evt:
                if (evt.OverriddenEvent is not null)
                {
                    return evt.OverriddenEvent;
                }

                if (evt.ExplicitInterfaceImplementations.Length > 0)
                {
                    return evt.ExplicitInterfaceImplementations[0];
                }

                return FindInterfaceMember(evt);

            case INamedTypeSymbol type:
                // Try base type first, then direct interfaces
                if (type.BaseType is not null &&
                    type.BaseType.SpecialType != SpecialType.System_Object &&
                    type.BaseType.SpecialType != SpecialType.System_ValueType)
                {
                    return type.BaseType;
                }

                // Fall back to first directly implemented interface
                return type.Interfaces.Length > 0 ? type.Interfaces[0] : type.BaseType;

            default:
                return null;
        }
    }

    private static ISymbol? FindInterfaceMember(ISymbol member) =>
        InterfaceImplementationLookup.FindInterfaceMember(member);

    private static XElement? ParseMember(string? xml)
    {
        if (String.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Root;
        }
        catch
        {
            // Malformed XML in doc comments is common (e.g. unclosed tags) — treat as "no docs"
            return null;
        }
    }

    private static void AppendElement(StringBuilder sb, XElement member, string elementName, string? label)
    {
        XElement? element = member.Element(elementName);
        if (element is null)
        {
            return;
        }

        string text = ExtractText(element);
        if (text.Length == 0)
        {
            return;
        }

        if (label is not null)
        {
            sb.AppendLine($"{label}: {text}");
        }
        else
        {
            sb.AppendLine(text);
        }
    }

    private static void AppendNamedElements(
        StringBuilder sb, XElement member, string elementName, string attributeName, string groupLabel)
    {
        List<XElement> elements = member.Elements(elementName).ToList();
        if (elements.Count == 0)
        {
            return;
        }

        sb.AppendLine($"{groupLabel}:");
        foreach (XElement element in elements)
        {
            string name = element.Attribute(attributeName)?.Value ?? "?";
            string text = ExtractText(element);
            sb.AppendLine($"  {name} \u2014 {text}");
        }
    }

    private static void AppendExceptions(StringBuilder sb, XElement member)
    {
        List<XElement> exceptions = member.Elements("exception").ToList();
        if (exceptions.Count == 0)
        {
            return;
        }

        sb.AppendLine("Exceptions:");
        foreach (XElement exception in exceptions)
        {
            string cref = exception.Attribute("cref")?.Value ?? "?";
            string typeName = ExtractCrefName(cref);
            string text = ExtractText(exception);
            sb.AppendLine($"  {typeName} \u2014 {text}");
        }
    }

    private static void AppendSeeAlso(StringBuilder sb, XElement member)
    {
        List<XElement> seeAlsos = member.Elements("seealso").ToList();
        if (seeAlsos.Count == 0)
        {
            return;
        }

        List<string> refs = seeAlsos
            .Select(e => e.Attribute("cref")?.Value)
            .Where(c => c is not null)
            .Select(c => ExtractCrefName(c!))
            .ToList();

        if (refs.Count > 0)
        {
            sb.AppendLine($"See also: {String.Join(", ", refs)}");
        }
    }

    private static string ExtractText(XElement element)
    {
        var sb = new StringBuilder();
        ExtractTextRecursive(element, sb);
        return NormalizeWhitespace(sb.ToString());
    }

    private static void ExtractTextRecursive(XNode node, StringBuilder sb)
    {
        switch (node)
        {
            case XText text:
                sb.Append(text.Value);
                break;

            case XElement element:
                switch (element.Name.LocalName)
                {
                    case "see":
                    {
                        string? cref = element.Attribute("cref")?.Value;
                        if (cref is not null)
                        {
                            sb.Append(ExtractCrefName(cref));
                        }
                        else
                        {
                            // <see langword="null"/> etc.
                            string? langword = element.Attribute("langword")?.Value;
                            if (langword is not null)
                            {
                                sb.Append(langword);
                            }
                        }

                        break;
                    }

                    case "paramref":
                    case "typeparamref":
                    {
                        string? name = element.Attribute("name")?.Value;
                        if (name is not null)
                        {
                            sb.Append(name);
                        }

                        break;
                    }

                    case "c":
                    case "code":
                        sb.Append(element.Value);
                        break;

                    default:
                        foreach (XNode child in element.Nodes())
                        {
                            ExtractTextRecursive(child, sb);
                        }

                        break;
                }

                break;
        }
    }

    /// <summary>
    ///     Extracts the short type/member name from a cref string like "T:System.ArgumentNullException"
    ///     or "M:Namespace.Type.Method(System.String)".
    /// </summary>
    private static string ExtractCrefName(string cref)
    {
        // Strip the prefix (T:, M:, P:, F:, E:, N:, !:)
        string name = cref.Length > 2 && cref[1] == ':'
            ? cref[2..]
            : cref;

        // For methods with parameters, extract just the method name portion
        int parenIndex = name.IndexOf('(');
        string nameWithoutParams = parenIndex >= 0 ? name[..parenIndex] : name;

        return FqnParser.SimpleName(nameWithoutParams);
    }

    private static string NormalizeWhitespace(string text)
    {
        // Collapse runs of whitespace (including newlines) into single spaces, then trim
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (char c in text)
        {
            if (Char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}
