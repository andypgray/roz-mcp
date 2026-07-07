using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using static Zphil.Roz.Presentation.FormattingHelpers;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats results for reference tools: find_references, find_implementations.
/// </summary>
internal static class ReferenceResultFormatter
{
    /// <summary>
    ///     Formats a mixed batch of <see cref="FindReferencesResult" /> and <see cref="FindCallersResult" />
    ///     (shared base <see cref="ReferenceSearchResult" />) as labeled sections. When two items
    ///     resolve to different symbols sharing a simple name, those headers are qualified
    ///     (<c>Type.Member</c>, escalating to <c>Namespace.Type.Member</c>) so the blocks stay
    ///     distinguishable; non-colliding headers stay bare.
    /// </summary>
    public static string Format(
        IReadOnlyList<BatchItem<ReferenceSearchResult>> items, DetailLevel level = DetailLevel.Full) =>
        FormatBatchWithErrors(items,
            CollisionAwareHeader(items, r => r.SymbolName, r => r.Qualifiers),
            r => r switch
            {
                FindReferencesResult fr => Format(fr, level),
                FindCallersResult fc => Format(fc, level),
                _ => throw new ArgumentException($"Unknown result type: {r.GetType()}")
            });

    /// <summary>
    ///     Formats multiple find_implementations results as labeled sections. Per-name errors in
    ///     the batch are rendered inline as <c>=== Error: {name} ===</c> blocks. When two items
    ///     resolve to different symbols sharing a simple name, those headers are qualified
    ///     (<c>Type.Member</c>, escalating to <c>Namespace.Type.Member</c>) so the blocks stay
    ///     distinguishable; non-colliding headers stay bare.
    /// </summary>
    public static string Format(
        IReadOnlyList<BatchItem<FindImplementationsResult>> items, bool includeDocs = false,
        bool includeBody = false, DetailLevel level = DetailLevel.Full,
        int? maxBodyLines = null, bool includeGenerated = false) =>
        FormatBatchWithErrors(items,
            CollisionAwareHeader(items, r => r.SymbolName, r => r.Qualifiers),
            r => Format(r, includeDocs, includeBody, level, maxBodyLines, includeGenerated));

