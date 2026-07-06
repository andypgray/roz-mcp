using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using static Zphil.Roz.Presentation.FormattingHelpers;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats results for navigation tools: find_symbol, get_symbols_overview,
///     go_to_definition, find_overloads.
/// </summary>
internal static class NavigationResultFormatter
{
    /// <summary>
    ///     Formats multiple find_symbol results as labeled sections, delegating each to the single-result overload.
    ///     Per-name errors in the batch are rendered inline as <c>=== Error: {name} ===</c> blocks.
    /// </summary>
    /// <example>
    ///     Batch searching for "Circle" and "Rectangle":
    ///     <code>
    ///     === Search: "Circle" ===
    /// 
    ///     Found 1 symbol(s) matching "Circle":
    /// 
    ///     1. public class Circle : Shape
    ///       Location: TestFixture/Shapes/Circle.cs:3
    /// 
    ///     === Search: "Rectangle" ===
    /// 
    ///     Found 1 symbol(s) matching "Rectangle":
    /// 
    ///     1. public class Rectangle : Shape
    ///       Location: TestFixture/Shapes/Rectangle.cs:3
    ///     </code>
    /// </example>
    public static string Format(
        IReadOnlyList<BatchItem<FindSymbolResult>> items, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full, int? maxBodyLines = null, bool includeGenerated = false) =>
        FormatBatchWithErrors(items, r => $"Search: \"{r.SearchName}\"",
            r => Format(r, includeDocs, level, maxBodyLines, includeGenerated));

