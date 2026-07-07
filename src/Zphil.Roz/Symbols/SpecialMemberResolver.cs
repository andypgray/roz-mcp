using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Constants;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Resolves special member names (.ctor, .cctor, Finalize, this[], op_*) that can't be found
///     by <see cref="SymbolSearch" /> because Roslyn's pattern search only matches user-visible names.
/// </summary>
internal static class SpecialMemberResolver
{
    /// <summary>
    ///     Returns <c>true</c> if <paramref name="name" /> is a special member name that requires
    ///     type-then-extract resolution instead of direct pattern search.
    /// </summary>
    public static bool IsSpecialMemberName(string name) =>
        name is ".ctor" or ".cctor" or "Finalize" or "this[]" or "this"
        || OperatorNames.IsOperatorMetadataName(name);

    /// <summary>
    ///     Searches projects for the containing type, then extracts matching special members.
    ///     Returns all matches without deduplication or filtering — callers apply their own policies.
    /// </summary>
    public static async Task<List<ISymbol>> ResolveAsync(
        IEnumerable<Project> projects, string memberName, string? containingType, CancellationToken ct)
    {
        List<Project> projectList = projects.ToList();
        List<INamedTypeSymbol> types;

        if (containingType is not null)
        {
            types = await SymbolSearch.SearchSourceTypesAsync(projectList, containingType, ct);
        }
        else
        {
            // No containing type — walk all source types in the solution
            types = await GetAllSourceTypesAsync(projectList, ct);
        }

        return types
            .SelectMany(t => ExtractMembers(t, memberName))
            .ToList();
    }

    /// <summary>
    ///     Collects all source-defined named types across projects by walking the global namespace tree.
    /// </summary>
    private static async Task<List<INamedTypeSymbol>> GetAllSourceTypesAsync(
        IEnumerable<Project> projects, CancellationToken ct)
    {
        Task<List<INamedTypeSymbol>>[] tasks = projects
            .Select(async project =>
            {
                Compilation? compilation = await project.GetCompilationAsync(ct);
                if (compilation is null)
                {
                    return [];
                }

                List<INamedTypeSymbol> projectTypes = new();
                SymbolSearch.CollectSourceTypes(compilation.GlobalNamespace, projectTypes);
                return projectTypes;
            })
            .ToArray();

        List<INamedTypeSymbol>[] results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    ///     Resolves a special-member lookup with file-based containingType inference when the
    ///     caller didn't provide one.
    /// </summary>
    /// <remarks>
    ///     When <paramref name="containingType" /> is <c>null</c>, requires
    ///     <paramref name="resolvedFilterPath" /> to infer the type from the declarations in that
    ///     file; throws <see cref="UserErrorException" /> if neither is available.
    /// </remarks>
    internal static async Task<(string ContainingType, List<ISymbol> Matches)> ResolveWithFileInferenceAsync(
        Solution solution, IReadOnlyList<Project> projects,
        string searchName, string? containingType,
        string? filePath, string? resolvedFilterPath,
        CancellationToken ct)
    {
        if (containingType is null)
        {
            if (resolvedFilterPath is null)
            {
                throw new UserErrorException(
                    $"containingType is required when symbolName is '{searchName}'. " +
                    "Specify the type that contains the member, or provide filePath to infer it.");
            }

            containingType = await InferContainingTypeFromFileAsync(
                solution, resolvedFilterPath, searchName, filePath, ct);
        }

        List<ISymbol> matches = await ResolveAsync(projects, searchName, containingType, ct);
        return (containingType, matches);
    }

    /// <summary>
    ///     Infers the containing type for a special member by scanning type declarations in the given file.
    ///     Returns the type name if exactly one type in the file has the requested member.
    /// </summary>
    private static async Task<string> InferContainingTypeFromFileAsync(
        Solution solution, string resolvedPath, string memberName,
        string? displayPath = null, CancellationToken ct = default)
    {
        Document? document = solution.GetDocumentByPath(resolvedPath);
        string label = displayPath ?? Path.GetFileName(resolvedPath);

        if (document is null)
        {
            throw new UserErrorException(ErrorMessages.FileNotInSolution(label));
        }

        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        SyntaxNode? root = await document.GetSyntaxRootAsync(ct);
        if (model is null || root is null)
        {
            throw new UserErrorException(ErrorMessages.CouldNotAnalyze(label));
        }

        List<INamedTypeSymbol> matchingTypes = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(decl => model.GetDeclaredSymbol(decl))
            .OfType<INamedTypeSymbol>()
            .Where(type => ExtractMembers(type, memberName).Any())
            .ToList();

        return matchingTypes.Count switch
        {
            0 => throw new UserErrorException(
                $"No '{memberName}' found in {label}."),
            1 => matchingTypes[0].Name,
            _ => throw new UserErrorException(
                $"Multiple types in {label} have '{memberName}': " +
                $"{String.Join(", ", matchingTypes.Select(t => t.Name))}. Specify containingType.")
        };
    }

    internal static IEnumerable<ISymbol> ExtractMembers(INamedTypeSymbol type, string memberName)
    {
        // .ctor: use InstanceConstructors which includes compiler-generated default constructors
        if (String.Equals(memberName, ".ctor", StringComparison.Ordinal))
        {
            return type.InstanceConstructors;
        }

        // this[]/this: indexers
        if (memberName is "this[]" or "this")
        {
            return type.GetMembers()
                .Where(m => m is IPropertySymbol { IsIndexer: true });
        }

        // .cctor, Finalize, and op_*: match by metadata name
        return type.GetMembers()
            .Where(m => m.IsUserVisibleMember()
                        && m is IMethodSymbol
                        && String.Equals(m.MetadataName, memberName, StringComparison.Ordinal));
    }
}