    /// <summary>
    ///     Formats reference locations grouped by file, with count and optional truncation notice.
    /// </summary>
    /// <example>
    ///     References to <c>IShape</c> (contextLines=0, truncated):
    ///     <code>
    ///     References to 'IShape' (showing 200 of 1898 location(s) across 42 files, 6 projects):
    ///       Distribution:
    ///         ICSharpCode.Decompiler                    1504  (36 files)
    ///         ICSharpCode.Decompiler.Tests               289  (4 files)
    ///         ICSharpCode.ILSpyX                          58  (3 files)
    /// 
    ///     Xaml/XamlType.cs [ICSharpCode.BamlDecompiler]:
    ///          41 | public IType ResolvedType { get; set; }
    ///     IL/ILReader.cs [ICSharpCode.Decompiler]:
    ///         330 | List&lt;IType&gt;[] storesByVar = new List&lt;IType&gt;[scope.Variables.Count];  (2 refs)
    ///     </code>
    /// </example>
    public static string Format(FindReferencesResult result, DetailLevel level = DetailLevel.Full)
    {
        if (result.Locations.Count == 0)
        {
            string noResults = FormatNoResultsWithDiFallback(
                ReferenceKindPluralNoun(result.Kinds), result.SymbolName, result.ExcludedTestCount,
                result.DiRegistrations, result.SolutionDir);
            return result.ProjectIgnored ? $"{noResults}\n{FormatProjectIgnoredHint()}" : noResults;
        }

        bool includeSourceContext = level < DetailLevel.Low;

        string countInfo = FormatHeaderCount(result.Locations.Count, result.TotalCount);
        string scopeInfo = result.Distribution is { Count: > 0 }
            ? $" across {result.Distribution.Sum(e => e.FileCount)} files, {result.Distribution.Count} projects"
            : "";

        var sb = new StringBuilder();
        AppendReferenceHeaderBlock(sb,
            $"{ReferenceKindHeader(result.Kinds)} '{result.SymbolName}' ({countInfo} location(s){scopeInfo}):",
            result.Distribution, result.FileDistribution);
        sb.AppendLine(ReferenceFormatter.FormatReferenceLocations(result.Locations, result.SolutionDir, includeSourceContext));

        if (result.TotalCount > result.Locations.Count)
        {
            sb.AppendLine(FormatTruncationHint(result.TotalCount, "increase maxResults", result.IncludedTestCount));
        }

        if (result.ExcludedTestCount > 0)
        {
            sb.AppendLine(FormatExcludedTestResultsHint(result.ExcludedTestCount));
        }

        if (result.ProjectIgnored)
        {
            sb.AppendLine(FormatProjectIgnoredHint());
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats implementations of a member, or derived/implementing types when the target
    ///     symbol is a type. Members render as a numbered list with a contract header; types
    ///     render as a tree (class hierarchy) or flat numbered list (interface, or includeBody).
    /// </summary>
    /// <example>
    ///     Implementations of <c>IShape.Describe()</c>:
    ///     <code>
    ///     Implementations of 'IShape.Describe' (3):
    ///       Contract: string Describe()
    ///                 TestFixture/Shapes/IShape.cs:5
    /// 
    ///     1. public override method string Circle.Describe()
    ///       Location: TestFixture/Shapes/Circle.cs:12
    /// 
    ///     2. public override method string Rectangle.Describe()
    ///       Location: TestFixture/Shapes/Rectangle.cs:15
    ///     </code>
    /// </example>
    public static string Format(
        FindImplementationsResult result, bool includeDocs = false, bool includeBody = false,
        DetailLevel level = DetailLevel.Full, int? maxBodyLines = null, bool includeGenerated = false)
    {
        if (result.TargetSymbol is INamedTypeSymbol targetType)
        {
            return FormatTypeImplementations(result, targetType, includeDocs, includeBody, level, maxBodyLines, includeGenerated);
        }

        return FormatMemberImplementations(result, includeDocs, includeBody, level, maxBodyLines, includeGenerated);
    }

    private static string FormatMemberImplementations(
        FindImplementationsResult result, bool includeDocs, bool includeBody,
        DetailLevel level, int? maxBodyLines, bool includeGenerated)
    {
        if (result.Implementations.Count == 0)
        {
            if (result.TargetSymbol is IMethodSymbol { IsStatic: true, MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion })
            {
                return $"'{result.SymbolName}' is a static operator — operators cannot be overridden or implemented. " +
                       "Use find_references referenceKinds=invocations to find call sites.";
            }

            var noImpl = $"No implementations found for '{result.SymbolName}'.";
            if (result.ExcludedTestCount > 0)
            {
                noImpl += $"\n{FormatExcludedTestResultsHint(result.ExcludedTestCount)}";
            }

            if (result.ExcludedMetadataCount > 0)
            {
                noImpl += $"\n{FormatExcludedMetadataHint(result.ExcludedMetadataCount)}";
            }

            return noImpl;
        }

        var sb = new StringBuilder();

        // Show containing type in header for member-level implementations
        string displayName = result.TargetSymbol?.ContainingType is { } ct
            ? $"{ct.Name}.{result.SymbolName}"
            : result.SymbolName;
        sb.AppendLine($"Implementations of '{displayName}' ({FormatHeaderCount(result.Implementations.Count, result.TotalCount)}):");

        bool targetIsMetadata = result.TargetSymbol?.IsMetadataSymbol() ?? false;
        EffectiveOptions eff = ComputeEffective(level, includeBody, includeDocs || targetIsMetadata);

        // Show the contract being implemented (interface/abstract declaration + location)
        if (result.TargetSymbol is IMethodSymbol or IPropertySymbol or IEventSymbol
            && result.TargetSymbol.ContainingType is { TypeKind: TypeKind.Interface } or { IsAbstract: true })
        {
            string signature = result.TargetSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            Location? loc = result.TargetSymbol.PreferNonGeneratedSourceLocation();
            string locationInfo = loc is not null
                ? $"\n            {loc.ToFileLineString(result.SolutionDir)}"
                : "";
            sb.AppendLine($"  Contract: {signature}{locationInfo}");
        }

        if (eff.Docs && result.TargetSymbol is not null)
        {
            XmlDocFormatter.AppendDocumentation(sb, result.TargetSymbol, "  ");
        }

        if (result.Distribution is { Count: > 0 })
        {
            sb.AppendLine(ReferenceFormatter.FormatDistributionSummary(result.Distribution));
        }

        sb.AppendLine();

        if (level == DetailLevel.Minimal)
        {
            FormatSymbolNamesOnlyList(sb, result.Implementations, result.SolutionDir);
        }
        else
        {
            sb.Append(SymbolFormatter.FormatSymbolList(result.Implementations, result.SolutionDir, includeBody: eff.Body, includeDocs: eff.Docs, maxBodyLines: maxBodyLines, includeGenerated: includeGenerated));
        }

        AppendImplementationTrailers(sb, result);
        return sb.ToString().TrimEnd();
    }

    private static string FormatTypeImplementations(
        FindImplementationsResult result, INamedTypeSymbol targetType,
        bool includeDocs, bool includeBody, DetailLevel level, int? maxBodyLines, bool includeGenerated)
    {
        bool isInterface = targetType.TypeKind == TypeKind.Interface;

        if (result.Implementations.Count == 0)
        {
            return FormatEmptyTypeImplementations(result, targetType, isInterface);
        }

        bool targetIsMetadata = targetType.IsMetadataSymbol();
        EffectiveOptions eff = ComputeEffective(level, includeBody, includeDocs || targetIsMetadata);
        string header = isInterface
            ? $"Types implementing '{targetType.Name}'"
            : $"Derived classes of '{targetType.Name}'";

        var sb = new StringBuilder();
        sb.AppendLine($"{header} ({FormatHeaderCount(result.Implementations.Count, result.TotalCount)}):");

        if (eff.Docs)
        {
            XmlDocFormatter.AppendDocumentation(sb, targetType, "  ");
        }

        if (result.Distribution is { Count: > 0 })
        {
            sb.AppendLine(ReferenceFormatter.FormatDistributionSummary(result.Distribution));
        }

        sb.AppendLine();

        if (level == DetailLevel.Minimal)
        {
            FormatSymbolNamesOnlyList(sb, result.Implementations, result.SolutionDir);
        }
        else if (isInterface || eff.Body)
        {
            // Tree format doesn't support bodies — use flat numbered list when includeBody is requested
            sb.Append(SymbolFormatter.FormatSymbolList(result.Implementations, result.SolutionDir, includeBody: eff.Body, includeDocs: eff.Docs, maxBodyLines: maxBodyLines, includeGenerated: includeGenerated));
        }
        else
        {
            List<INamedTypeSymbol> derivedTypes = result.Implementations.OfType<INamedTypeSymbol>().ToList();
            sb.Append(TypeHierarchyFormatter.FormatDerivedClassTree(targetType, derivedTypes, result.SolutionDir));
        }

        AppendImplementationTrailers(sb, result);
        return sb.ToString().TrimEnd();
    }

    private static void AppendImplementationTrailers(StringBuilder sb, FindImplementationsResult result)
    {
        if (result.TotalCount > result.Implementations.Count)
        {
            sb.AppendLine(FormatTruncationHint(result.TotalCount, "increase maxResults", result.IncludedTestCount));
        }

        if (result.ExcludedTestCount > 0)
        {
            sb.AppendLine(FormatExcludedTestResultsHint(result.ExcludedTestCount));
        }

        if (result.ExcludedMetadataCount > 0)
        {
            sb.AppendLine(FormatExcludedMetadataHint(result.ExcludedMetadataCount));
        }

        if (result.DiRegistrationsByType is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(ReferenceFormatter.FormatDiRegistrationsByType(result.DiRegistrationsByType, result.SolutionDir));
        }
    }

    private static string FormatEmptyTypeImplementations(
        FindImplementationsResult result, INamedTypeSymbol targetType, bool isInterface)
    {
        string name = targetType.Name;

        string noResults = targetType.TypeKind switch
        {
            TypeKind.Struct => $"'{name}' is a struct — structs cannot be inherited. " +
                               "Use find_references to find usages.",
            TypeKind.Enum => $"'{name}' is an enum — enums cannot be inherited. " +
                             "Use find_references to find usages.",
            TypeKind.Class when targetType.IsSealed =>
                $"'{name}' is sealed — sealed classes cannot be derived from. " +
                "Use find_references to find usages.",
            _ => $"No {(isInterface ? "implementing types" : "derived classes")} found for '{name}'."
        };

        if (result.ExcludedTestCount > 0)
        {
            noResults += $"\n{FormatExcludedTestResultsHint(result.ExcludedTestCount)}";
        }

        if (result.ExcludedMetadataCount > 0)
        {
            noResults += $"\n{FormatExcludedMetadataHint(result.ExcludedMetadataCount)}";
        }

        return noResults;
    }

    /// <summary>
    ///     Formats a numbered caller list with call-site snippets and optional truncation notice.
    /// </summary>
    /// <example>
    ///     Callers of <c>IShape.Describe()</c> (small result, no distribution):
    ///     <code>
    ///     Callers of 'Describe' (1):
    /// 
    ///     TestFixture/Services/ShapeService.cs [TestFixture]:
    ///       1. [method] string ShapeService.ProcessShape(IShape):
    ///             17 | return shape.Describe();
    ///     </code>
    ///     When truncated, a distribution summary is prepended (same format as find_references).
    /// </example>
    public static string Format(
        FindCallersResult result, DetailLevel level = DetailLevel.Full)
    {
        if (result.Callers.Count == 0)
        {
            string noResults = FormatNoResultsWithDiFallback(
                "callers", result.SymbolName, result.ExcludedTestCount,
                result.DiRegistrations, result.SolutionDir);

            if (result.ImplementedInterfaceMembers is { Count: > 0 })
            {
                noResults = $"{noResults.TrimEnd()}\n\n{FormatInterfaceDispatchTip(result)}";
            }

            return result.ProjectIgnored ? $"{noResults}\n{FormatProjectIgnoredHint()}" : noResults;
        }

        bool includeSourceContext = level < DetailLevel.Low;

        // Header counts caller symbols, matching TotalCount (ReferenceService groups by calling
        // symbol) and the truncation guard below (result.TotalCount > result.Callers.Count). The
        // prior call-site sum mixed units, so a caller with N call sites showed "N of M" nonsense.
        int callerCount = result.Callers.Count;
        var sb = new StringBuilder();
        string overloadNote = result.OverloadCount > 1 ? $"all {result.OverloadCount} overloads, " : "";
        AppendReferenceHeaderBlock(sb,
            $"Callers of '{result.SymbolName}' ({overloadNote}{FormatHeaderCount(callerCount, result.TotalCount)}):",
            result.Distribution, result.FileDistribution);
        sb.AppendLine(ReferenceFormatter.FormatCallers(result.Callers, result.SolutionDir, includeSourceContext));

        if (result.TotalCount > result.Callers.Count)
        {
            sb.AppendLine(FormatTruncationHint(result.TotalCount, "increase maxResults", result.IncludedTestCount));
        }

        if (result.ExcludedTestCount > 0)
        {
            sb.AppendLine(FormatExcludedTestResultsHint(result.ExcludedTestCount));
        }

        if (result.ProjectIgnored)
        {
            sb.AppendLine(FormatProjectIgnoredHint());
        }

        if (result.ImplementedInterfaceMembers is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(FormatInterfaceDispatchTip(result));
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatInterfaceDispatchTip(FindCallersResult result)
    {
        IReadOnlyList<InterfaceMemberDescriptor> members = result.ImplementedInterfaceMembers!;
        string concreteDisplay = result.ConcreteContainingTypeShort is { } t
            ? $"{t}.{result.SymbolName}"
            : result.SymbolName;

        var sb = new StringBuilder();
        if (members.Count == 1)
        {
            InterfaceMemberDescriptor m = members[0];
            string location = FormatInterfaceMemberLocation(m, result.SolutionDir);
            sb.AppendLine($"Tip: '{concreteDisplay}' implements {MemberRef(m)}");
            sb.AppendLine($"     {location}.");
            sb.Append("     Run find_references referenceKinds=invocations on the interface member to include callers via interface dispatch.");
        }
        else
        {
            sb.AppendLine($"Tip: '{concreteDisplay}' implements {members.Count} interface members:");
            foreach (InterfaceMemberDescriptor m in members)
            {
                string location = FormatInterfaceMemberLocation(m, result.SolutionDir);
                sb.AppendLine($"  - {MemberRef(m)}  ({location})");
            }

            sb.Append("  Run find_references referenceKinds=invocations on any of these to include callers via interface dispatch.");
        }

        return sb.ToString();

        static string MemberRef(InterfaceMemberDescriptor m)
        {
            return $"{m.ContainingTypeShort}.{m.MemberName}";
        }
    }

    private static void AppendReferenceHeaderBlock(
        StringBuilder sb, string headerLine,
        IReadOnlyList<ProjectDistributionEntry>? distribution,
        IReadOnlyList<FileDistributionEntry>? fileDistribution)
    {
        sb.AppendLine(headerLine);

        if (distribution is { Count: > 0 })
        {
            sb.AppendLine(ReferenceFormatter.FormatDistributionSummary(distribution));
        }

        if (fileDistribution is { Count: > 0 })
        {
            sb.AppendLine(ReferenceFormatter.FormatFileDistribution(fileDistribution));
        }

        sb.AppendLine();
    }

    private static string ReferenceKindHeader(ReferenceKind kinds) => kinds switch
    {
        ReferenceKind.Reads => "Reads of",
        ReferenceKind.Writes => "Writes to",
        _ => "References to"
    };

    private static string ReferenceKindPluralNoun(ReferenceKind kinds) => kinds switch
    {
        ReferenceKind.Reads => "reads",
        ReferenceKind.Writes => "writes",
        _ => "references"
    };

    private static string FormatInterfaceMemberLocation(InterfaceMemberDescriptor member, string solutionDir)
    {
        if (member.FilePath is null || member.Line is null)
        {
            return $"external — {member.ContainingTypeFullName}";
        }

        string relative = Path.GetRelativePath(solutionDir, member.FilePath);
        return LocationFormat.Format(relative, member.Line);
    }
}
