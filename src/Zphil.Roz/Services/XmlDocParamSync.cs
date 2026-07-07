using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services;

/// <summary>
///     Reorders / removes / placeholder-adds the <c>&lt;param&gt;</c> elements of a method's XML doc
///     comment to match a new parameter-name order. Trivia-only: it cannot affect the compile delta, so
///     an unexpected doc shape is a safe no-op rather than an error.
/// </summary>
internal static class XmlDocParamSync
{
    /// <summary>
    ///     Returns <paramref name="decl" /> with its <c>&lt;param&gt;</c> doc elements reordered to
    ///     <paramref name="newParamNames" />, dropping documentation for removed parameters and inserting
    ///     empty placeholders for added ones. A method with no doc comment (or no <c>&lt;param&gt;</c>
    ///     elements) is returned unchanged.
    /// </summary>
    public static BaseMethodDeclarationSyntax Sync(
        BaseMethodDeclarationSyntax decl, IReadOnlyList<string> newParamNames)
    {
        SyntaxTrivia docTrivia = decl.GetLeadingTrivia()
            .FirstOrDefault(t => t.HasStructure && t.GetStructure() is DocumentationCommentTriviaSyntax);
        if (docTrivia.GetStructure() is not DocumentationCommentTriviaSyntax doc)
        {
            return decl;
        }

        List<XmlNodeSyntax> content = doc.Content.ToList();
        List<int> paramIndices = content
            .Select((node, index) => (node, index))
            .Where(x => IsParamElement(x.node))
            .Select(x => x.index)
            .ToList();

        if (paramIndices.Count == 0)
        {
            return decl;
        }

        Dictionary<string, XmlElementSyntax> existingByName = new(StringComparer.Ordinal);
        foreach (int index in paramIndices)
        {
            var element = (XmlElementSyntax)content[index];
            if (ParamName(element) is { } name && !existingByName.ContainsKey(name))
            {
                existingByName[name] = element;
            }
        }

        var template = (XmlElementSyntax)content[paramIndices[0]];
        int firstParam = paramIndices[0];
        int lastParam = paramIndices[^1];

        // The text node (`\n/// `) that precedes the first param becomes the separator before each param.
        XmlNodeSyntax? separator = firstParam > 0 ? content[firstParam - 1] : null;

        List<XmlNodeSyntax> rebuilt = [];
        foreach (string name in newParamNames)
        {
            if (separator is not null)
            {
                rebuilt.Add(separator);
            }

            rebuilt.Add(existingByName.TryGetValue(name, out XmlElementSyntax? existing)
                ? existing
                : Placeholder(template, name));
        }

        // Replace the run [separator-before-first .. last-param] with the rebuilt param block, preserving
        // everything before it (summary) and after it (returns/remarks + the trailing text node).
        int replaceStart = separator is not null ? firstParam - 1 : firstParam;
        List<XmlNodeSyntax> newContent = [];
        newContent.AddRange(content.Take(replaceStart));
        newContent.AddRange(rebuilt);
        newContent.AddRange(content.Skip(lastParam + 1));

        DocumentationCommentTriviaSyntax newDoc = doc.WithContent(SyntaxFactory.List(newContent));
        SyntaxTriviaList newLeading = decl.GetLeadingTrivia()
            .Replace(docTrivia, SyntaxFactory.Trivia(newDoc));
        return decl.WithLeadingTrivia(newLeading);
    }

    private static bool IsParamElement(XmlNodeSyntax node) =>
        node is XmlElementSyntax { StartTag.Name.LocalName.ValueText: "param" };

    private static string? ParamName(XmlElementSyntax element) =>
        element.StartTag.Attributes
            .OfType<XmlNameAttributeSyntax>()
            .FirstOrDefault(a => a.Name.LocalName.ValueText == "name")
            ?.Identifier.Identifier.ValueText;

    /// <summary>
    ///     Clones <paramref name="template" />'s tag structure with the <c>name</c> attribute set to
    ///     <paramref name="name" /> and empty content — an undocumented-parameter placeholder.
    /// </summary>
    private static XmlElementSyntax Placeholder(XmlElementSyntax template, string name)
    {
        XmlNameAttributeSyntax? nameAttribute = template.StartTag.Attributes
            .OfType<XmlNameAttributeSyntax>()
            .FirstOrDefault(a => a.Name.LocalName.ValueText == "name");
        if (nameAttribute is null)
        {
            return template;
        }

        XmlNameAttributeSyntax renamed = nameAttribute.WithIdentifier(SyntaxFactory.IdentifierName(name));
        XmlElementStartTagSyntax startTag = template.StartTag.ReplaceNode(nameAttribute, renamed);
        return template
            .WithStartTag(startTag)
            .WithContent(SyntaxFactory.List<XmlNodeSyntax>());
    }
}
