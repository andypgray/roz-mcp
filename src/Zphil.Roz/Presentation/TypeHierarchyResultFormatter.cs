using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using static Zphil.Roz.Presentation.FormattingHelpers;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats results for the get_type_hierarchy tool.
/// </summary>
internal static class TypeHierarchyResultFormatter
{
    /// <summary>
    ///     Formats multiple type-hierarchy results as labeled sections. Per-name errors in the
    ///     batch are rendered inline as <c>=== Error: {name} ===</c> blocks. When two items
    ///     resolve to different types sharing a simple name (e.g. across namespaces), those
    ///     headers are qualified (<c>Namespace.Type</c>) so the blocks stay distinguishable;
    ///     non-colliding headers stay bare.
    /// </summary>
    public static string Format(
        IReadOnlyList<BatchItem<TypeHierarchyResult>> items, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full) =>
        FormatBatchWithErrors(items,
            CollisionAwareHeader(items, r => r.TypeSymbol.Name, r => SymbolQualifiers.For(r.TypeSymbol)),
            r => Format(r, includeDocs, level));

    /// <summary>
    ///     Formats a type's base type chain and implemented interfaces.
    /// </summary>
    /// <example>
    ///     Type hierarchy for <c>Circle</c>:
    ///     <code>
    ///     Type hierarchy for 'Circle':
    /// 
    ///     Base types:
    ///       Shape (TestFixture/Shapes/Shape.cs:3)
    /// 
    ///     Implemented interfaces:
    ///       IShape (TestFixture/Shapes/IShape.cs:3)
    ///     </code>
    /// </example>
    private static string Format(TypeHierarchyResult result, bool includeDocs = false, DetailLevel level = DetailLevel.Full)
    {
        bool isMetadataOnly = result.TypeSymbol.IsMetadataSymbol();
        EffectiveOptions eff = ComputeEffective(level, includeDocs: includeDocs || isMetadataOnly);

        var sb = new StringBuilder();
        INamedTypeSymbol t = result.TypeSymbol;
        sb.AppendLine($"Type hierarchy for '{t.Name}' ({SymbolFormatter.FormatMemberTag(t)}):");
        sb.AppendLine();

        if (eff.Docs)
        {
            int lengthBefore = sb.Length;
            XmlDocFormatter.AppendDocumentation(sb, result.TypeSymbol, "");
            if (sb.Length > lengthBefore)
            {
                sb.AppendLine();
            }
        }

        TypeHierarchyFormatter.AppendBaseTypeChain(sb, result.TypeSymbol, result.SolutionDir);
        TypeHierarchyFormatter.AppendImplementedInterfaces(sb, result.TypeSymbol, result.SolutionDir);
        return sb.ToString().TrimEnd();
    }
}
