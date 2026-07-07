using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats type hierarchy information (base types, interfaces, derived class trees)
///     into human-readable plain text.
/// </summary>
internal static class TypeHierarchyFormatter
{
    /// <summary>
    ///     Appends the base type chain for a type, with increasing indentation per level.
    /// </summary>
    /// <example>
    ///     Base type chain for <c>Circle</c>:
    ///     <code>
    ///     Base types:
    ///       Shape (TestFixture/Shapes/Shape.cs:3)
    ///     </code>
    ///     Returns <c>"(none beyond System.Object)"</c> when the type has no explicit base.
    /// </example>
    public static void AppendBaseTypeChain(StringBuilder sb, INamedTypeSymbol typeSymbol, string solutionDir)
    {
        sb.AppendLine("Base types:");
        List<INamedTypeSymbol> baseTypes = typeSymbol.BaseTypes().ToList();
        if (baseTypes.Count == 0)
        {
            sb.AppendLine("  (none beyond System.Object)");
            return;
        }

        var indent = 1;
        foreach (INamedTypeSymbol baseType in baseTypes)
        {
            var prefix = new string(' ', indent * 2);
            sb.AppendLine($"{prefix}{baseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}{FormatSourceLocation(baseType, solutionDir)}");
            indent++;
        }
    }

    /// <summary>
    ///     Appends the list of all implemented interfaces for a type.
    /// </summary>
    /// <example>
    ///     Implemented interfaces for <c>Shape</c>:
    ///     <code>
    ///     Implemented interfaces:
    ///       IShape (TestFixture/Shapes/IShape.cs:3)
    ///     </code>
    ///     Returns <c>"(none)"</c> when the type implements no interfaces.
    /// </example>
    public static void AppendImplementedInterfaces(StringBuilder sb, INamedTypeSymbol typeSymbol, string solutionDir)
    {
        sb.AppendLine();
        sb.AppendLine("Implemented interfaces:");
        INamedTypeSymbol[] interfaces = typeSymbol.AllInterfaces.ToArray();
        if (interfaces.Length == 0)
        {
            sb.AppendLine("  (none)");
            return;
        }

        foreach (INamedTypeSymbol iface in interfaces.OrderBy(i => i.Name))
        {
            sb.AppendLine($"  {iface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}{FormatSourceLocation(iface, solutionDir)}");
        }
    }

    /// <summary>
    ///     Formats derived classes as an indented tree showing inheritance hierarchy.
    ///     Only meaningful for class hierarchies (not interfaces).
    /// </summary>
    /// <example>
    ///     Derived class tree for <c>Shape</c>:
    ///     <code>
    ///     Circle (TestFixture/Shapes/Circle.cs:3)
    ///     Rectangle (TestFixture/Shapes/Rectangle.cs:3)
    ///     Triangle (TestFixture/Shapes/Triangle.cs:3)
    ///     </code>
    ///     A deeper hierarchy would use box-drawing connectors:
    ///     <code>
    ///     ├─ ChildA (path/to/ChildA.cs:1)
    ///     │  └─ GrandchildA (path/to/GrandchildA.cs:1)
    ///     └─ ChildB (path/to/ChildB.cs:1)
    ///     </code>
    /// </example>
    public static string FormatDerivedClassTree(
        INamedTypeSymbol root, List<INamedTypeSymbol> allDerived, string solutionDir)
    {
        // Build parent → children map
        Dictionary<string, List<INamedTypeSymbol>> childrenMap = new(StringComparer.Ordinal);
        foreach (INamedTypeSymbol type in allDerived)
        {
            string parentKey = type.BaseType?.OriginalDefinition.ToDisplayString() ?? "";
            if (!childrenMap.TryGetValue(parentKey, out List<INamedTypeSymbol>? list))
            {
                childrenMap[parentKey] = list = [];
            }

            list.Add(type);
        }

        // Sort children alphabetically within each group
        foreach (List<INamedTypeSymbol> list in childrenMap.Values)
        {
            list.Sort((a, b) => String.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }

        var sb = new StringBuilder();
        HashSet<string> visited = new(StringComparer.Ordinal);
        string rootKey = root.OriginalDefinition.ToDisplayString();
        AppendDerivedTree(sb, rootKey, "", true, childrenMap, solutionDir, visited);
        return sb.ToString().TrimEnd();
    }

    private static void AppendDerivedTree(
        StringBuilder sb, string parentKey, string indent, bool isRoot,
        Dictionary<string, List<INamedTypeSymbol>> childrenMap, string solutionDir,
        HashSet<string> visited)
    {
        if (!childrenMap.TryGetValue(parentKey, out List<INamedTypeSymbol>? children))
        {
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            INamedTypeSymbol child = children[i];
            string childKey = child.OriginalDefinition.ToDisplayString();

            // Guard against cycles
            if (!visited.Add(childKey))
            {
                continue;
            }

            bool isLast = i == children.Count - 1;
            string connector = isRoot ? "" : isLast ? "\u2514\u2500 " : "\u251C\u2500 ";
            string location = FormatSourceLocation(child, solutionDir);

            sb.AppendLine($"{indent}{connector}{child.Name}{location}");

            string childIndent = isRoot ? "" : indent + (isLast ? "   " : "\u2502  ");
            AppendDerivedTree(sb, childKey, childIndent, false, childrenMap, solutionDir, visited);
        }
    }

    private static string FormatSourceLocation(ISymbol symbol, string solutionDir)
    {
        Location? loc = symbol.PreferNonGeneratedSourceLocation();
        return loc is not null ? $" ({loc.ToFileLineString(solutionDir)})" : " (external)";
    }
}
