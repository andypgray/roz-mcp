using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Extensions;

/// <summary>
///     Reusable filter extensions for excluding generated-file and test-project symbols.
/// </summary>
internal static class SymbolFilterExtensions
{
    /// <summary>
    ///     Filters out symbols declared in generated files. Returns input unchanged when
    ///     <paramref name="includeGenerated" /> is <c>true</c>.
    /// </summary>
    internal static IEnumerable<T> WhereNotGenerated<T>(this IEnumerable<T> symbols, bool includeGenerated = false)
        where T : ISymbol =>
        includeGenerated ? symbols : symbols.Where(s => !s.IsInGeneratedFile());

    /// <summary>
    ///     Partitions a list around a "is this in a test project?" predicate. When
    ///     <paramref name="excludeTests" /> is <c>true</c>, returns the items not in test projects
    ///     plus the count that were dropped; when <c>false</c>, returns the input unchanged plus
    ///     a count of how many test-project items are present. The two counts are mutually exclusive
    ///     by construction — only one is non-zero per call.
    /// </summary>
    /// <remarks>
    ///     Generic over <typeparamref name="T" /> so callers can supply <c>ReferenceLocation</c>,
    ///     <c>SymbolCallerInfo</c>, <see cref="ISymbol" />, or any other shape; the
    ///     <paramref name="isInTestProject" /> predicate adapts to each one.
    /// </remarks>
    internal static (List<T> Filtered, int ExcludedCount, int IncludedCount) PartitionByTestProject<T>(
        this List<T> items, bool excludeTests, Func<T, bool> isInTestProject)
    {
        if (!excludeTests)
        {
            return (items, 0, items.Count(isInTestProject));
        }

        List<T> filtered = items.Where(item => !isInTestProject(item)).ToList();
        return (filtered, items.Count - filtered.Count, 0);
    }

    /// <summary>
    ///     Filters out symbols from metadata (BCL/NuGet) and reports how many were excluded.
    ///     When <paramref name="excludeMetadata" /> is <c>false</c>, returns input unchanged with
    ///     <paramref name="excludedCount" /> set to 0.
    /// </summary>
    internal static List<T> ExcludeMetadataSymbols<T>(
        this List<T> symbols, bool excludeMetadata, out int excludedCount) where T : ISymbol
    {
        if (!excludeMetadata)
        {
            excludedCount = 0;
            return symbols;
        }

        List<T> filtered = symbols.Where(s => !s.IsMetadataSymbol()).ToList();
        excludedCount = symbols.Count - filtered.Count;
        return filtered;
    }
}
