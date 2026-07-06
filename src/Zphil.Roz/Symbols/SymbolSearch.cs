using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Searches for source declarations matching a name pattern across multiple projects in parallel.
///     When the name is a match-all glob (e.g. <c>*</c>), falls back to a predicate-based API
///     because
///     <see cref="SymbolFinder.FindSourceDeclarationsWithPatternAsync(Solution, string, SymbolFilter, CancellationToken)" />
///     cannot handle it.
/// </summary>
internal static class SymbolSearch
{
    internal static async Task<List<ISymbol>> SearchProjectsAsync(
        IEnumerable<Project> projects, string name, CancellationToken ct)
    {
        bool isMatchAll = PathExtensions.IsGlobPattern(name) && name.Trim('*', '?').Length == 0;

        Task<IEnumerable<ISymbol>>[] searchTasks = projects
            .Select(project => isMatchAll
                ? SymbolFinder.FindSourceDeclarationsAsync(project, _ => true, SymbolFilter.All, ct)
                : SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, name, SymbolFilter.All, ct))
            .ToArray();

        IEnumerable<ISymbol>[] results = await Task.WhenAll(searchTasks);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    ///     Searches for source types by name with automatic GlobalNamespace fallback.
    /// </summary>
    /// <remarks>
    ///     Combines <see cref="SearchProjectsAsync" /> (fast path via SymbolFinder) with
    ///     <see cref="FindSourceTypesByNameAsync" /> (walks GlobalNamespace for errored types).
    ///     Does not deduplicate or filter generated files — callers apply their own policies.
    /// </remarks>
    internal static async Task<List<INamedTypeSymbol>> SearchSourceTypesAsync(
        IEnumerable<Project> projects, string typeName, CancellationToken ct)
    {
        List<Project> projectList = projects.ToList();
        List<ISymbol> results = await SearchProjectsAsync(projectList, typeName, ct);
        List<INamedTypeSymbol> types = results
            .OfType<INamedTypeSymbol>()
            .Where(t => String.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (types.Count > 0)
        {
            return types;
        }

        return await FindSourceTypesByNameAsync(projectList, typeName, ct);
    }

    /// <summary>
    ///     Searches for types by simple name across all referenced assemblies (including metadata/NuGet/BCL),
    ///     not just in-solution source. Walks <see cref="Compilation.GlobalNamespace" /> recursively,
    ///     which includes types from all metadata references. Used as a fallback when
    ///     <see cref="SearchProjectsAsync" /> finds no source declarations for external types like
    ///     <c>IDisposable</c>.
    /// </summary>
    internal static async Task<List<INamedTypeSymbol>> FindMetadataTypesAsync(
        IEnumerable<Project> projects, string typeName, CancellationToken ct)
    {
        HashSet<INamedTypeSymbol> types = new(SymbolEqualityComparer.Default);

        // A namespace-qualified name (e.g. "System.Threading.Timer") resolves via the exact,
        // O(1) GetTypeByMetadataName so same-named types in different namespaces don't collide
        // (e.g. "System.Threading.Timer" vs "System.Timers.Timer"). A simple name keeps the
        // first-match namespace walk — a documented perf optimization over 10K+ namespace nodes.
        bool namespaceQualified = typeName.Contains('.');

        foreach (Project project in projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            if (namespaceQualified)
            {
                INamedTypeSymbol? exact = compilation.GetTypeByMetadataName(typeName);
                if (exact is not null)
                {
                    types.Add(exact);
                }
            }
            else
            {
                CollectTypesFromNamespace(compilation.GlobalNamespace, typeName, types);
            }

            // Short-circuit: one match is enough for most cases (same type from different
            // compilations is functionally identical for FindImplementationsAsync)
            if (types.Count > 0)
            {
                break;
            }
        }

        return types.ToList();
    }

    /// <summary>
    ///     Walks <see cref="Compilation.GlobalNamespace" /> to find source-located types by name.
    /// </summary>
    /// <remarks>
    ///     Used as a fallback when <see cref="SearchProjectsAsync" /> misses types whose compilation
    ///     context has errors (e.g. CS0315 constraint violations in base types).
    /// </remarks>
    internal static async Task<List<INamedTypeSymbol>> FindSourceTypesByNameAsync(
        IEnumerable<Project> projects, string typeName, CancellationToken ct)
    {
        HashSet<INamedTypeSymbol> types = new(SymbolEqualityComparer.Default);

        foreach (Project project in projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            CollectSourceTypes(compilation.GlobalNamespace, types, typeName);
        }

        return types.ToList();
    }

    /// <summary>
    ///     Finds source types that derive from or implement <paramref name="targetType" /> by walking
    ///     all source types in the solution.
    /// </summary>
    /// <remarks>
    ///     Supplements <c>SymbolFinder.FindDerivedClassesAsync</c> and
    ///     <c>SymbolFinder.FindImplementationsAsync</c> which miss types with compilation errors.
    /// </remarks>
    internal static async Task<List<INamedTypeSymbol>> FindDerivedSourceTypesAsync(
        INamedTypeSymbol targetType, Solution solution, CancellationToken ct)
    {
        bool isInterface = targetType.TypeKind == TypeKind.Interface;
        string targetName = targetType.Name;
        List<INamedTypeSymbol> results = [];

        foreach (Project project in solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            List<INamedTypeSymbol> sourceTypes = [];
            CollectSourceTypes(compilation.GlobalNamespace, sourceTypes);

            foreach (INamedTypeSymbol candidate in sourceTypes)
            {
                if (SymbolEqualityComparer.Default.Equals(candidate, targetType))
                {
                    continue;
                }

                if (IsDerivedOrImplementing(candidate, targetType, isInterface, targetName))
                {
                    results.Add(candidate);
                }
            }
        }

        return SymbolDeduplication.DeduplicateByLocation(results);
    }

    /// <summary>
    ///     Checks whether <paramref name="candidate" /> derives from or implements <paramref name="targetType" />.
    /// </summary>
    /// <remarks>
    ///     Uses a three-tier approach: semantic base chain, transitive interface check, and syntax fallback
    ///     for error types whose semantic base is unresolvable.
    /// </remarks>
    private static bool IsDerivedOrImplementing(
        INamedTypeSymbol candidate, INamedTypeSymbol targetType, bool isInterface, string targetName)
    {
        // Tier 1: Semantic — walk BaseType chain (classes) or AllInterfaces (interfaces)
        if (isInterface)
        {
            if (candidate.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, targetType.OriginalDefinition)))
            {
                return true;
            }
        }
        else
        {
            INamedTypeSymbol? baseType = candidate.BaseType;
            while (baseType is not null)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, targetType.OriginalDefinition))
                {
                    return true;
                }

                baseType = baseType.BaseType;
            }
        }

        // Tier 2: Syntax fallback — only when the candidate has unresolvable base types (ErrorType).
        // Avoids expensive syntax walks for the common case where semantic checks are definitive.
        bool hasErrorBase = candidate.BaseType is { TypeKind: TypeKind.Error }
                            || candidate.AllInterfaces.Any(i => i.TypeKind == TypeKind.Error);
        if (!hasErrorBase)
        {
            return false;
        }

        foreach (SyntaxReference syntaxRef in candidate.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is not BaseTypeDeclarationSyntax typeDecl)
            {
                continue;
            }

            if (typeDecl.BaseList is null)
            {
                continue;
            }

            foreach (BaseTypeSyntax baseTypeSyntax in typeDecl.BaseList.Types)
            {
                string baseText = baseTypeSyntax.Type switch
                {
                    SimpleNameSyntax simple => simple.Identifier.Text,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                    _ => ""
                };

                if (String.Equals(baseText, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Walks <paramref name="ns" /> recursively and adds source-located types to
    ///     <paramref name="results" />. When <paramref name="typeName" /> is supplied only types
    ///     with that name are collected; otherwise every source type is.
    /// </summary>
    internal static void CollectSourceTypes(
        INamespaceSymbol ns, ICollection<INamedTypeSymbol> results, string? typeName = null)
    {
        ImmutableArray<INamedTypeSymbol> types = typeName is null
            ? ns.GetTypeMembers()
            : ns.GetTypeMembers(typeName);

        foreach (INamedTypeSymbol type in types)
        {
            if (type.Locations.Any(loc => loc.IsInSource))
            {
                results.Add(type);
            }
        }

        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
        {
            CollectSourceTypes(child, results, typeName);
        }
    }

    /// <returns><c>true</c> if at least one match was found, allowing callers to stop recursing.</returns>
    private static bool CollectTypesFromNamespace(
        INamespaceSymbol ns, string typeName, HashSet<INamedTypeSymbol> results)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers(typeName))
        {
            results.Add(type);
        }

        // Stop recursing once we've found a match — avoids traversing the entire
        // namespace tree (10K+ nodes in large solutions with many NuGet references)
        if (results.Count > 0)
        {
            return true;
        }

        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
        {
            if (CollectTypesFromNamespace(child, typeName, results))
            {
                return true;
            }
        }

        return false;
    }
}
