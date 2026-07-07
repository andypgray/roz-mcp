using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Utility;

/// <summary>
///     Roslyn-aware fuzzy suggestion helpers for "did you mean..." error messages.
///     Delegates string matching to <see cref="FuzzyMatcher" />.
/// </summary>
internal static class FuzzySuggestionHelper
{
    /// <summary>
    ///     Searches projects for candidate symbols and returns fuzzy suggestions.
    /// </summary>
    internal static async Task<List<string>> GetSolutionSuggestionsAsync(
        string name, IEnumerable<Project> projects,
        SymbolicKind? kind, string? containingType, CancellationToken ct)
    {
        // Short names produce too many false-positive bigram matches to be useful
        if (name.Length < 3)
        {
            return [];
        }

        // Build search patterns from the name to find nearby candidates
        HashSet<string> patterns = new(StringComparer.OrdinalIgnoreCase) { name[..2] };
        if (name.Length >= 4)
        {
            patterns.Add(name[^2..]);
        }

        List<Project> projectList = projects.ToList();
        List<ISymbol>[] searchResults = await Task.WhenAll(
            patterns.Select(pattern => SymbolSearch.SearchProjectsAsync(projectList, pattern, ct)));

        List<string> distinctNames = SymbolDeduplication.DeduplicateByLocation(
                searchResults.SelectMany(r => r))
            .WhereNotGenerated()
            .Where(s => kind is null || s.MatchesKindFilter(kind))
            .Where(s => containingType is null ||
                        String.Equals(s.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FuzzyMatcher.GetSuggestions(name, distinctNames);
    }

    /// <summary>
    ///     Returns fuzzy suggestions from a resolved type's members.
    /// </summary>
    private static List<string> GetMemberSuggestions(string name, INamedTypeSymbol containingType)
    {
        List<string> memberNames = containingType
            .GetMembers()
            .Where(m => m.CanBeReferencedByName || SpecialMemberResolver.IsSpecialMemberName(m.Name))
            .Where(m => !m.IsImplicitlyDeclared)
            .Select(m => m.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FuzzyMatcher.GetSuggestions(name, memberNames);
    }

    /// <summary>
    ///     Resolves the containing type by name and returns fuzzy member suggestions.
    /// </summary>
    /// <remarks>Returns an empty list if the type can't be resolved.</remarks>
    private static async Task<List<string>> GetContainingTypeMemberSuggestionsAsync(
        IEnumerable<Project> projects, string memberName, string containingType, CancellationToken ct)
    {
        List<INamedTypeSymbol> typeMatches = await SymbolSearch.SearchSourceTypesAsync(projects, containingType, ct);
        INamedTypeSymbol? resolvedType = typeMatches.FirstOrDefault();

        return resolvedType is not null
            ? GetMemberSuggestions(memberName, resolvedType)
            : [];
    }

    /// <summary>
    ///     Formats a "Did you mean" suffix for error messages.
    /// </summary>
    internal static string FormatSuggestionSuffix(List<string> suggestions)
    {
        return suggestions.Count > 0
            ? $" Did you mean: {String.Join(", ", suggestions)}?"
            : "";
    }

    /// <summary>
    ///     Convenience wrapper: <see cref="GetSolutionSuggestionsAsync" /> + <see cref="FormatSuggestionSuffix" />.
    /// </summary>
    internal static async Task<string> SolutionSuggestionSuffixAsync(
        string name, IEnumerable<Project> projects,
        SymbolicKind? kind, string? containingType, CancellationToken ct)
    {
        List<string> suggestions = await GetSolutionSuggestionsAsync(name, projects, kind, containingType, ct);
        return FormatSuggestionSuffix(suggestions);
    }

    /// <summary>
    ///     Convenience wrapper: <see cref="GetMemberSuggestions" /> + <see cref="FormatSuggestionSuffix" />.
    /// </summary>
    internal static string MemberSuggestionSuffix(string name, INamedTypeSymbol containingType) =>
        FormatSuggestionSuffix(GetMemberSuggestions(name, containingType));

    /// <summary>
    ///     Convenience wrapper: <see cref="GetContainingTypeMemberSuggestionsAsync" /> + <see cref="FormatSuggestionSuffix" />
    ///     .
    /// </summary>
    internal static async Task<string> ContainingTypeMemberSuggestionSuffixAsync(
        IEnumerable<Project> projects, string memberName, string containingType, CancellationToken ct)
    {
        List<string> suggestions = await GetContainingTypeMemberSuggestionsAsync(projects, memberName, containingType, ct);
        return FormatSuggestionSuffix(suggestions);
    }
}
