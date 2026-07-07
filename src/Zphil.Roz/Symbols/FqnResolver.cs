using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Resolves fully-qualified symbol names against Roslyn compilations using
///     <see cref="Compilation.GetTypeByMetadataName" /> for O(1) type lookup,
///     then <see cref="INamedTypeSymbol.GetMembers()" /> for member resolution.
///     Falls back to <see cref="SymbolSearch" /> for simple type names without namespace.
/// </summary>
internal static class FqnResolver
{
    /// <summary>
    ///     Attempts to resolve a fully-qualified name against all projects in the solution.
    ///     Tries each candidate decomposition from <see cref="FqnParser.Decompose" /> in order,
    ///     returning the first non-empty result.
    /// </summary>
    /// <remarks>
    ///     When <paramref name="arity" /> is specified, constructs backtick metadata names
    ///     (e.g. <c>Namespace.Type`1</c>) to match generic types via
    ///     <see cref="Compilation.GetTypeByMetadataName" />.
    /// </remarks>
    internal static async Task<List<ISymbol>> ResolveAsync(
        IEnumerable<Project> projects, string fqn, int? arity = null, CancellationToken ct = default)
    {
        IReadOnlyList<FqnCandidate> candidates = FqnParser.Decompose(fqn);
        List<Project> projectList = projects.ToList();

        foreach (FqnCandidate candidate in candidates)
        {
            List<ISymbol> results = await TryCandidateAsync(projectList, candidate, arity, ct);
            if (results.Count > 0)
            {
                return results;
            }
        }

        return [];
    }

    private static async Task<List<ISymbol>> TryCandidateAsync(
        List<Project> projects, FqnCandidate candidate, int? arity, CancellationToken ct)
    {
        List<INamedTypeSymbol> types = await FindTypesAsync(projects, candidate.TypeFqn, arity, ct);

        // If no type found with dots, try replacing the last dot with '+' for nested types
        if (types.Count == 0 && candidate.MemberName is null && candidate.TypeFqn.Contains('.'))
        {
            int lastDot = candidate.TypeFqn.LastIndexOf('.');
            var nestedName = $"{candidate.TypeFqn[..lastDot]}+{candidate.TypeFqn[(lastDot + 1)..]}";
            types = await FindTypesByMetadataNameAsync(projects, nestedName, ct);
        }

        if (types.Count == 0)
        {
            return [];
        }

        // Type-only resolution
        if (candidate.MemberName is null)
        {
            return SymbolDeduplication.DeduplicateByLocation(types.Cast<ISymbol>());
        }

        // Member resolution — includes nested types via GetMembers()
        return SymbolDeduplication.DeduplicateByLocation(
            types.SelectMany(t => ExtractMembersFromType(t, candidate.MemberName)));
    }

    /// <summary>
    ///     Finds types by name, trying <see cref="Compilation.GetTypeByMetadataName" /> first
    ///     (for fully-qualified names with namespace), then falling back to
    ///     <see cref="SymbolSearch" /> for simple type names or case-insensitive matching.
    ///     When <paramref name="arity" /> is specified, also tries the backtick metadata name
    ///     (e.g. <c>Namespace.Grain`1</c>) for generic types.
    /// </summary>
    private static async Task<List<INamedTypeSymbol>> FindTypesAsync(
        List<Project> projects, string typeName, int? arity, CancellationToken ct)
    {
        // GetTypeByMetadataName works for full metadata names (Namespace.Type), case-sensitive
        List<INamedTypeSymbol> types = await FindTypesByMetadataNameAsync(projects, typeName, ct);

        // When arity is specified and the plain name didn't match (or matched the wrong arity),
        // try the backtick metadata name (e.g. "Namespace.Grain`1")
        if (arity is > 0 && types.All(t => t.TypeParameters.Length != arity.Value))
        {
            List<INamedTypeSymbol> arityTypes = await FindTypesByMetadataNameAsync(projects, $"{typeName}`{arity.Value}", ct);
            types = types.Concat(arityTypes).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToList();
        }

        if (types.Count > 0)
        {
            return types;
        }

        // Fallback: use SymbolSearch pattern matching for:
        // - Simple names ("Circle") where the full metadata name is "TestFixture.Shapes.Circle"
        // - Case-insensitive matching ("testfixture.shapes.circle")
        string searchName = FqnParser.SimpleName(typeName);

        List<INamedTypeSymbol> typeResults = await SymbolSearch.SearchSourceTypesAsync(
            projects, searchName, ct);
        IEnumerable<INamedTypeSymbol> candidates = typeResults
            .WhereNotGenerated();

        if (typeName.Contains('.'))
        {
            string expectedNamespace = FqnParser.Namespace(typeName);
            candidates = candidates.Where(t => String.Equals(
                t.ContainingNamespace?.ToDisplayString(), expectedNamespace,
                StringComparison.OrdinalIgnoreCase));
        }

        return candidates.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToList();
    }

    private static async Task<List<INamedTypeSymbol>> FindTypesByMetadataNameAsync(
        List<Project> projects, string metadataName, CancellationToken ct)
    {
        HashSet<INamedTypeSymbol> types = new(SymbolEqualityComparer.Default);

        foreach (Project project in projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(ct);
            INamedTypeSymbol? type = compilation?.GetTypeByMetadataName(metadataName);
            if (type is not null)
            {
                types.Add(type);
            }
        }

        return types.ToList();
    }

    private static IEnumerable<ISymbol> ExtractMembersFromType(INamedTypeSymbol type, string memberName)
    {
        // Delegate to SpecialMemberResolver for .ctor, .cctor, this[], op_* names
        if (SpecialMemberResolver.IsSpecialMemberName(memberName))
        {
            return SpecialMemberResolver.ExtractMembers(type, memberName);
        }

        // Standard member lookup by name (case-insensitive)
        return type.GetMembers()
            .Where(m => String.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
    }
}