    /// <summary>
    ///     Formats matching symbols with a count header, numbered list, and optional truncation notice.
    /// </summary>
    /// <example>
    ///     Searching for "Shape":
    ///     <code>
    ///     Found 2 symbol(s) matching "Shape":
    /// 
    ///     1. public abstract class Shape : IShape
    ///       Location: TestFixture/Shapes/Shape.cs:3
    /// 
    ///     2. public class ShapeService
    ///       Location: TestFixture/Services/ShapeService.cs:5
    ///     </code>
    ///     Returns <c>No symbols found matching "Hexagon".</c> when no matches exist.
    /// </example>
    public static string Format(
        FindSymbolResult result, bool includeDocs = false, DetailLevel level = DetailLevel.Full,
        int? maxBodyLines = null, bool includeGenerated = false)
    {
        if (result.Symbols.Count == 0)
        {
            string noResults = FormatNoSymbolsFoundMessage(result);
            return result.ExcludedTestProjectCount > 0
                ? $"{noResults}\n{FormatExcludedTestProjectsHint(result.ExcludedTestProjectCount)}"
                : noResults;
        }

        // Auto-enable docs when all resolved symbols are metadata-only (BCL/NuGet types resolved
        // by FQN, e.g. System.Collections.Generic.List) — docs are the primary value for these.
        bool allMetadataOnly = result.Symbols.All(s => s.IsMetadataSymbol());
        EffectiveOptions eff = ComputeEffective(level, result.IncludeBody, includeDocs || allMetadataOnly);
        int effectiveDepth = level >= DetailLevel.Low ? 0 : result.Depth;

        // Namespace symbols from different compilations are merged at presentation time —
        // compute once and derive both the deduplicated count and the formatted list
        List<(ISymbol Symbol, Location[]? LocationOverride)> merged = SymbolMerger.MergeNamespaceSymbols(result.Symbols, includeGenerated);
        int shownCount = merged.Count;
        int totalCount = result.TotalCount - (result.Symbols.Count - merged.Count);

        var sb = new StringBuilder();
        string displayName = GenericArityParser.FormatWithArity(result.SearchName, result.Arity);
        sb.AppendLine($"Found symbol(s) matching \"{displayName}\" ({FormatHeaderCount(shownCount, totalCount)}):");

        if (result.Distribution is { Count: > 0 })
        {
            sb.AppendLine(ReferenceFormatter.FormatDistributionSummary(result.Distribution));
        }

        sb.AppendLine();

        if (level == DetailLevel.Minimal)
        {
            FormatSymbolNamesOnlyList(sb, merged, result.SolutionDir);
        }
        else
        {
            // Suppress "Containing type:" when all results share the same type (e.g. containingType filter was used)
            bool suppressContainingType = result.ContainingType is not null
                                          && merged.All(m => m.Symbol.ContainingType?.Name == result.ContainingType);
            sb.AppendLine(SymbolFormatter.FormatSymbolList(merged, result.SolutionDir, effectiveDepth, eff.Body, eff.Docs, result.MemberKinds, result.MaxMembers, maxBodyLines, suppressContainingType, includeGenerated));
        }

        if (result.IncludeBody && result.MatchMode == SymbolMatchMode.Contains && shownCount > 3)
        {
            sb.AppendLine();
            sb.AppendLine("Hint: Broad search with includeBody returned many results. Use matchMode=Exact or matchMode=StartsWith to narrow.");
        }

        if (totalCount > shownCount)
        {
            sb.AppendLine(FormatTruncationHint(totalCount, "increase maxResults", result.IncludedTestCount));
        }

        if (result.ExcludedTestProjectCount > 0)
        {
            sb.AppendLine(FormatExcludedTestProjectsHint(result.ExcludedTestProjectCount));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats multiple file-level symbol overviews as labeled sections.
    /// </summary>
    public static string Format(
        IReadOnlyList<SymbolsOverviewResult> results, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full) =>
        FormatBatch(results, r => r.RelPath, r => Format(r, includeDocs, level, true));

    /// <summary>
    ///     Formats top-level type declarations for a single file with inline locations.
    /// </summary>
    /// <example>
    ///     <code>
    ///     File: TestFixture/Shapes/Circle.cs
    ///
    ///     public class Circle : Shape  :3
    ///     </code>
    /// </example>
    public static string Format(SymbolsOverviewResult result, bool includeDocs = false, DetailLevel level = DetailLevel.Full,
        bool suppressFileHeader = false)
    {
        if (result.Error is not null)
        {
            return $"Error: {result.Error}";
        }

        if (result.Symbols.Count == 0)
        {
            if (result.HasTopLevelStatements)
            {
                return $"File: {result.RelPath}\n\n"
                       + "This file uses top-level statements (no explicit type declarations). "
                       + "Use Read to view the file contents, or find_symbol to search for specific symbols used in this file.";
            }

            return $"No type declarations found in: {result.RelPath}";
        }

        EffectiveOptions eff = ComputeEffective(level, includeDocs: includeDocs);
        int effectiveDepth = level >= DetailLevel.Low ? 0 : result.Depth;

        var sb = new StringBuilder();
        if (!suppressFileHeader)
        {
            sb.AppendLine($"File: {result.RelPath}");
            sb.AppendLine();
        }

        if (level == DetailLevel.Minimal)
        {
            foreach (ISymbol symbol in result.Symbols)
            {
                sb.AppendLine(SymbolFormatter.FormatSymbolNameOnly(symbol, result.SolutionDir));
            }
        }
        else
        {
            foreach (ISymbol symbol in result.Symbols)
            {
                sb.AppendLine(SymbolFormatter.FormatSymbol(symbol, result.SolutionDir, effectiveDepth,
                    inlineLocation: true, filterToFilePath: result.AbsolutePath, includeDocs: eff.Docs,
                    memberKinds: result.MemberKinds, maxMembers: result.MaxMembers));
                sb.AppendLine();
            }
        }

        if (result.TotalTypeCount > result.Symbols.Count)
        {
            sb.AppendLine(FormatTruncationHint(result.TotalTypeCount, "increase maxTypes"));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats a go_to_definition result. When already at the declaration (and body not requested),
    ///     returns a terse summary (signature + location + member count) to save tokens.
    ///     Otherwise returns full symbol details with a note for metadata-only types.
    /// </summary>
    /// <example>
    ///     Navigating from a usage of <c>IShape</c> in ShapeService.cs:
    ///     <code>
    ///     public interface IShape
    ///       Location: TestFixture/Shapes/IShape.cs:3
    ///     </code>
    ///     When already at the declaration (auto-shows depth=1 members for types):
    ///     <code>
    ///     At declaration.
    /// 
    ///     public interface IShape
    ///       Location: TestFixture/Shapes/IShape.cs:6
    ///       Members (3):
    ///         [public abstract property] double Area  :9
    ///         [public abstract property] double Perimeter  :12
    ///         [public abstract method] string Describe()  :18
    ///     </code>
    /// </example>
    public static string Format(SymbolAtPositionResult result, bool includeDocs = false, DetailLevel level = DetailLevel.Full, int? maxBodyLines = null)
    {
        if (level == DetailLevel.Minimal)
        {
            return SymbolFormatter.FormatSymbolNameOnly(result.Symbol, result.SolutionDir);
        }

        bool isMetadataOnly = result.Symbol.IsMetadataSymbol();
        EffectiveOptions eff = ComputeEffective(level, result.IncludeBody, includeDocs || isMetadataOnly);

        // At declaration: auto-show depth=1 members for types, terse for non-types
        if (result.IsAtDeclaration && !eff.Body)
        {
            if (result.Symbol is INamedTypeSymbol { TypeKind: not TypeKind.Enum and not TypeKind.Delegate })
            {
                string withMembers = SymbolFormatter.FormatSymbol(
                    result.Symbol, result.SolutionDir, 1,
                    projectOrAssemblyName: result.ProjectOrAssemblyName, includeDocs: eff.Docs,
                    maxMembers: result.MaxMembers);
                return $"At declaration.\n\n{withMembers}";
            }

            string terse = SymbolFormatter.FormatSymbol(
                result.Symbol, result.SolutionDir,
                projectOrAssemblyName: result.ProjectOrAssemblyName, includeDocs: eff.Docs);
            return $"At declaration.\n\n{terse}";
        }

        string formatted = SymbolFormatter.FormatSymbol(result.Symbol, result.SolutionDir, result.FormatDepth, eff.Body,
            projectOrAssemblyName: result.ProjectOrAssemblyName, includeDocs: eff.Docs, maxMembers: result.MaxMembers,
            maxBodyLines: maxBodyLines);

        if (isMetadataOnly)
        {
            formatted += "\n\n(Metadata-only type — no source. Adjust column if wrong symbol.)";
        }

        if (result.IsAtDeclaration)
        {
            formatted = $"At declaration.\n\n{formatted}";
        }

        return formatted;
    }

    /// <summary>
    ///     Formats multiple find_overloads results as labeled sections. Per-name errors in the
    ///     batch are rendered inline as <c>=== Error: {name} ===</c> blocks. When two items
    ///     resolve to different symbols sharing a simple name, those headers are qualified
    ///     (<c>Type.Member</c>, escalating to <c>Namespace.Type.Member</c>) so the blocks stay
    ///     distinguishable; non-colliding headers stay bare.
    /// </summary>
    public static string Format(
        IReadOnlyList<BatchItem<FindOverloadsResult>> items, bool includeDocs = false,
        bool includeBody = false, DetailLevel level = DetailLevel.Full, int? maxBodyLines = null) =>
        FormatBatchWithErrors(items,
            // A zero-overload result is still a success (renders the "no overloads found"
            // message), so there is no symbol to qualify by — fall back to the bare name.
            CollisionAwareHeader(items, r => r.SymbolName,
                r => r.Overloads.Count > 0
                    ? SymbolQualifiers.For(r.Overloads[0])
                    : SymbolQualifiers.Bare(r.SymbolName)),
            r => Format(r, includeDocs, includeBody, level, maxBodyLines));

    /// <summary>
    ///     Formats overloads of a method with their signatures and locations.
    ///     Default output is a compact list with parameters and locations.
    ///     Full signatures with body/docs are appended only when <paramref name="includeBody" />
    ///     or <paramref name="includeDocs" /> is requested.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Overloads of '.ctor' in CSharpDecompiler (4):
    /// 
    ///       1. (string fileName, DecompilerSettings settings) — CSharpDecompiler.cs:42
    ///       2. (string fileName, IAssemblyResolver resolver, DecompilerSettings settings) — CSharpDecompiler.cs:55
    ///       3. (MetadataFile module, IAssemblyResolver resolver, DecompilerSettings settings) — CSharpDecompiler.cs:68
    ///       4. (IDecompilerTypeSystem typeSystem, DecompilerSettings settings) — CSharpDecompiler.cs:80
    ///     </code>
    /// </example>
    public static string Format(FindOverloadsResult result, bool includeDocs = false, bool includeBody = false, DetailLevel level = DetailLevel.Full, int? maxBodyLines = null)
    {
        if (result.Overloads.Count == 0)
        {
            return $"No overloads found for '{result.SymbolName}' in {result.ContainingTypeName}.";
        }

        string header = result.Overloads.Count == 1
            ? $"'{result.SymbolName}' in {result.ContainingTypeName} has no overloads (only 1 signature):"
            : $"Overloads of '{result.SymbolName}' in {result.ContainingTypeName} ({result.Overloads.Count}):";

        var sb = new StringBuilder();
        sb.AppendLine(header);

        IReadOnlyList<ISymbol> symbols = result.Overloads;

        if (level == DetailLevel.Minimal)
        {
            sb.AppendLine();
            FormatSymbolNamesOnlyList(sb, symbols, result.SolutionDir);
            return sb.ToString().TrimEnd();
        }

        // Compact summary with locations (default output)
        sb.AppendLine();
        for (var i = 0; i < symbols.Count; i++)
        {
            sb.AppendLine($"  {i + 1}. {FormatOverloadCompactEntry(symbols[i], result.SolutionDir)}");
        }

        bool allMetadataOnly = result.Overloads.All(s => s.IsMetadataSymbol());
        // Full detailed section only when body or docs are effectively requested
        EffectiveOptions eff = ComputeEffective(level, includeBody, includeDocs || allMetadataOnly);
        if (eff.Body || eff.Docs)
        {
            sb.AppendLine();
            sb.Append(SymbolFormatter.FormatSymbolList(symbols, result.SolutionDir,
                includeBody: eff.Body, includeDocs: eff.Docs, maxBodyLines: maxBodyLines,
                suppressContainingType: true));
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatOverloadCompactEntry(ISymbol symbol, string solutionDir)
    {
        string signature = symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Conversion } conv =>
                $"-> {conv.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} ({SymbolFormatter.FormatParameterList(conv.Parameters)})",
            IMethodSymbol method => $"({SymbolFormatter.FormatParameterList(method.Parameters)})",
            IPropertySymbol { IsIndexer: true } prop =>
                $"this[{SymbolFormatter.FormatParameterList(prop.Parameters)}]",
            _ => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
        };

        Location? loc = symbol.PreferNonGeneratedSourceLocation();
        string location = loc is not null ? $" \u2014 {loc.ToFileLineString(solutionDir)}" : "";
        return $"{signature}{location}";
    }

    private static string FormatNoSymbolsFoundMessage(FindSymbolResult result)
    {
        string displayName = GenericArityParser.FormatWithArity(result.SearchName, result.Arity);
        var sb = new StringBuilder($"No symbols found matching \"{displayName}\"");
        if (result.Kind is not null)
        {
            sb.Append($" with kind '{result.Kind}'");
        }

        if (result.ContainingType is not null)
        {
            sb.Append($" in type '{result.ContainingType}'");
        }

        if (result.Project is not null)
        {
            sb.Append($" in project '{result.Project}'");
        }

        if (result.FilePaths is { Length: > 0 })
        {
            sb.Append($" in files [{String.Join(", ", result.FilePaths)}]");
        }

        if (result.ExcludePattern is not null)
        {
            sb.Append($" (excluding '{result.ExcludePattern}')");
        }

        if (result.MatchMode != SymbolMatchMode.Contains)
        {
            sb.Append($" (matchMode={result.MatchMode})");
        }

        sb.Append('.');

        if (result.FilteredOutKinds is { Length: > 0 })
        {
            sb.AppendLine();
            sb.Append(KindFilterBlame.FormatHint(displayName, result.FilteredOutKinds));
        }

        if (result.ContainingTypeIsNamespace)
        {
            sb.AppendLine();
            sb.Append(NamespaceDetection.FormatNamespaceError(result.ContainingType!));
        }

        if (result.Suggestions is { Count: > 0 })
        {
            sb.AppendLine();
            sb.Append("Did you mean: ");
            sb.Append(String.Join(", ", result.Suggestions));
            sb.Append('?');
        }

        return sb.ToString();
    }
}
